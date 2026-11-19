namespace VoicePilot.Core.Configuration;

using VoicePilot.Core.Audio;
using VoicePilot.Core.Speech;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public AudioSourceMode SourceMode { get; set; } = AudioSourceMode.Microphone;

    public string? MicrophoneDeviceId { get; set; }

    public string? PlaybackDeviceId { get; set; }

    public double MicrophoneGain { get; set; } = 0.8;

    public double PlaybackGain { get; set; } = 0.8;

    public string SourceLanguage { get; set; } = "auto";

    public string TargetLanguage { get; set; } = "en";

    public string RecognitionProviderId { get; set; } = "unconfigured";

    public string TranslationProviderId { get; set; } = "unconfigured";

    public List<GlossaryTerm> Glossary { get; set; } = [];

    // Authentication material and remote endpoints intentionally have no settings fields until a protocol is selected.
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public sealed record LanguageOption(string Code, string DisplayName, bool SupportsRecognition, bool SupportsTranslation);

public interface ILanguageCatalog
{
    Task<IReadOnlyList<LanguageOption>> LoadAsync(CancellationToken cancellationToken);
}
