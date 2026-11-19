using System.Diagnostics;
using System.Runtime.CompilerServices;
using VoicePilot.Core.Audio;
using VoicePilot.Core.Sessions;
using VoicePilot.Core.Speech;

namespace VoicePilot.Providers;

public sealed class UnconfiguredRecognitionProvider : ISpeechRecognitionProvider
{
    public string Id => "unconfigured";

    public string DisplayName => "未接入服务（仅采集音频）";

    public bool IsAvailable => false;

    public bool IsSimulated => false;

    public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<PcmFrame> audio,
        RecognitionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (PcmFrame _ in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Deliberately drain the bounded stream without persisting audio or inventing recognition text.
        }

        yield break;
    }
}

public sealed class UnconfiguredTranslationProvider : ITranslationProvider
{
    public string Id => "unconfigured";

    public string DisplayName => "未接入翻译服务";

    public bool IsAvailable => false;

    public bool IsSimulated => false;

    public Task<TranslationResult> TranslateAsync(
        RecognitionUpdate recognition,
        TranslationRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new TranslationResult(recognition.SequenceNumber, string.Empty));
}
