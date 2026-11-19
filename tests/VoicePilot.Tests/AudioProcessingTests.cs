using System.Buffers.Binary;
using System.Diagnostics;
using VoicePilot.Audio.Processing;
using VoicePilot.Core.Audio;
using Xunit;

namespace VoicePilot.Tests;

public sealed class AudioProcessingTests
{
    [Fact]
    public void Queue_EnforcesByteBudgetAndTracksDrops()
    {
        var queue = new BoundedAudioBuffer(capacityBytes: 1280, minimumFrameBytes: 640);
        Assert.True(queue.TryEnqueue(CreateFrame(0)));
        Assert.True(queue.TryEnqueue(CreateFrame(1)));
        Assert.False(queue.TryEnqueue(CreateFrame(2)));
        Assert.Equal(1280, queue.PendingBytes);
        Assert.Equal(1, queue.DroppedFrames);
        queue.DiscardRemaining();
        Assert.Equal(0, queue.PendingBytes);
    }

    [Fact]
    public async Task Queue_ReleasesByteBudgetAfterReading()
    {
        var queue = new BoundedAudioBuffer(capacityBytes: 640, minimumFrameBytes: 640);
        queue.TryEnqueue(CreateFrame(0));
        queue.Complete();
        await foreach (PcmFrame frame in queue.ReadAllAsync())
        {
            Assert.Equal(640, frame.Data.Length);
        }

        Assert.Equal(0, queue.PendingBytes);
    }

    [Fact]
    public void Mixer_ClampsSummedPcmWithoutWrapping()
    {
        var mixer = new SynchronizedAudioMixer();
        var loud = new byte[640];
        for (int index = 0; index < 320; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(index * 2, 2), short.MaxValue);
        }

