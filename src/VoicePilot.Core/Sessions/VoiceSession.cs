using System.Diagnostics;
using VoicePilot.Core.Audio;
using VoicePilot.Core.Configuration;
using VoicePilot.Core.Sessions;
using VoicePilot.Core.Speech;

namespace VoicePilot.Core.Sessions;

/// <summary>Coordinates bounded capture, replaceable recognition and sequential final-result translation.</summary>
public sealed class VoiceSession : IVoiceSession, IDisposable
{
    private readonly IAudioCaptureSession _capture;
    private readonly IReadOnlyDictionary<string, ISpeechRecognitionProvider> _recognitionProviders;
    private readonly IReadOnlyDictionary<string, ITranslationProvider> _translationProviders;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private BoundedAudioBuffer? _input;
    private Task? _pipeline;
    private ISpeechRecognitionProvider? _activeRecognition;
    private ITranslationProvider? _activeTranslation;
    private long _latestFrameTimestamp;
    private long _sequence;
    private long _latestAudioAgeAtRecognitionMs = -1;
    private long _lastTranslationLatencyMs = -1;
    private bool _disposed;
    private bool _requiresRestart;

    public VoiceSession(
        IAudioCaptureSession capture,
        IEnumerable<ISpeechRecognitionProvider> recognitionProviders,
        IEnumerable<ITranslationProvider> translationProviders)
    {
        _capture = capture;
        _recognitionProviders = recognitionProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _translationProviders = translationProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsRunning { get; private set; }

    public SessionStatistics Statistics => new(
        _capture.Statistics,
        _input?.DroppedFrames ?? 0,
        _input?.PendingBytes ?? 0,
        ToNullableLatency(Interlocked.Read(ref _latestAudioAgeAtRecognitionMs)),
        ToNullableLatency(Interlocked.Read(ref _lastTranslationLatencyMs)));

    public event EventHandler<RecognitionUpdate>? RecognitionUpdated;

    public event EventHandler<TranscriptEntry>? TranscriptCompleted;

    public event EventHandler<SessionNotification>? NotificationRaised;

    public async Task StartAsync(
        AppSettings settings,
        AudioDeviceInfo? microphone,
        AudioDeviceInfo? playback,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning || _requiresRestart || _pipeline is { IsCompleted: false })
            {
                throw new InvalidOperationException("翻译会话已经启动，或上一个识别服务尚未安全退出。");
            }

            if (_pipeline is not null || _input is not null)
            {
                ResetPipeline();
            }

            _activeRecognition = FindProvider(settings.RecognitionProviderId, _recognitionProviders.Values,
                new UnconfiguredFallbackRecognition());
            _activeTranslation = FindProvider(settings.TranslationProviderId, _translationProviders.Values,
                new UnconfiguredFallbackTranslation());
            _cancellation = new CancellationTokenSource();
            _input = new BoundedAudioBuffer(capacityBytes: 128_000);
            _latestFrameTimestamp = 0;
            Interlocked.Exchange(ref _sequence, 0);
            Interlocked.Exchange(ref _latestAudioAgeAtRecognitionMs, -1);
            Interlocked.Exchange(ref _lastTranslationLatencyMs, -1);
            _capture.FrameAvailable += OnFrameAvailable;
            _capture.CaptureFailed += OnCaptureFailed;
            _pipeline = RunPipelineAsync(settings, _activeRecognition, _activeTranslation, _cancellation.Token);
            try
            {
                var captureSettings = new CaptureSettings(
                    settings.SourceMode,
                    microphone,
                    playback,
                    settings.MicrophoneGain,
                    settings.PlaybackGain);
                await _capture.StartAsync(captureSettings, cancellationToken).ConfigureAwait(false);
                IsRunning = true;
                Notify("CAPTURE_STARTED", $"正在采集：{DescribeMode(settings.SourceMode)}；原始音频不会保存。",
                    NotificationSeverity.Information);
                if (!_activeRecognition.IsAvailable || !_activeTranslation.IsAvailable)
                {
                    Notify("PROVIDERS_NOT_CONFIGURED",
                        "识别或翻译服务尚未接入。当前仅验证音频链路，不产生真实识别或译文。",
                        NotificationSeverity.Warning);
                }
            }
            catch (Exception)
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_input is null && _pipeline is null)
        {
            IsRunning = false;
            return;
        }

