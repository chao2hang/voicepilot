using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using VoicePilot.Core.Audio;
using VoicePilot.Core.Configuration;
using VoicePilot.Core.Sessions;
using VoicePilot.Core.Speech;

namespace VoicePilot.App.ViewModels;

public sealed record OptionItem<T>(T Value, string DisplayName);
public sealed record AudioDeviceOption(AudioDeviceInfo Device, bool IsAvailable = true)
{
    public string DisplayName => (IsAvailable ? string.Empty : "不可用 · ") + Device.Name + (Device.IsDefault ? "（默认）" : string.Empty);
    public string Id => Device.Id;
}
public sealed record ProviderOption(string Id, string DisplayName, string Description);

public sealed partial class TranscriptLine : ObservableObject
{
    public TranscriptLine(TranscriptEntry entry)
    {
        OriginalText = entry.OriginalText;
        Translation = entry.IsTranslationAvailable ? entry.TranslatedText : "未获得译文（服务未接入或本段翻译失败）";
        Timestamp = entry.CompletedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        SimulationBadge = entry.IsSimulated ? "模拟" : string.Empty;
    }

    public string OriginalText { get; }
    public string Translation { get; }
    public string Timestamp { get; }
    public string SimulationBadge { get; }
}

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaximumTranscriptLines = 500;
    private readonly IAudioDeviceService _devices;
    private readonly IVoiceSession _session;
    private readonly ISettingsStore _settingsStore;
    private readonly ILanguageCatalog _languageCatalog;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private AppSettings _settings = new();
    private bool _initialized;
    private bool _shuttingDown;

    public MainViewModel(
        IAudioDeviceService devices,
        IVoiceSession session,
        ISettingsStore settingsStore,
        ILanguageCatalog languageCatalog,
        IEnumerable<ISpeechRecognitionProvider> recognitionProviders,
        IEnumerable<ITranslationProvider> translationProviders)
    {
        _devices = devices;
        _session = session;
        _settingsStore = settingsStore;
        _languageCatalog = languageCatalog;
        _dispatcher = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("ViewModel 必须在 UI 线程创建。");
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateStatistics();
        SourceModes =
        [
            new(AudioSourceMode.Microphone, "麦克风"),
            new(AudioSourceMode.Playback, "系统声音 · 指定播放设备 Loopback"),
            new(AudioSourceMode.Mixed, "混合 · 麦克风 + 系统声音"),
        ];
        ProviderOptions = recognitionProviders.Select(provider =>
        {
            ITranslationProvider? translation = translationProviders.FirstOrDefault(item => item.Id == provider.Id);
            string description = provider.IsSimulated
                ? "仅验证传输与显示，所有文本均为模拟内容。"
                : provider.IsAvailable ? "已配置服务。" : "后端协议未接入；可测试采集，但不会生成真实识别或译文。";
            return new ProviderOption(provider.Id, provider.DisplayName, description + (translation is null ? " 无匹配翻译实现。" : string.Empty));
        }).ToArray();
        _selectedSourceMode = SourceModes[0];
        _selectedProvider = ProviderOptions.FirstOrDefault();
        _session.RecognitionUpdated += OnRecognitionUpdated;
        _session.TranscriptCompleted += OnTranscriptCompleted;
        _session.NotificationRaised += OnSessionNotification;
        _devices.DevicesChanged += OnDevicesChanged;
    }

    public IReadOnlyList<OptionItem<AudioSourceMode>> SourceModes { get; }
    public IReadOnlyList<ProviderOption> ProviderOptions { get; }
    public ObservableCollection<AudioDeviceOption> Microphones { get; } = [];
    public ObservableCollection<AudioDeviceOption> PlaybackDevices { get; } = [];
    public ObservableCollection<LanguageOption> SourceLanguages { get; } = [];
    public ObservableCollection<LanguageOption> TargetLanguages { get; } = [];
    public ObservableCollection<TranscriptLine> TranscriptLines { get; } = [];

    [ObservableProperty] private OptionItem<AudioSourceMode>? _selectedSourceMode;
    [ObservableProperty] private AudioDeviceOption? _selectedMicrophone;
    [ObservableProperty] private AudioDeviceOption? _selectedPlayback;
    [ObservableProperty] private LanguageOption? _selectedSourceLanguage;
    [ObservableProperty] private LanguageOption? _selectedTargetLanguage;
    [ObservableProperty] private ProviderOption? _selectedProvider;
    [ObservableProperty] private string _providerDescription = string.Empty;
    [ObservableProperty] private string _microphoneDeviceId = "未选择";
    [ObservableProperty] private string _playbackDeviceId = "未选择";
    [ObservableProperty] private double _microphoneGain = 0.8;
    [ObservableProperty] private double _playbackGain = 0.8;
    [ObservableProperty] private string _glossaryText = string.Empty;
    [ObservableProperty] private string _interimTranscript = "尚无临时识别结果";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusTitle = "就绪 · 未采集";
    [ObservableProperty] private SolidColorBrush _statusBrush = new(Color.FromArgb(255, 108, 117, 125));
    [ObservableProperty] private string _captureDuration = "00:00:00";
    [ObservableProperty] private string _queueStatus = "队列 0 KB · 丢弃 0";
    [ObservableProperty] private bool _hasNotification;
    [ObservableProperty] private InfoBarSeverity _notificationSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private string _notificationTitle = string.Empty;
    [ObservableProperty] private string _notificationMessage = string.Empty;

    public bool NeedsMicrophone => SelectedSourceMode?.Value is AudioSourceMode.Microphone or AudioSourceMode.Mixed;
    public bool NeedsPlayback => SelectedSourceMode?.Value is AudioSourceMode.Playback or AudioSourceMode.Mixed;
    public bool NeedsBothDevices => SelectedSourceMode?.Value == AudioSourceMode.Mixed;
    public bool IsStopped => !IsRunning && !IsBusy;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        IsBusy = true;
        try
        {
            _settings = await _settingsStore.LoadAsync(CancellationToken.None);
            GlossaryText = string.Join(Environment.NewLine, _settings.Glossary.Select(term => $"{term.Source}={term.Target}"));
            MicrophoneGain = _settings.MicrophoneGain;
            PlaybackGain = _settings.PlaybackGain;
            IReadOnlyList<LanguageOption> languages = await _languageCatalog.LoadAsync(CancellationToken.None);
            foreach (LanguageOption language in languages.Where(language => language.SupportsRecognition))
            {
                SourceLanguages.Add(language);
            }

            foreach (LanguageOption language in languages.Where(language => language.SupportsTranslation))
            {
                TargetLanguages.Add(language);
            }

            SelectedSourceLanguage = SourceLanguages.FirstOrDefault(item => item.Code == _settings.SourceLanguage) ?? SourceLanguages.FirstOrDefault();
            SelectedTargetLanguage = TargetLanguages.FirstOrDefault(item => item.Code == _settings.TargetLanguage) ?? TargetLanguages.FirstOrDefault();
            SelectedProvider = ProviderOptions.FirstOrDefault(item => item.Id == _settings.RecognitionProviderId) ?? ProviderOptions.FirstOrDefault();
            SelectedSourceMode = SourceModes.FirstOrDefault(item => item.Value == _settings.SourceMode) ?? SourceModes[0];
            _devices.StartMonitoring();
            await RefreshDevicesAsync();
            _timer.Start();
        }
        catch (Exception)
        {
            ShowNotification("初始化失败", "设备或设置初始化失败。请检查安装环境与 Configuration/languages.json，然后重启应用。", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        string? microphoneId = SelectedMicrophone?.Id ?? _settings.MicrophoneDeviceId;
        string? playbackId = SelectedPlayback?.Id ?? _settings.PlaybackDeviceId;
        string? microphoneName = SelectedMicrophone?.Device.Name;
        string? playbackName = SelectedPlayback?.Device.Name;
        try
        {
            var microphoneTask = Task.Run(() => _devices.GetDevices(AudioDeviceKind.Microphone));
            var playbackTask = Task.Run(() => _devices.GetDevices(AudioDeviceKind.Playback));
            await Task.WhenAll(microphoneTask, playbackTask);
            ReplaceDevices(Microphones, await microphoneTask, microphoneId, microphoneName, AudioDeviceKind.Microphone);
            ReplaceDevices(PlaybackDevices, await playbackTask, playbackId, playbackName, AudioDeviceKind.Playback);
            SelectedMicrophone = ChooseDevice(Microphones, microphoneId, microphoneName);
            SelectedPlayback = ChooseDevice(PlaybackDevices, playbackId, playbackName);
        }
        catch (Exception)
        {
            ShowNotification("设备枚举失败", "无法读取音频端点。请检查 Windows 音频服务与设备连接后重试。", InfoBarSeverity.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsBusy || IsRunning)
        {
            return;
        }

        if (NeedsMicrophone && SelectedMicrophone?.IsAvailable != true || NeedsPlayback && SelectedPlayback?.IsAvailable != true)
        {
            ShowNotification("设备不可用", "请为当前音源选择可用的设备。应用不会自动改用默认麦克风。", InfoBarSeverity.Warning);
            return;
        }

        IsBusy = true;
        StatusTitle = "正在启动所选设备…";
        try
        {
            ApplySettings();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            await _session.StartAsync(
                _settings,
                NeedsMicrophone ? SelectedMicrophone?.Device : null,
                NeedsPlayback ? SelectedPlayback?.Device : null,
                CancellationToken.None);
            IsRunning = true;
            StatusTitle = "正在采集 · 原始音频不保存";
            StatusBrush = new SolidColorBrush(Color.FromArgb(255, 15, 124, 79));
        }
        catch (Exception)
        {
            ShowNotification("无法开始", "启动失败。请确认所选设备仍处于启用状态，并关闭可能独占设备的应用后重试。", InfoBarSeverity.Error);
            StatusTitle = "启动失败 · 未采集";
            await _session.StopAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (IsBusy || !IsRunning)
        {
            return;
        }

        await StopCoreAsync();
        ShowNotification("停止操作结束", "停止流程已结束，原始音频未保存；请留意是否还有设备或服务错误通知。", InfoBarSeverity.Success);
        StatusTitle = "停止操作结束 · 请检查状态通知";
        StatusBrush = new SolidColorBrush(Color.FromArgb(255, 108, 117, 125));
    }

    [RelayCommand]
    private void ClearTranscript()
    {
        TranscriptLines.Clear();
        InterimTranscript = "尚无临时识别结果";
    }

    public async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _timer.Stop();
        if (IsRunning || _session.IsRunning)
        {
            await _session.StopAsync();
            IsRunning = false;
        }

        ApplySettings();
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        _devices.DevicesChanged -= OnDevicesChanged;
        _devices.Dispose();
        await _session.DisposeAsync();
    }

    private async Task StopCoreAsync()
    {
        IsBusy = true;
        StatusTitle = "正在停止采集…";
        try
        {
            await _session.StopAsync();
            IsRunning = false;
            InterimTranscript = "尚无临时识别结果";
        }
        finally
        {
            IsBusy = false;
            UpdateCommandStates();
        }
    }

    private bool CanStart() => IsStopped;
    private bool CanStop() => IsRunning && !IsBusy;

    private void ApplySettings()
    {
        _settings.SourceMode = SelectedSourceMode?.Value ?? AudioSourceMode.Microphone;
        _settings.MicrophoneDeviceId = SelectedMicrophone?.Id;
        _settings.PlaybackDeviceId = SelectedPlayback?.Id;
        _settings.SourceLanguage = SelectedSourceLanguage?.Code ?? "auto";
        _settings.TargetLanguage = SelectedTargetLanguage?.Code ?? "en";
        _settings.RecognitionProviderId = SelectedProvider?.Id ?? "unconfigured";
        _settings.TranslationProviderId = SelectedProvider?.Id ?? "unconfigured";
        _settings.MicrophoneGain = MicrophoneGain;
        _settings.PlaybackGain = PlaybackGain;
        _settings.Glossary = GlossaryText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            .Select(parts => new GlossaryTerm(parts[0], parts[1]))
            .ToList();
    }

    private void OnRecognitionUpdated(object? sender, RecognitionUpdate update) => _dispatcher.TryEnqueue(() =>
    {
        InterimTranscript = update.IsFinal ? string.Empty : update.Text;
    });

    private void OnTranscriptCompleted(object? sender, TranscriptEntry entry) => _dispatcher.TryEnqueue(() =>
    {
        TranscriptLines.Add(new TranscriptLine(entry));
        while (TranscriptLines.Count > MaximumTranscriptLines)
        {
            TranscriptLines.RemoveAt(0);
        }

    });


    private void OnSessionNotification(object? sender, SessionNotification notification) => _dispatcher.TryEnqueue(async () =>
    {
        ShowNotification(
            notification.Severity == global::VoicePilot.Core.Sessions.NotificationSeverity.Error ? "会话错误" : "会话状态",
            notification.Message,
            notification.Severity switch
            {
                global::VoicePilot.Core.Sessions.NotificationSeverity.Error => InfoBarSeverity.Error,
                global::VoicePilot.Core.Sessions.NotificationSeverity.Warning => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            });
        if (notification.Severity == global::VoicePilot.Core.Sessions.NotificationSeverity.Error &&
            notification.Code is not ("TRANSLATION_FAILED" or "CAPTURE_STOP_FAILED" or "CAPTURE_STOP_TIMEOUT" or "PROVIDER_STOP_TIMEOUT") &&
            IsRunning && !_shuttingDown)
        {
            await StopCoreAsync();
            StatusTitle = "已停止 · 请检查设备或服务";
            StatusBrush = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28));
        }
    });

    private void OnDevicesChanged(object? sender, EventArgs args) => _dispatcher.TryEnqueue(async () =>
    {
        if (!IsRunning && !IsBusy)
        {
            await RefreshDevicesAsync();
        }
    });

    private void UpdateStatistics()
    {
        SessionStatistics statistics = _session.Statistics;
        CaptureDuration = statistics.Capture.CapturedDuration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        long dropped = statistics.Capture.DroppedFrames + statistics.RecognitionQueueDroppedFrames;
        string latency = statistics.LatestAudioAgeAtRecognition is { } value ? $" · 识别时音频年龄 {value.TotalMilliseconds:F0} ms" : string.Empty;
        QueueStatus = $"队列 {statistics.RecognitionQueueBytes / 1024} KB · 丢弃 {dropped}{latency}";
    }

    private static void ReplaceDevices(
        ObservableCollection<AudioDeviceOption> target,
        IReadOnlyList<AudioDeviceInfo> devices,
        string? previousId,
        string? previousName,
        AudioDeviceKind kind)
    {
        target.Clear();
        foreach (AudioDeviceInfo device in devices)
        {
            target.Add(new AudioDeviceOption(device));
        }

        if (previousId is not null && devices.All(device => !string.Equals(device.Id, previousId, StringComparison.OrdinalIgnoreCase)))
        {
            target.Add(new AudioDeviceOption(new AudioDeviceInfo(previousId, previousName ?? "已断开的设备", kind, false), false));
        }
    }

    private static AudioDeviceOption? ChooseDevice(ObservableCollection<AudioDeviceOption> devices, string? previousId, string? previousName)
    {
        if (previousId is not null)
        {
            return devices.FirstOrDefault(device => string.Equals(device.Id, previousId, StringComparison.OrdinalIgnoreCase))
                ?? devices.FirstOrDefault(device => !device.IsAvailable && device.Device.Name == previousName);
        }

        return devices.FirstOrDefault(device => device.Device.IsDefault) ?? devices.FirstOrDefault(device => device.IsAvailable);
    }

    private void ShowNotification(string title, string message, InfoBarSeverity severity)
    {
        NotificationTitle = title;
        NotificationMessage = message;
        NotificationSeverity = severity;
        HasNotification = true;
    }

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(IsStopped));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RefreshDevicesCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSourceModeChanged(OptionItem<AudioSourceMode>? value)
    {
        OnPropertyChanged(nameof(NeedsMicrophone));
        OnPropertyChanged(nameof(NeedsPlayback));
        OnPropertyChanged(nameof(NeedsBothDevices));
    }

    partial void OnSelectedMicrophoneChanged(AudioDeviceOption? value) => MicrophoneDeviceId = value?.Id ?? "未选择";
    partial void OnSelectedPlaybackChanged(AudioDeviceOption? value) => PlaybackDeviceId = value?.Id ?? "未选择";
    partial void OnSelectedProviderChanged(ProviderOption? value) => ProviderDescription = value?.Description ?? string.Empty;
    partial void OnIsRunningChanged(bool value) => UpdateCommandStates();
    partial void OnIsBusyChanged(bool value) => UpdateCommandStates();
}
