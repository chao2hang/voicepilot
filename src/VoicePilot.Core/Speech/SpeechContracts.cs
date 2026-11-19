using VoicePilot.Core.Audio;

namespace VoicePilot.Core.Speech;

public sealed record RecognitionRequest(
    string SourceLanguage,
    AudioFormat Format,
    IReadOnlyList<GlossaryTerm> Terms,
    IReadOnlyDictionary<string, string> ProviderOptions)
{
    public static RecognitionRequest CreateDefault(string sourceLanguage, IReadOnlyList<GlossaryTerm> terms) =>
        new(sourceLanguage, AudioFormat.RecognitionPcm16, terms, new Dictionary<string, string>());
}

public sealed record RecognitionUpdate(
    long SequenceNumber,
    string Text,
    bool IsFinal,
    TimeSpan AudioPosition);

public sealed record TranslationRequest(
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<GlossaryTerm> Terms,
    IReadOnlyDictionary<string, string> ProviderOptions);

public sealed record TranslationResult(long SequenceNumber, string Text);

public sealed record GlossaryTerm(string Source, string Target);

public interface ISpeechRecognitionProvider
{
    string Id { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    bool IsSimulated { get; }

    /// <summary>
    /// Converts a bounded, cancellable PCM stream to recognition updates. Implementations must not block the audio callback.
    /// </summary>
    IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<PcmFrame> audio,
        RecognitionRequest request,
        CancellationToken cancellationToken);
}

public interface ITranslationProvider
{
    string Id { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    bool IsSimulated { get; }

    Task<TranslationResult> TranslateAsync(
        RecognitionUpdate recognition,
        TranslationRequest request,
        CancellationToken cancellationToken);
}
