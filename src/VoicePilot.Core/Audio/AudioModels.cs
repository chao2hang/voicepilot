namespace VoicePilot.Core.Audio;

public enum AudioSourceMode
{
    Microphone,
    Playback,
    Mixed,
}

public enum AudioDeviceKind
{
    Microphone,
    Playback,
}

public enum AudioSampleEncoding
{
    Pcm,
    IeeeFloat,
}

/// <summary>An immutable description of uncompressed interleaved audio.</summary>
public sealed record AudioFormat
{
    public AudioFormat(int sampleRate, int channels, int bitsPerSample, AudioSampleEncoding encoding)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        if (bitsPerSample is not (16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Only 16, 24 and 32 bit samples are supported.");
        }

        if (encoding == AudioSampleEncoding.IeeeFloat && bitsPerSample != 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "IEEE Float capture must use 32-bit samples.");
        }

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        Encoding = encoding;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BitsPerSample { get; }

    public AudioSampleEncoding Encoding { get; }

    public int BlockAlign => Channels * BitsPerSample / 8;

    public int BytesPerSecond => SampleRate * BlockAlign;

    public static AudioFormat RecognitionPcm16 { get; } = new(16_000, 1, 16, AudioSampleEncoding.Pcm);
}

public sealed class PcmFrame
{
    public PcmFrame(
        ReadOnlyMemory<byte> data,
        AudioFormat format,
        AudioSourceKind source,
        long timestamp,
        long sequenceNumber = 0)
    {
        if (data.Length == 0 || data.Length % format.BlockAlign != 0)
        {
            throw new ArgumentException("Audio data must contain whole, non-empty sample frames.", nameof(data));
        }

        Data = data;
        Format = format ?? throw new ArgumentNullException(nameof(format));
        Source = source;
        Timestamp = timestamp;
        SequenceNumber = sequenceNumber;
    }

    public ReadOnlyMemory<byte> Data { get; }

    public AudioFormat Format { get; }

    public AudioSourceKind Source { get; }

    /// <summary>A <see cref="System.Diagnostics.Stopwatch"/> timestamp captured before processing.</summary>
    public long Timestamp { get; }

    public long SequenceNumber { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Data.Length / Format.BytesPerSecond);
}

public enum AudioSourceKind
{
    Microphone,
    Playback,
    Mixed,
}

public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault);

public sealed record CaptureSettings(
    AudioSourceMode Mode,
    AudioDeviceInfo? Microphone,
    AudioDeviceInfo? Playback,
    double MicrophoneGain = 0.8,
    double PlaybackGain = 0.8)
{
    public void Validate()
    {
        if (Mode is AudioSourceMode.Microphone or AudioSourceMode.Mixed)
        {
            if (Microphone is null || Microphone.Kind != AudioDeviceKind.Microphone)
            {
                throw new InvalidOperationException("请选择可用的麦克风。");
            }
        }

        if (Mode is AudioSourceMode.Playback or AudioSourceMode.Mixed)
        {
            if (Playback is null || Playback.Kind != AudioDeviceKind.Playback)
            {
                throw new InvalidOperationException("请选择用于 Loopback 采集的播放设备。");
            }
        }

        if (!double.IsFinite(MicrophoneGain) || MicrophoneGain is < 0 or > 1 ||
            !double.IsFinite(PlaybackGain) || PlaybackGain is < 0 or > 1)
        {
            throw new InvalidOperationException("采集增益必须在 0 到 1 之间。");
        }
    }
}

public enum CaptureState
{
    Idle,
    Starting,
    Capturing,
    Stopping,
    DeviceLost,
    Error,
}

public sealed record CaptureStatistics(
    long CapturedFrames,
    long DroppedFrames,
    long LateFrames,
    TimeSpan BufferedDuration,
    TimeSpan CapturedDuration);
