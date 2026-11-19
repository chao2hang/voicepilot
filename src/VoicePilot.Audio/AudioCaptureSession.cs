using System.Runtime.Versioning;
using VoicePilot.Audio.Processing;
using VoicePilot.Audio.Windows;
using VoicePilot.Core.Audio;

namespace VoicePilot.Audio;

/// <summary>Owns the complete capture graph. Every invocation is foreground and explicitly started by the user.</summary>
[SupportedOSPlatform("windows")]
public sealed class AudioCaptureSession : IAudioCaptureSession, IDisposable
{
    private const int RawQueueCapacityBytes = 512_000;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _sync = new();
    private readonly List<IAudioCaptureSource> _sources = [];
    private readonly Dictionary<AudioSourceKind, BoundedAudioBuffer> _rawQueues = [];
    private readonly List<Task> _workers = [];
    private CancellationTokenSource? _cancellation;
    private AutoResetEvent? _mixerSignal;
    private SynchronizedAudioMixer? _mixer;
    private long _capturedFrames;
    private int _failureReported;
    private bool _stopping;
    private bool _disposed;

    public CaptureState State { get; private set; } = CaptureState.Idle;

    public CaptureStatistics Statistics
    {
        get
        {
            lock (_sync)
            {
                long dropped = _rawQueues.Values.Sum(queue => queue.DroppedFrames);
                TimeSpan buffered = _rawQueues.Values.Select(queue => queue.Format is null
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds((double)queue.PendingBytes / queue.Format.BytesPerSecond))
                    .DefaultIfEmpty(TimeSpan.Zero)
                    .Max();
                long frames = Interlocked.Read(ref _capturedFrames);
                return new CaptureStatistics(
                    frames,
                    dropped,
                    _mixer?.LateFrames ?? 0,
                    buffered,
                    TimeSpan.FromMilliseconds(frames * 20));
            }
        }
    }

    public event EventHandler<AudioFrameEventArgs>? FrameAvailable;

    public event EventHandler<CaptureStateChangedEventArgs>? StateChanged;

    public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    public async Task StartAsync(CaptureSettings settings, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is CaptureState.Starting or CaptureState.Capturing)
            {
                throw new InvalidOperationException("采集已经在运行。");
            }

