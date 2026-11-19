using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Wave;
using VoicePilot.Core.Audio;

namespace VoicePilot.Audio.Windows;

/// <summary>Shared-mode microphone capture or endpoint-specific WASAPI loopback capture.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCaptureSource(AudioSourceKind kind) : IAudioCaptureSource
{
    private WasapiCapture? _capture;
    private TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;
    private AudioFormat? _format;
    private int _failureReported;
    private int _stopRequested;

    public AudioSourceKind Kind { get; } = kind is AudioSourceKind.Microphone or AudioSourceKind.Playback
        ? kind
        : throw new ArgumentOutOfRangeException(nameof(kind), "A physical capture source must be microphone or playback.");

    public event EventHandler<AudioFrameEventArgs>? FrameAvailable;

    public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    public async Task StartAsync(AudioDeviceInfo device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI 只能在 Windows 上运行。");
        }

        if (_capture is not null)
        {
            throw new InvalidOperationException("采集源已经启动。");
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice mmDevice = enumerator.GetDevice(device.Id);
            if (mmDevice.State != DeviceState.Active ||
                mmDevice.DataFlow != (Kind == AudioSourceKind.Microphone ? DataFlow.Capture : DataFlow.Render))
            {
                throw new InvalidOperationException("所选设备当前不可用，请重新选择。");
            }

            WasapiCapture capture = Kind == AudioSourceKind.Microphone
                ? new WasapiCapture(mmDevice)
                : new WasapiLoopbackCapture(mmDevice);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _stopRequested, 0);
            try
            {
                _format = ToAudioFormat(capture.WaveFormat);
                capture.StartRecording();
            }
            catch (Exception)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
                _capture = null;
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        WasapiCapture? capture = _capture;
        if (capture is null || Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        await Task.Run(capture.StopRecording).ConfigureAwait(false);
        Task completed = await Task.WhenAny(_stopped.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        if (completed != _stopped.Task)
        {
            CaptureFailed?.Invoke(this, new AudioCaptureFailedEventArgs(
                "WASAPI_STOP_TIMEOUT", "音频设备停止响应。请关闭相关音频应用，再重新启动 VoicePilot。", false));
        }
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
                {
                    _capture.StopRecording();
                }
            }
            catch (Exception)
            {
                // Disposal remains best effort after a disconnected device.
            }

            _capture.Dispose();
            _capture = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded <= 0 || _format is null)
            {
                return;
            }

            long timestamp = Stopwatch.GetTimestamp();
            var copy = GC.AllocateUninitializedArray<byte>(e.BytesRecorded);
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
            var frame = new PcmFrame(copy, _format, Kind, timestamp, Interlocked.Increment(ref _sequence));
            FrameAvailable?.Invoke(this, new AudioFrameEventArgs(frame));
        }
        catch (Exception)
        {
            CaptureFailed?.Invoke(this, new AudioCaptureFailedEventArgs(
                "WASAPI_CALLBACK_FAILED", "音频缓冲区处理失败，采集将停止。请重新开始。", false));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null && Interlocked.Exchange(ref _failureReported, 1) == 0)
        {
            CaptureFailed?.Invoke(this, new AudioCaptureFailedEventArgs(
                "WASAPI_DEVICE_INTERRUPTED",
                Kind == AudioSourceKind.Microphone
                    ? "麦克风采集已中断。请检查设备是否被拔出或禁用，然后重新选择。"
                    : "系统声音采集已中断。请检查播放设备，然后重新选择。",
                true));
        }

        _stopped.TrySetResult();
    }

    private static AudioFormat ToAudioFormat(WaveFormat waveFormat)
    {
        AudioSampleEncoding encoding = waveFormat.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => AudioSampleEncoding.IeeeFloat,
            WaveFormatEncoding.Extensible when waveFormat is WaveFormatExtensible extensible &&
                extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT => AudioSampleEncoding.IeeeFloat,
            WaveFormatEncoding.Pcm => AudioSampleEncoding.Pcm,
            WaveFormatEncoding.Extensible when waveFormat is WaveFormatExtensible extensible &&
                extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM => AudioSampleEncoding.Pcm,
            _ => throw new NotSupportedException("WASAPI 返回了不支持的压缩或非 PCM 音频格式。"),
        };
        return new AudioFormat(waveFormat.SampleRate, waveFormat.Channels, waveFormat.BitsPerSample, encoding);
    }
}
