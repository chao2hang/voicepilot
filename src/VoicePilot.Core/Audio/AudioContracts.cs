namespace VoicePilot.Core.Audio;

public interface IAudioDeviceService : IDisposable
{
    event EventHandler? DevicesChanged;

    IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind);

    AudioDeviceInfo? FindDevice(string id, AudioDeviceKind kind);

    void StartMonitoring();
}

public interface IAudioCaptureSource : IDisposable
{
    AudioSourceKind Kind { get; }

    event EventHandler<AudioFrameEventArgs>? FrameAvailable;

    event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    Task StartAsync(AudioDeviceInfo device, CancellationToken cancellationToken);

    Task StopAsync();
}

public interface IAudioCaptureSession : IAsyncDisposable
{
    CaptureState State { get; }

    CaptureStatistics Statistics { get; }

    event EventHandler<AudioFrameEventArgs>? FrameAvailable;

    event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken);

    Task StopAsync();
}

public sealed class AudioFrameEventArgs(PcmFrame frame) : EventArgs
{
    public PcmFrame Frame { get; } = frame;
}

public sealed class CaptureStateChangedEventArgs(CaptureState state) : EventArgs
{
    public CaptureState State { get; } = state;
}

public sealed class AudioCaptureFailedEventArgs(string code, string userMessage, bool deviceLost) : EventArgs
{
    public string Code { get; } = code;

    public string UserMessage { get; } = userMessage;

    public bool DeviceLost { get; } = deviceLost;
}