            ResetGraph();
            Interlocked.Exchange(ref _failureReported, 0);
            _stopping = false;
            Interlocked.Exchange(ref _capturedFrames, 0);
            _cancellation = new CancellationTokenSource();
            CancellationToken token = _cancellation.Token;
            SetState(CaptureState.Starting);
            try
            {
                if (settings.Mode is AudioSourceMode.Microphone or AudioSourceMode.Mixed)
                {
                    AddSource(AudioSourceKind.Microphone);
                }

                if (settings.Mode is AudioSourceMode.Playback or AudioSourceMode.Mixed)
                {
                    AddSource(AudioSourceKind.Playback);
                }

                if (settings.Mode == AudioSourceMode.Mixed)
                {
                    StartMixedWorkers(settings, token);
                }
                else
                {
                    StartSingleWorker(_rawQueues.Single().Value, token);
                }

                foreach (IAudioCaptureSource source in _sources)
                {
                    AudioDeviceInfo device = source.Kind == AudioSourceKind.Microphone
                        ? settings.Microphone!
                        : settings.Playback!;
                    await source.StartAsync(device, cancellationToken).ConfigureAwait(false);
                }

                SetState(CaptureState.Capturing);
            }
            catch (Exception)
            {
                await CleanupGraphAsync().ConfigureAwait(false);
                SetState(CaptureState.Error);
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
            if (State == CaptureState.Idle && _sources.Count == 0)
            {
                return;
            }

            _stopping = true;
            SetState(CaptureState.Stopping);
            await CleanupGraphAsync().ConfigureAwait(false);
            SetState(CaptureState.Idle);
        }
        finally
        {
            _stopping = false;
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        _cancellation?.Dispose();
        _lifecycle.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private void AddSource(AudioSourceKind kind)
    {
        var source = new WasapiAudioCaptureSource(kind);
        var queue = new BoundedAudioBuffer(RawQueueCapacityBytes);
        _sources.Add(source);
        _rawQueues.Add(kind, queue);
        source.FrameAvailable += (_, args) => queue.TryEnqueue(args.Frame);
        source.CaptureFailed += (_, args) => OnCaptureFailed(args);
    }

    private void StartSingleWorker(BoundedAudioBuffer queue, CancellationToken token)
    {
        _workers.Add(Task.Run(async () =>
        {
            try
            {
                PcmAudioConverter? converter = null;
                await foreach (PcmFrame frame in queue.ReadAllAsync(token).ConfigureAwait(false))
                {
                    converter ??= new PcmAudioConverter(frame.Format);
                    converter.Convert(frame, Emit);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                OnCaptureFailed(new AudioCaptureFailedEventArgs(
                    "AUDIO_CONVERSION_FAILED", "音频格式转换失败，采集已停止。", false));
            }
        }, token));
    }

    private void StartMixedWorkers(CaptureSettings settings, CancellationToken token)
    {
        _mixer = new SynchronizedAudioMixer();
        _mixerSignal = new AutoResetEvent(false);
        BoundedAudioBuffer microphone = _rawQueues[AudioSourceKind.Microphone];
        BoundedAudioBuffer playback = _rawQueues[AudioSourceKind.Playback];
        _workers.Add(Task.Run(() => NormalizeToMixerAsync(microphone, token), token));
        _workers.Add(Task.Run(() => NormalizeToMixerAsync(playback, token), token));
        _workers.Add(Task.Factory.StartNew(() =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _mixerSignal.WaitOne(TimeSpan.FromMilliseconds(20));
                    if (_mixer.TryMix(out PcmFrame? mixed, settings.MicrophoneGain, settings.PlaybackGain) && mixed is not null)
                    {
                        Emit(mixed);
                    }
                }
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                OnCaptureFailed(new AudioCaptureFailedEventArgs(
                    "AUDIO_MIXER_CLOCK_FAILED", "混音时钟或输出处理失败，采集已停止。", false));
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }

    private async Task NormalizeToMixerAsync(BoundedAudioBuffer queue, CancellationToken token)
    {
        try
        {
            PcmAudioConverter? converter = null;
            await foreach (PcmFrame frame in queue.ReadAllAsync(token).ConfigureAwait(false))
            {
                converter ??= new PcmAudioConverter(frame.Format);
                converter.Convert(frame, converted =>
                {
                    _mixer!.Offer(converted);
                    _mixerSignal!.Set();
                });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            OnCaptureFailed(new AudioCaptureFailedEventArgs(
                "AUDIO_MIXER_FAILED", "双路音频混音失败，采集已停止。", false));
        }
    }

    private void Emit(PcmFrame frame)
    {
        Interlocked.Increment(ref _capturedFrames);
        FrameAvailable?.Invoke(this, new AudioFrameEventArgs(frame));
    }

    private void OnCaptureFailed(AudioCaptureFailedEventArgs error)
    {
        if (_stopping || _disposed || Interlocked.Exchange(ref _failureReported, 1) != 0)
        {
            return;
        }

        SetState(error.DeviceLost ? CaptureState.DeviceLost : CaptureState.Error);
        _cancellation?.Cancel();
        CompleteQueues();
        CaptureFailed?.Invoke(this, error);
        _ = Task.Run(StopAsync, CancellationToken.None);
    }

    private async Task CleanupGraphAsync()
    {
        _cancellation?.Cancel();
        foreach (IAudioCaptureSource source in _sources)
        {
            try
            {
                await source.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Continue stopping the remaining endpoint even if one device disappeared.
            }
        }

        CompleteQueues();
        _mixerSignal?.Set();
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Worker failures are reported by their owning path; disposal must always continue.
        }
        finally
        {
            foreach (IAudioCaptureSource source in _sources)
            {
                try
                {
                    source.Dispose();
                }
                catch (Exception)
                {
                    // A disconnected endpoint must not prevent cleanup of the other source.
                }
            }

            foreach (BoundedAudioBuffer queue in _rawQueues.Values)
            {
                queue.DiscardRemaining();
            }

            _sources.Clear();
            _rawQueues.Clear();
            _workers.Clear();
            _mixerSignal?.Dispose();
            _mixerSignal = null;
            _mixer = null;
        }
    }

    private void CompleteQueues()
    {
        foreach (BoundedAudioBuffer queue in _rawQueues.Values)
        {
            queue.Complete();
        }
    }

    private void ResetGraph()
    {
        if (_sources.Count != 0 || _workers.Count != 0)
        {
            throw new InvalidOperationException("上一个采集图尚未释放。");
        }

        _cancellation?.Dispose();
        _cancellation = null;
        _mixer = null;
        _mixerSignal = null;
    }

    private void SetState(CaptureState state)
    {
        State = state;
        StateChanged?.Invoke(this, new CaptureStateChangedEventArgs(state));
    }
}
