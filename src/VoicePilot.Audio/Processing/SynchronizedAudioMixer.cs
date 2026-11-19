using System.Buffers.Binary;
using VoicePilot.Core.Audio;

namespace VoicePilot.Audio.Processing;

/// <summary>
/// Sample-aligned mono PCM16 mixer driven by a monotonic clock. Each call emits only one due 20 ms frame;
/// a missing stream gets silence after a bounded wait. No unbounded drain or free-running output is allowed.
/// </summary>
public sealed class SynchronizedAudioMixer
{
    private const int SamplesPerFrame = 320;
    private const int BytesPerFrame = SamplesPerFrame * 2;
    private static readonly TimeSpan StartupWait = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan MissingStreamWait = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan MaximumBufferedAudio = TimeSpan.FromMilliseconds(500);
    private readonly Queue<PcmFrame> _microphone = new();
    private readonly Queue<PcmFrame> _playback = new();
    private readonly Lock _sync = new();
    private long _timeline;
    private long _lateFrames;
    private long _startedAt;
    private bool _hasStart;

    public long LateFrames
    {
        get
        {
            lock (_sync)
            {
                return _lateFrames;
            }
        }
    }

    public void Offer(PcmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Format != AudioFormat.RecognitionPcm16 || frame.Duration != TimeSpan.FromMilliseconds(20))
        {
            throw new ArgumentException("Mixer input must be 20 ms, 16 kHz, mono PCM16.", nameof(frame));
        }

        lock (_sync)
        {
            if (!_hasStart)
            {
                _hasStart = true;
                _startedAt = frame.Timestamp;
            }

            Queue<PcmFrame> queue = GetQueue(frame.Source);
            queue.Enqueue(frame);
            int maximumFrames = (int)(MaximumBufferedAudio / frame.Duration);
            while (queue.Count > maximumFrames)
            {
                queue.Dequeue();
                _lateFrames++;
            }
        }
    }

    public bool TryMix(long now, out PcmFrame? mixed, double microphoneGain = 0.8, double playbackGain = 0.8)
    {
        lock (_sync)
        {
            mixed = null;
            if (!_hasStart)
            {
                return false;
            }

            if (_timeline == 0)
            {
                if (now - _startedAt < ToTimestampTicks(StartupWait))
                {
                    return false;
                }

                _timeline = Math.Min(
                    _microphone.Count > 0 ? _microphone.Peek().Timestamp : long.MaxValue,
                    _playback.Count > 0 ? _playback.Peek().Timestamp : long.MaxValue);
            }

            if (now < _timeline)
            {
                return false;
            }

            long end = MonotonicTime.Add(_timeline, TimeSpan.FromMilliseconds(20));
            DropStale(_microphone);
            DropStale(_playback);
            if (_microphone.Count == 0 && _playback.Count == 0)
            {
                return false;
            }

            if (now - _timeline > ToTimestampTicks(MaximumBufferedAudio))
            {
                // A long suspension or discontinuity must not replay seconds of obsolete silence.
                _timeline = Math.Min(
                    _microphone.Count > 0 ? _microphone.Peek().Timestamp : long.MaxValue,
                    _playback.Count > 0 ? _playback.Peek().Timestamp : long.MaxValue);
                end = MonotonicTime.Add(_timeline, TimeSpan.FromMilliseconds(20));
            }

            if (now < _timeline)
            {
                return false;
            }

            if (ShouldWait(_microphone, now, end) || ShouldWait(_playback, now, end))
            {
                return false;
            }

            var output = new byte[BytesPerFrame];
            MixFrames(_microphone, _playback, microphoneGain, playbackGain, output);
            var timestamp = _timeline;
            mixed = new PcmFrame(output, AudioFormat.RecognitionPcm16, AudioSourceKind.Mixed, timestamp);
            _timeline = end;
            return true;
        }
    }

    public bool TryMix(out PcmFrame? mixed, double microphoneGain = 0.8, double playbackGain = 0.8) =>
        TryMix(System.Diagnostics.Stopwatch.GetTimestamp(), out mixed, microphoneGain, playbackGain);

    private Queue<PcmFrame> GetQueue(AudioSourceKind source) => source switch
    {
        AudioSourceKind.Microphone => _microphone,
        AudioSourceKind.Playback => _playback,
        _ => throw new ArgumentOutOfRangeException(nameof(source), "Mixer accepts only microphone and playback inputs."),
    };

    private bool ShouldWait(Queue<PcmFrame> queue, long now, long end)
    {
        if (queue.Count > 0)
        {
            return false;
        }

        bool otherStreamHasAudio = (ReferenceEquals(queue, _microphone) ? _playback : _microphone).Count > 0;
        return otherStreamHasAudio && now < MonotonicTime.Add(end, MissingStreamWait);
    }

    private void DropStale(Queue<PcmFrame> queue)
    {
        while (queue.Count > 0 && MonotonicTime.Add(queue.Peek().Timestamp, queue.Peek().Duration) <= _timeline)
        {
            queue.Dequeue();
            _lateFrames++;
        }
    }

    private void MixFrames(
        Queue<PcmFrame> microphone,
        Queue<PcmFrame> playback,
        double microphoneGain,
        double playbackGain,
        Span<byte> output)
    {
        long frameTicks = ToTimestampTicks(TimeSpan.FromMilliseconds(20));
        for (int index = 0; index < SamplesPerFrame; index++)
        {
            long sampleTimestamp = _timeline + index * frameTicks / SamplesPerFrame;
            float microphoneSample = SampleAt(microphone, sampleTimestamp) * (float)microphoneGain;
            float playbackSample = SampleAt(playback, sampleTimestamp) * (float)playbackGain;
            float sample = Math.Clamp(microphoneSample + playbackSample, -1f, 1f);
            short pcm = (short)Math.Clamp(
                (int)MathF.Round(sample * short.MaxValue, MidpointRounding.AwayFromZero),
                short.MinValue,
                short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.Slice(index * 2, 2), pcm);
        }

        ConsumeCompletedFrame(microphone);
        ConsumeCompletedFrame(playback);
    }

    private void ConsumeCompletedFrame(Queue<PcmFrame> queue)
    {
        if (queue.Count > 0 && queue.Peek().Timestamp + ToTimestampTicks(queue.Peek().Duration) <= _timeline + ToTimestampTicks(TimeSpan.FromMilliseconds(20)))
        {
            queue.Dequeue();
        }
    }

    private static float SampleAt(Queue<PcmFrame> queue, long sampleTimestamp)
    {
        if (queue.Count == 0)
        {
            return 0;
        }

        PcmFrame frame = queue.Peek();
        int sampleIndex = (int)((sampleTimestamp - frame.Timestamp) * SamplesPerFrame / ToTimestampTicks(frame.Duration));
        if (sampleIndex is < 0 or >= SamplesPerFrame)
        {
            return 0;
        }

        return BinaryPrimitives.ReadInt16LittleEndian(queue.Peek().Data.Span.Slice(sampleIndex * 2, 2)) / 32768f;
    }

    private static long ToTimestampTicks(TimeSpan duration) =>
        (long)Math.Round(duration.TotalSeconds * System.Diagnostics.Stopwatch.Frequency, MidpointRounding.AwayFromZero);
}