        long timestamp = Stopwatch.GetTimestamp();
        OfferBoth(mixer, loud, timestamp);
        long now = timestamp + Ticks(TimeSpan.FromMilliseconds(60));
        Assert.True(mixer.TryMix(now, out PcmFrame? mixed, 1, 1));
        Assert.NotNull(mixed);
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.Span));
    }

    [Fact]
    public void Mixer_AppliesSampleOffsetInsteadOfHardMixingWholeFrames()
    {
        var mixer = new SynchronizedAudioMixer();
        byte[] microphone = new byte[640];
        byte[] playback = new byte[640];
        BinaryPrimitives.WriteInt16LittleEndian(microphone.AsSpan(160 * 2, 2), 1000);
        BinaryPrimitives.WriteInt16LittleEndian(microphone.AsSpan(319 * 2, 2), 500);
        BinaryPrimitives.WriteInt16LittleEndian(playback.AsSpan(0, 2), 2000);
        BinaryPrimitives.WriteInt16LittleEndian(playback.AsSpan(159 * 2, 2), 700);
        long timestamp = Stopwatch.GetTimestamp();
        mixer.Offer(new PcmFrame(microphone, AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, timestamp));
        mixer.Offer(new PcmFrame(playback, AudioFormat.RecognitionPcm16, AudioSourceKind.Playback,
            timestamp + Ticks(TimeSpan.FromMilliseconds(10))));
        long now = timestamp + Ticks(TimeSpan.FromMilliseconds(60));
        Assert.True(mixer.TryMix(now, out PcmFrame? mixed, 1, 1));
        Assert.NotNull(mixed);
        // Timeline starts at the earliest input. Playback is silent for its initial 10 ms;
        // microphone sample 160 then aligns with playback sample 0.
        Assert.Equal(timestamp, mixed.Timestamp);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.Span));
        Assert.Equal(3000, BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.Span.Slice(160 * 2, 2)));
        Assert.Equal(1200, BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.Span.Slice(319 * 2, 2)));
    }

    [Fact]
    public void Mixer_FillsMissingStreamWithSilenceAndAdvancesTimeline()
    {
        var mixer = new SynchronizedAudioMixer();
        long timestamp = Stopwatch.GetTimestamp();
        OfferBoth(mixer, new byte[640], timestamp);
        long now = timestamp + Ticks(TimeSpan.FromMilliseconds(60));
        Assert.True(mixer.TryMix(now, out PcmFrame? first));
        Assert.NotNull(first);

        long step = Ticks(first.Duration);
        mixer.Offer(new PcmFrame(new byte[640], AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone,
            first.Timestamp + step));
        long secondDeadline = first.Timestamp + step + Ticks(TimeSpan.FromMilliseconds(60));
        Assert.True(mixer.TryMix(secondDeadline, out PcmFrame? second));
        Assert.NotNull(second);
        Assert.Equal(AudioSourceKind.Mixed, second.Source);
        Assert.Equal(first.Timestamp + step, second.Timestamp);
    }

    [Fact]
    public void Mixer_PacesOutputAndProgressesWhenOtherStreamNeverArrives()
    {
        var mixer = new SynchronizedAudioMixer();
        long start = Stopwatch.GetTimestamp();
        mixer.Offer(new PcmFrame(new byte[640], AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, start));
        long now = start + Ticks(TimeSpan.FromMilliseconds(60));
        Assert.True(mixer.TryMix(now, out PcmFrame? mixed));
        Assert.NotNull(mixed);
        Assert.False(mixer.TryMix(now, out _, 0.8, 0.8));
    }

    [Fact]
    public void Mixer_RecoversAfterLongIdlePeriodWithoutEmittingFutureAudio()
    {
        var mixer = new SynchronizedAudioMixer();
        long start = Stopwatch.GetTimestamp();
        mixer.Offer(new PcmFrame(new byte[640], AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, start));
        Assert.True(mixer.TryMix(start + Ticks(TimeSpan.FromMilliseconds(60)), out _));
        long idlePoll = start + Ticks(TimeSpan.FromSeconds(2));
        Assert.False(mixer.TryMix(idlePoll, out _));

        long resumed = start + Ticks(TimeSpan.FromSeconds(3));
        mixer.Offer(new PcmFrame(new byte[640], AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, resumed));
        Assert.False(mixer.TryMix(resumed - Ticks(TimeSpan.FromMilliseconds(1)), out _));
        Assert.False(mixer.TryMix(resumed, out _));
        Assert.True(mixer.TryMix(resumed + Ticks(TimeSpan.FromMilliseconds(60)), out PcmFrame? mixed));
        Assert.NotNull(mixed);
        Assert.Equal(resumed, mixed.Timestamp);
    }

    [Fact]
    public void Mixer_ConsumesFramesAndDoesNotRepeatAudio()
    {
        var mixer = new SynchronizedAudioMixer();
        long start = Stopwatch.GetTimestamp();
        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            byte[] microphone = new byte[640];
            byte[] playback = new byte[640];
            for (int sample = 0; sample < 320; sample++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(microphone.AsSpan(sample * 2, 2), (short)(100 + frameIndex));
                BinaryPrimitives.WriteInt16LittleEndian(playback.AsSpan(sample * 2, 2), (short)(200 + frameIndex));
            }

            long timestamp = start + frameIndex * Ticks(TimeSpan.FromMilliseconds(20));
            mixer.Offer(new PcmFrame(microphone, AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, timestamp));
            mixer.Offer(new PcmFrame(playback, AudioFormat.RecognitionPcm16, AudioSourceKind.Playback, timestamp));
        }

        for (int frameIndex = 0; frameIndex < 3; frameIndex++)
        {
            long now = start + Ticks(TimeSpan.FromMilliseconds(60 + 20 * frameIndex));
            Assert.True(mixer.TryMix(now, out PcmFrame? mixed, 1, 1));
            Assert.NotNull(mixed);
            Assert.Equal(start + frameIndex * Ticks(TimeSpan.FromMilliseconds(20)), mixed.Timestamp);
            Assert.Equal(300 + frameIndex * 2, BinaryPrimitives.ReadInt16LittleEndian(mixed.Data.Span));
        }

        Assert.Equal(0, mixer.LateFrames);
    }

    [Fact]
    public async Task Converter_ProducesExactRecognitionChunks()
    {
        var format = new AudioFormat(48_000, 2, 32, AudioSampleEncoding.IeeeFloat);
        var converter = new PcmAudioConverter(format);
        var bytes = new byte[format.BytesPerSecond / 5];
        var chunks = new List<PcmFrame>();
        converter.Convert(new PcmFrame(bytes, format, AudioSourceKind.Playback, Stopwatch.GetTimestamp()), chunks.Add);
        await Task.Yield();
        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(AudioFormat.RecognitionPcm16, chunk.Format);
            Assert.Equal(640, chunk.Data.Length);
            Assert.Equal(TimeSpan.FromMilliseconds(20), chunk.Duration);
        });
    }

    [Fact]
    public void Converter_RejectsUnexpectedFormatChange()
    {
        var format = new AudioFormat(48_000, 2, 32, AudioSampleEncoding.IeeeFloat);
        var converter = new PcmAudioConverter(format);
        var changed = new AudioFormat(44_100, 2, 32, AudioSampleEncoding.IeeeFloat);
        Assert.Throws<NotSupportedException>(() => converter.Convert(
            new PcmFrame(new byte[1760], changed, AudioSourceKind.Microphone, 1), _ => { }));
    }

    [Fact]
    public void Converter_ReanchorsOnRawTimestampGap()
    {
        var format = AudioFormat.RecognitionPcm16;
        var converter = new PcmAudioConverter(format);
        var chunks = new List<PcmFrame>();
        long start = Stopwatch.GetTimestamp();
        converter.Convert(new PcmFrame(new byte[640], format, AudioSourceKind.Playback, start), chunks.Add);
        long gapStart = start + Ticks(TimeSpan.FromSeconds(2));
        converter.Convert(new PcmFrame(new byte[640], format, AudioSourceKind.Playback, gapStart), chunks.Add);
        Assert.Equal(gapStart, chunks[^1].Timestamp);
    }

    private static PcmFrame CreateFrame(long sequence) => new(
        new byte[640], AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, sequence);

    private static void OfferBoth(SynchronizedAudioMixer mixer, byte[] samples, long timestamp)
    {
        mixer.Offer(new PcmFrame(samples, AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, timestamp));
        mixer.Offer(new PcmFrame(samples, AudioFormat.RecognitionPcm16, AudioSourceKind.Playback, timestamp));
    }

    private static long Ticks(TimeSpan duration) =>
        (long)Math.Round(duration.TotalSeconds * Stopwatch.Frequency);
}