        IsRunning = false;
        try
        {
            await _capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Notify("CAPTURE_STOP_TIMEOUT", "音频采集停止超时；后台资源可能仍在释放，请重启应用后再采集。", NotificationSeverity.Error);
            _requiresRestart = true;
        }
        catch (Exception)
        {
            Notify("CAPTURE_STOP_FAILED", "停止采集时发生错误；请检查设备状态，必要时重启应用。", NotificationSeverity.Error);
            _requiresRestart = true;
        }
        finally
        {
            _capture.FrameAvailable -= OnFrameAvailable;
            _capture.CaptureFailed -= OnCaptureFailed;
            _input?.Complete();
        }

        Task? pipeline = _pipeline;
        if (pipeline is null)
        {
            ResetPipeline();
            return;
        }

        Task completed = await Task.WhenAny(pipeline, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        if (completed != pipeline)
        {
            _cancellation?.Cancel();
            completed = await Task.WhenAny(pipeline, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }

        if (completed != pipeline)
        {
            Notify("PROVIDER_STOP_TIMEOUT", "识别或翻译服务未响应取消。采集已停止，但不能安全复用会话；请重启应用。", NotificationSeverity.Error);
            _ = ObserveAbandonedPipelineAsync(pipeline, this);
            return;
        }

        await ObservePipelineAsync().ConfigureAwait(false);
        ResetPipeline();
    }

    private static async Task ObserveAbandonedPipelineAsync(Task pipeline, VoiceSession owner)
    {
        try
        {
            await pipeline.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original provider failure is already surfaced by the pipeline's error boundary.
        }

        bool reset = false;
        bool acquired = false;
        try
        {
            await owner._lifecycle.WaitAsync().ConfigureAwait(false);
            acquired = true;
            if (!owner._disposed && ReferenceEquals(owner._pipeline, pipeline))
            {
                owner.ResetPipeline();
                reset = true;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        finally
        {
            if (acquired)
            {
                owner._lifecycle.Release();
            }
        }

        if (reset && !owner._disposed)
        {
            owner.Notify("PROVIDER_EXITED", "后台识别服务已退出；现在可以重新启动会话。", NotificationSeverity.Information);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        if (_pipeline is { IsCompleted: false } || _requiresRestart)
        {
            throw new InvalidOperationException("后台资源未安全停止，不能释放会话。请重启应用。");
        }

        _disposed = true;
        await _capture.DisposeAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task RunPipelineAsync(
        AppSettings settings,
        ISpeechRecognitionProvider recognition,
        ITranslationProvider translation,
        CancellationToken token)
    {
        try
        {
            var recognitionRequest = RecognitionRequest.CreateDefault(settings.SourceLanguage, settings.Glossary);
            var translationRequest = new TranslationRequest(
                settings.SourceLanguage,
                settings.TargetLanguage,
                settings.Glossary,
                new Dictionary<string, string>());
            await foreach (RecognitionUpdate update in recognition.RecognizeAsync(
                               _input!.ReadAllAsync(token), recognitionRequest, token).ConfigureAwait(false))
            {
                long sequence = Interlocked.Increment(ref _sequence);
                RecognitionUpdate sequenced = update with { SequenceNumber = sequence };
                UpdateLatestAudioAgeAtRecognition();
                RecognitionUpdated?.Invoke(this, sequenced);
                if (!sequenced.IsFinal)
                {
                    continue;
                }

                string translatedText = string.Empty;
                bool translated = false;
                if (translation.IsAvailable)
                {
                    long started = Stopwatch.GetTimestamp();
                    try
                    {
                        TranslationResult result = await translation.TranslateAsync(
                            sequenced, translationRequest, token).ConfigureAwait(false);
                        translatedText = result.Text;
                        translated = true;
                        long elapsed = Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        Interlocked.Exchange(ref _lastTranslationLatencyMs, elapsed);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        Notify("TRANSLATION_FAILED",
                            "该段内容翻译失败；采集仍可继续。可停止后重试，原文未写入日志。",
                            NotificationSeverity.Error);
                    }
                }

                TranscriptCompleted?.Invoke(this, new TranscriptEntry(
                    sequence,
                    sequenced.Text,
                    translatedText,
                    translated,
                    recognition.IsSimulated || translation.IsSimulated,
                    DateTimeOffset.Now,
                    sequenced.AudioPosition));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Notify("RECOGNITION_FAILED", "语音识别连接或处理失败，会话将停止。请检查服务配置后重试。",
                NotificationSeverity.Error);
            _ = Task.Run(StopAsync, CancellationToken.None);
        }
    }

    private void ResetPipeline()
    {
        _cancellation?.Dispose();
        _cancellation = null;
        _input?.DiscardRemaining();
        _input = null;
        _pipeline = null;
        _activeRecognition = null;
        _activeTranslation = null;
        IsRunning = false;
    }

    private async Task ObservePipelineAsync()
    {
        if (_pipeline is not null)
        {
            await _pipeline.ConfigureAwait(false);
        }
    }

    private void OnFrameAvailable(object? sender, AudioFrameEventArgs args)
    {
        Interlocked.Exchange(ref _latestFrameTimestamp, args.Frame.Timestamp);
        _input?.TryEnqueue(args.Frame);
    }

    private void OnCaptureFailed(object? sender, AudioCaptureFailedEventArgs args)
    {
        Notify(args.Code, args.UserMessage, NotificationSeverity.Error);
        _ = Task.Run(StopAsync, CancellationToken.None);
    }

    private void UpdateLatestAudioAgeAtRecognition()
    {
        long timestamp = Interlocked.Read(ref _latestFrameTimestamp);
        if (timestamp != 0)
        {
            long elapsed = Math.Max(0, (long)Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
            Interlocked.Exchange(ref _latestAudioAgeAtRecognitionMs, elapsed);
        }
    }

    private void Notify(string code, string message, NotificationSeverity severity) =>
        NotificationRaised?.Invoke(this, new SessionNotification(code, message, severity));

    private static TProvider FindProvider<TProvider>(
        string id,
        IEnumerable<TProvider> providers,
        TProvider fallback)
        where TProvider : class => providers.FirstOrDefault(provider =>
        string.Equals(provider switch
        {
            ISpeechRecognitionProvider recognition => recognition.Id,
            ITranslationProvider translation => translation.Id,
            _ => string.Empty,
        }, id, StringComparison.OrdinalIgnoreCase)) ?? fallback;

    private static string DescribeMode(AudioSourceMode mode) => mode switch
    {
        AudioSourceMode.Microphone => "麦克风",
        AudioSourceMode.Playback => "指定播放设备的系统声音（WASAPI Loopback）",
        AudioSourceMode.Mixed => "麦克风与指定播放设备混合（单路单声道输出）",
        _ => "未知音源",
    };

    private static TimeSpan? ToNullableLatency(long milliseconds) =>
        milliseconds < 0 ? null : TimeSpan.FromMilliseconds(milliseconds);

    private sealed class UnconfiguredFallbackRecognition : ISpeechRecognitionProvider
    {
        public string Id => "unconfigured";
        public string DisplayName => "未接入服务";
        public bool IsAvailable => false;
        public bool IsSimulated => false;

        public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<PcmFrame> audio,
            RecognitionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (PcmFrame _ in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
            }

            yield break;
        }
    }

    private sealed class UnconfiguredFallbackTranslation : ITranslationProvider
    {
        public string Id => "unconfigured";
        public string DisplayName => "未接入翻译";
        public bool IsAvailable => false;
        public bool IsSimulated => false;
        public Task<TranslationResult> TranslateAsync(RecognitionUpdate recognition, TranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult(new TranslationResult(recognition.SequenceNumber, string.Empty));
    }
}
