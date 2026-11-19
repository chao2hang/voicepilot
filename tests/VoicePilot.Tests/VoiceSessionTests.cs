using System.Runtime.CompilerServices;
using VoicePilot.Core.Audio;
using VoicePilot.Core.Configuration;
using VoicePilot.Core.Sessions;
using VoicePilot.Core.Speech;
using Xunit;

namespace VoicePilot.Tests;

public sealed class VoiceSessionTests
{
    [Fact]
    public async Task Session_EmitsInterimFinalAndTranslationInOrder()
    {
        using var capture = new FakeCaptureSession();
        await using var session = new VoiceSession(
            capture,
            [new ScriptedRecognitionProvider()],
            [new ScriptedTranslationProvider()]);
        var recognition = new List<RecognitionUpdate>();
        var transcripts = new List<TranscriptEntry>();
        session.RecognitionUpdated += (_, update) => recognition.Add(update);
        session.TranscriptCompleted += (_, entry) => transcripts.Add(entry);
        var settings = new AppSettings
        {
            RecognitionProviderId = "scripted",
            TranslationProviderId = "scripted",
        };
        var microphone = new AudioDeviceInfo("mic", "Microphone", AudioDeviceKind.Microphone, true);
        await session.StartAsync(settings, microphone, null, CancellationToken.None);
        for (int index = 0; index < 4; index++)
        {
            capture.Emit(new PcmFrame(new byte[640], AudioFormat.RecognitionPcm16,
                AudioSourceKind.Microphone, System.Diagnostics.Stopwatch.GetTimestamp()));
        }

        await session.StopAsync();
        Assert.Equal([false, true], recognition.Select(update => update.IsFinal).ToArray());
        TranscriptEntry entry = Assert.Single(transcripts);
        Assert.Equal("final text", entry.OriginalText);
        Assert.Equal("translated text", entry.TranslatedText);
        Assert.True(entry.IsTranslationAvailable);
        Assert.False(session.IsRunning);
        Assert.True(capture.Stopped);
    }

