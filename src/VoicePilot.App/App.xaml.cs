using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using VoicePilot.App.ViewModels;
using VoicePilot.Audio;
using VoicePilot.Audio.Windows;
using VoicePilot.Core.Audio;
using VoicePilot.Core.Configuration;
using VoicePilot.Core.Sessions;
using VoicePilot.Core.Speech;
using VoicePilot.Infrastructure.Configuration;
using VoicePilot.Providers;

namespace VoicePilot.App;

public partial class App : Application
{
    private readonly IHost _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAudioDeviceService, WindowsAudioDeviceService>();
                services.AddTransient<IAudioCaptureSession, AudioCaptureSession>();
                services.AddSingleton<ISpeechRecognitionProvider, UnconfiguredRecognitionProvider>();
                services.AddSingleton<ISpeechRecognitionProvider, SimulatedRecognitionProvider>();
                services.AddSingleton<ITranslationProvider, UnconfiguredTranslationProvider>();
                services.AddSingleton<ITranslationProvider, SimulatedTranslationProvider>();
                services.AddSingleton<IVoiceSession, VoiceSession>();
                services.AddSingleton<ISettingsStore, JsonSettingsStore>();
                services.AddSingleton<ILanguageCatalog, JsonLanguageCatalog>();
                services.AddSingleton<MainViewModel>();
            })
            .Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_host.Services.GetRequiredService<MainViewModel>());
        _window.Activate();
        _window.Closed += OnWindowClosed;
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        try
        {
            await _host.Services.GetRequiredService<MainViewModel>().ShutdownAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
        finally
        {
            Exit();
        }
    }
}
