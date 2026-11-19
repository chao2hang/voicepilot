using System.Runtime.CompilerServices;
using VoicePilot.Core.Audio;
using VoicePilot.Core.Speech;

namespace VoicePilot.Providers;

/// <summary>Development-only transport test. Its output is intentionally and visibly not real recognition.</summary>
public sealed class SimulatedRecognitionProvider : ISpeechRecognitionProvider
{
    public string Id => "simulation";

    public string DisplayName => "开发联调（模拟，非真实识别）";

    public bool IsAvailable => true;

    public bool IsSimulated => true;

    public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<PcmFrame> audio,
        RecognitionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long frames = 0;
        bool emittedFinal = false;
        await foreach (PcmFrame frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            frames++;
            if (frames == 25)
            {
                yield return new RecognitionUpdate(0, "【联调模拟】正在接收音频；这不是语音识别结果。", false, frame.Duration * frames);
            }

            if (frames == 100)
            {
                emittedFinal = true;
                yield return new RecognitionUpdate(0,
                    "【联调模拟】已接收 2 秒音频；当前内容仅用于验证显示链路，不是实际识别。",
                    true,
                    frame.Duration * frames);
            }
        }

        if (frames > 0 && !emittedFinal)
        {
            yield return new RecognitionUpdate(0,
                $"【联调模拟】会话结束，共接收 {frames * 0.02:F1} 秒音频；未执行真实识别。",
                true,
                TimeSpan.FromMilliseconds(frames * 20));
        }
    }
}

public sealed class SimulatedTranslationProvider : ITranslationProvider
{
    public string Id => "simulation";

    public string DisplayName => "开发联调（模拟，非真实翻译）";

    public bool IsAvailable => true;

    public bool IsSimulated => true;

    public async Task<TranslationResult> TranslateAsync(
        RecognitionUpdate recognition,
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        return new TranslationResult(recognition.SequenceNumber, "【模拟译文】仅验证界面与传输链路，未执行实际翻译。");
    }
}