    [Fact]
    public async Task Stop_CancelsProviderThatIsWaitingForMoreAudio()
    {
        using var capture = new FakeCaptureSession();
        await using var session = new VoiceSession(capture, [new WaitingRecognitionProvider()], []);
        var settings = new AppSettings { RecognitionProviderId = "waiting" };
        var microphone = new AudioDeviceInfo("mic", "Microphone", AudioDeviceKind.Microphone, true);
        await session.StartAsync(settings, microphone, null, CancellationToken.None);
        Task stop = session.StopAsync();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stop.IsCompletedSuccessfully);
        Assert.True(capture.Stopped);
    }

    [Fact]
    public async Task CaptureFailure_StopsPipelineAndReportsSafeMessage()
    {
        using var capture = new FakeCaptureSession();
        await using var session = new VoiceSession(capture, [new WaitingRecognitionProvider()], []);
        var notifications = new List<SessionNotification>();
        session.NotificationRaised += (_, notification) => notifications.Add(notification);
        var settings = new AppSettings { RecognitionProviderId = "waiting" };
        var microphone = new AudioDeviceInfo("mic", "Microphone", AudioDeviceKind.Microphone, true);
        await session.StartAsync(settings, microphone, null, CancellationToken.None);
        capture.Fail("DEVICE_LOST", "设备已拔出");
        await WaitUntilAsync(() => !session.IsRunning);
        Assert.Contains(notifications, notification => notification.Code == "DEVICE_LOST");
        Assert.True(capture.Stopped);
    }

    [Fact]
    public async Task Stop_ReportsProviderThatIgnoresCancellationWithoutWaitingForever()
    {
        using var capture = new FakeCaptureSession();
        var provider = new CancellationIgnoringRecognitionProvider();
        var session = new VoiceSession(capture, [provider], []);
        var notifications = new List<SessionNotification>();
        session.NotificationRaised += (_, notification) => notifications.Add(notification);
        var settings = new AppSettings { RecognitionProviderId = "ignoring" };
        var microphone = new AudioDeviceInfo("mic", "Microphone", AudioDeviceKind.Microphone, true);
        await session.StartAsync(settings, microphone, null, CancellationToken.None);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(6));
        Assert.False(session.IsRunning);
        Assert.True(capture.Stopped);
        Assert.Contains(notifications, notification => notification.Code == "PROVIDER_STOP_TIMEOUT");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.StartAsync(settings, microphone, null, CancellationToken.None));
        provider.Release.TrySetResult();
        await WaitUntilAsync(() => notifications.Any(notification => notification.Code == "PROVIDER_EXITED"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task StartFailure_StopsCaptureAndCleansUpPipeline()
    {
        using var capture = new FakeCaptureSession { StartException = new InvalidOperationException("start failed") };
        await using var session = new VoiceSession(capture, [new WaitingRecognitionProvider()], []);
        var microphone = new AudioDeviceInfo("mic", "Microphone", AudioDeviceKind.Microphone, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync(
            new AppSettings { RecognitionProviderId = "waiting" }, microphone, null, CancellationToken.None));
        Assert.True(capture.Stopped);
        Assert.False(session.IsRunning);
        Assert.Null(session.Statistics.LastTranslationLatency);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class ScriptedRecognitionProvider : ISpeechRecognitionProvider
    {
        public string Id => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;
        public bool IsSimulated => false;

        public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<PcmFrame> audio,
            RecognitionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            bool interim = false;
            await foreach (PcmFrame _ in audio.WithCancellation(cancellationToken))
            {
                if (!interim)
                {
                    interim = true;
                    yield return new RecognitionUpdate(0, "interim", false, TimeSpan.Zero);
                }
            }

            yield return new RecognitionUpdate(0, "final text", true, TimeSpan.FromMilliseconds(80));
        }
    }

    private sealed class WaitingRecognitionProvider : ISpeechRecognitionProvider
    {
        public string Id => "waiting";
        public string DisplayName => "Waiting";
        public bool IsAvailable => true;
        public bool IsSimulated => false;

        public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<PcmFrame> audio,
            RecognitionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (PcmFrame _ in audio.WithCancellation(cancellationToken))
            {
            }

            yield break;
        }
    }

    private sealed class CancellationIgnoringRecognitionProvider : ISpeechRecognitionProvider
    {
        public string Id => "ignoring";
        public string DisplayName => "Ignoring";
        public bool IsAvailable => true;
        public bool IsSimulated => false;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RecognitionUpdate> RecognizeAsync(
            IAsyncEnumerable<PcmFrame> audio,
            RecognitionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task;
            yield break;
        }
    }

    private sealed class ScriptedTranslationProvider : ITranslationProvider
    {
        public string Id => "scripted";
        public string DisplayName => "Scripted";
        public bool IsAvailable => true;
        public bool IsSimulated => false;
        public Task<TranslationResult> TranslateAsync(RecognitionUpdate recognition, TranslationRequest request,
            CancellationToken cancellationToken) => Task.FromResult(new TranslationResult(recognition.SequenceNumber, "translated text"));
    }

    private sealed class FakeCaptureSession : IAudioCaptureSession, IDisposable
    {
        public CaptureState State { get; private set; } = CaptureState.Idle;
        public CaptureStatistics Statistics => new(0, 0, 0, TimeSpan.Zero, TimeSpan.Zero);
        public bool Stopped { get; private set; }
        public Exception? StartException { get; init; }
        public event EventHandler<AudioFrameEventArgs>? FrameAvailable;
        public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;
        public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

        public Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
        {
            settings.Validate();
            if (StartException is not null)
            {
                throw StartException;
            }

            State = CaptureState.Capturing;
            Stopped = false;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            State = CaptureState.Idle;
            Stopped = true;
            StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(CaptureState.Idle));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
        }

        public void Emit(PcmFrame frame) => FrameAvailable?.Invoke(this, new AudioFrameEventArgs(frame));

        public void Fail(string code, string message) => CaptureFailed?.Invoke(this,
            new AudioCaptureFailedEventArgs(code, message, true));
    }
}
