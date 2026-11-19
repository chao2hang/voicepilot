using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoicePilot.Core.Audio;

namespace VoicePilot.Audio.Processing;

/// <summary>Persistent per-endpoint conversion to mono, 16 kHz, signed PCM16 in exact 20 ms chunks.</summary>
public sealed class PcmAudioConverter
{
    private const int ChunkBytes = 640;
    private readonly AudioFormat _sourceFormat;
    private readonly BufferedWaveProvider _input;
    private readonly SampleToWaveProvider16 _output;
    private readonly byte[] _readBuffer = new byte[16_000];
    private readonly byte[] _chunk = new byte[ChunkBytes];
    private int _chunkBytes;
    private long _nextTimestamp;
    private long _expectedInputTimestamp;
    private bool _hasTimestamp;
    private bool _disposed;

    public PcmAudioConverter(AudioFormat sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        _sourceFormat = sourceFormat;
        WaveFormat waveFormat = sourceFormat.Encoding == AudioSampleEncoding.IeeeFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(sourceFormat.SampleRate, sourceFormat.Channels)
            : new WaveFormat(sourceFormat.SampleRate, sourceFormat.BitsPerSample, sourceFormat.Channels);
        _input = new BufferedWaveProvider(waveFormat)
        {
            BufferLength = Math.Max(waveFormat.AverageBytesPerSecond * 2, ChunkBytes * 4),
            ReadFully = false,
            DiscardOnBufferOverflow = false,
        };
        ISampleProvider samples = _input.ToSampleProvider();
        if (sourceFormat.Channels > 1)
        {
            samples = new DownmixToMonoSampleProvider(samples);
        }

        var resampled = new WdlResamplingSampleProvider(samples, AudioFormat.RecognitionPcm16.SampleRate);
        _output = new SampleToWaveProvider16(resampled);
    }

    public void Convert(PcmFrame frame, Action<PcmFrame> emit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(emit);
        if (frame.Format != _sourceFormat)
        {
            throw new NotSupportedException("采集中途的音频格式发生变化；请重新启动采集。");
        }

        if (!_hasTimestamp)
        {
            _nextTimestamp = frame.Timestamp;
            _hasTimestamp = true;
        }
        else
        {
            long inputDrift = frame.Timestamp - _expectedInputTimestamp;
            long threshold = MonotonicTime.Add(0, TimeSpan.FromMilliseconds(40));
            if (Math.Abs(inputDrift) > threshold)
            {
                FlushPartialFrame(frame.Source, emit);
                // Preserve known discontinuities instead of concealing a dropped/gapped raw packet.
                _nextTimestamp = frame.Timestamp;
            }
        }

        _expectedInputTimestamp = MonotonicTime.Add(frame.Timestamp, frame.Duration);
        byte[] input = frame.Data.ToArray();
        _input.AddSamples(input, 0, input.Length);
        int read;
        while ((read = _output.Read(_readBuffer, 0, _readBuffer.Length)) > 0)
        {
            Append(_readBuffer.AsSpan(0, read), frame.Source, emit);
        }
    }

    private void Append(ReadOnlySpan<byte> bytes, AudioSourceKind source, Action<PcmFrame> emit)
    {
        while (!bytes.IsEmpty)
        {
            int count = Math.Min(bytes.Length, ChunkBytes - _chunkBytes);
            bytes[..count].CopyTo(_chunk.AsSpan(_chunkBytes));
            _chunkBytes += count;
            bytes = bytes[count..];
            if (_chunkBytes != ChunkBytes)
            {
                continue;
            }

            var data = _chunk.ToArray();
            var result = new PcmFrame(data, AudioFormat.RecognitionPcm16, source, _nextTimestamp);
            _nextTimestamp = MonotonicTime.Add(_nextTimestamp, result.Duration);
            _chunkBytes = 0;
            emit(result);
        }
    }

    private void FlushPartialFrame(AudioSourceKind source, Action<PcmFrame> emit)
    {
        if (_chunkBytes == 0)
        {
            return;
        }

        _chunk.AsSpan(_chunkBytes).Clear();
        var result = new PcmFrame(_chunk.ToArray(), AudioFormat.RecognitionPcm16, source, _nextTimestamp);
        _nextTimestamp = MonotonicTime.Add(_nextTimestamp, result.Duration);
        _chunkBytes = 0;
        emit(result);
    }

    public void Dispose() => _disposed = true;

    private sealed class DownmixToMonoSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private float[] _interleaved = new float[2048];

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = source.WaveFormat.Channels;
            int frames = count;
            int required = checked(frames * channels);
            if (_interleaved.Length < required)
            {
                _interleaved = new float[required];
            }

            int read = source.Read(_interleaved, 0, required);
            int framesRead = read / channels;
            for (int frame = 0; frame < framesRead; frame++)
            {
                double sum = 0;
                int start = frame * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    sum += _interleaved[start + channel];
                }

                buffer[offset + frame] = (float)(sum / channels);
            }

            return framesRead;
        }
    }
}
