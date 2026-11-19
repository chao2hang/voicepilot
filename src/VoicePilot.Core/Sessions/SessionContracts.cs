using VoicePilot.Core.Audio;
using VoicePilot.Core.Configuration;
using VoicePilot.Core.Speech;

namespace VoicePilot.Core.Sessions;

public interface IVoiceSession : IAsyncDisposable
{
    bool IsRunning { get; }

    SessionStatistics Statistics { get; }

    event EventHandler<RecognitionUpdate>? RecognitionUpdated;

    event EventHandler<TranscriptEntry>? TranscriptCompleted;

    event EventHandler<SessionNotification>? NotificationRaised;

    Task StartAsync(
        AppSettings settings,
        AudioDeviceInfo? microphone,
        AudioDeviceInfo? playback,
        CancellationToken cancellationToken);

    Task StopAsync();
}

public sealed record TranscriptEntry(
    long SequenceNumber,
    string OriginalText,
    string TranslatedText,
    bool IsTranslationAvailable,
    bool IsSimulated,
    DateTimeOffset CompletedAt,
    TimeSpan AudioPosition);

public sealed record SessionStatistics(
    CaptureStatistics Capture,
    long RecognitionQueueDroppedFrames,
    int RecognitionQueueBytes,
    TimeSpan? LatestAudioAgeAtRecognition,
    TimeSpan? LastTranslationLatency);

public enum NotificationSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record SessionNotification(
    string Code,
    string Message,
    NotificationSeverity Severity);
