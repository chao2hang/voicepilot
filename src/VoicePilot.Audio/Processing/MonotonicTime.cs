using System.Diagnostics;
using VoicePilot.Core.Audio;

namespace VoicePilot.Audio.Processing;

internal static class MonotonicTime
{
    public static long Add(long timestamp, TimeSpan duration) =>
        timestamp + (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency, MidpointRounding.AwayFromZero);

    public static TimeSpan Elapsed(long from, long to) =>
        TimeSpan.FromSeconds((double)(to - from) / Stopwatch.Frequency);
}
