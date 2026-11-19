using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoicePilot.Core.Audio;

namespace VoicePilot.Audio.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioDeviceService : IAudioDeviceService
{
    private readonly Lock _sync = new();
    private MMDeviceEnumerator? _monitorEnumerator;
    private IMMNotificationClient? _notificationClient;
    private Timer? _debounceTimer;
    private bool _disposed;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI 设备枚举只能在 Windows 上运行。");
        }

        var flow = kind == AudioDeviceKind.Microphone ? DataFlow.Capture : DataFlow.Render;
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? defaultDevice = null;
        try
        {
            defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
        }
        catch (Exception)
        {
            // No active default endpoint is valid; explicit non-default devices can still be listed.
        }

        try
        {
            var devices = new List<AudioDeviceInfo>();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDeviceInfo(
                        device.ID,
                        device.FriendlyName,
                        kind,
                        string.Equals(device.ID, defaultDevice?.ID, StringComparison.OrdinalIgnoreCase)));
                }
            }

            return devices.OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            defaultDevice?.Dispose();
        }
    }

    public AudioDeviceInfo? FindDevice(string id, AudioDeviceKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return GetDevices(kind).FirstOrDefault(device => string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public void StartMonitoring()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI 设备监听只能在 Windows 上运行。");
        }

        lock (_sync)
        {
            if (_monitorEnumerator is not null)
            {
                return;
            }

            _debounceTimer = new Timer(_ => DevicesChanged?.Invoke(this, EventArgs.Empty), null,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _monitorEnumerator = new MMDeviceEnumerator();
            _notificationClient = new DeviceNotificationClient(ScheduleRefresh);
            _monitorEnumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _monitorEnumerator?.UnregisterEndpointNotificationCallback(_notificationClient);
            _monitorEnumerator?.Dispose();
            _debounceTimer?.Dispose();
        }
    }

    private void ScheduleRefresh()
    {
        if (!_disposed)
        {
            _debounceTimer?.Change(TimeSpan.FromMilliseconds(150), Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class DeviceNotificationClient(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string deviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => changed();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) => changed();
    }
}
