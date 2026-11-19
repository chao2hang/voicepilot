using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace VoicePilot.Core.Audio;

/// <summary>
/// A low-latency queue bounded by both frame count and buffered bytes. New audio is dropped on overload;
/// unbounded producer growth is never permitted.
/// </summary>
public sealed class BoundedAudioBuffer
{
    private const int DefaultMinimumFrameBytes = 640;
    private readonly Channel<PcmFrame> _channel;
    private readonly int _minimumFrameBytes;
    private int _pendingBytes;
    private long _droppedFrames;

    public BoundedAudioBuffer(int capacityBytes = 128_000, int minimumFrameBytes = DefaultMinimumFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumFrameBytes);
        CapacityBytes = capacityBytes;
        _minimumFrameBytes = minimumFrameBytes;
        int frameCapacity = CalculateFrameCapacity(capacityBytes, minimumFrameBytes);
        _channel = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(frameCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public int CapacityBytes { get; }

    public int PendingBytes => Math.Max(0, Volatile.Read(ref _pendingBytes));

    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    public AudioFormat? Format { get; private set; }

    public bool TryEnqueue(PcmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Format ??= frame.Format;
        int bytes = frame.Data.Length;
        int pending = Interlocked.Add(ref _pendingBytes, bytes);
        if (pending > CapacityBytes || !_channel.Writer.TryWrite(frame))
        {
            Interlocked.Add(ref _pendingBytes, -bytes);
            Interlocked.Increment(ref _droppedFrames);
            return false;
        }

        return true;
    }

    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Called by the single owner after its reader stops; releases all retained frames and statistics.</summary>
    public void DiscardRemaining()
    {
        Complete();
        while (_channel.Reader.TryRead(out PcmFrame? frame))
        {
            if (frame is not null)
            {
                Interlocked.Add(ref _pendingBytes, -frame.Data.Length);
            }
        }

        Interlocked.Exchange(ref _pendingBytes, 0);
    }

    public async IAsyncEnumerable<PcmFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (PcmFrame frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Add(ref _pendingBytes, -frame.Data.Length);
            yield return frame;
        }
    }

    public int FrameCapacity => CalculateFrameCapacity(CapacityBytes, _minimumFrameBytes);

    private static int CalculateFrameCapacity(int capacityBytes, int minimumFrameBytes) =>
        Math.Max(2, checked((int)Math.Min(int.MaxValue, (capacityBytes - 1L) / minimumFrameBytes + 1)));
}
