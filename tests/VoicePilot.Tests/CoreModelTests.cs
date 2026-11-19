using System.Reflection;
using VoicePilot.Core.Configuration;
using VoicePilot.Core.Audio;
using VoicePilot.Core.Sessions;
using VoicePilot.Core.Speech;
using Xunit;

namespace VoicePilot.Tests;

public sealed class CoreModelTests
{
    [Fact]
    public void AudioFormat_ComputesExpectedLayout()
    {
        var format = new AudioFormat(48_000, 2, 32, AudioSampleEncoding.IeeeFloat);
        Assert.Equal(8, format.BlockAlign);
        Assert.Equal(384_000, format.BytesPerSecond);
    }

    [Fact]
    public void PcmFrame_RejectsPartialSampleFrame()
    {
        Assert.Throws<ArgumentException>(() => new PcmFrame(
            new byte[3], AudioFormat.RecognitionPcm16, AudioSourceKind.Microphone, 1));
    }

    [Fact]
    public void CaptureSettings_RequiresExplicitEndpointForEachSource()
    {
        var playback = new AudioDeviceInfo("render-id", "Headphones", AudioDeviceKind.Playback, true);
        var mixed = new CaptureSettings(AudioSourceMode.Mixed, null, playback);
        Assert.Throws<InvalidOperationException>(() => mixed.Validate());

        var loopback = new CaptureSettings(AudioSourceMode.Playback, null, playback);
        loopback.Validate();
    }

    [Fact]
    public void TranscriptEntry_PreservesSimulationMarker()
    {
        var entry = new TranscriptEntry(1, "source", "target", true, true,
            DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(2));
        Assert.True(entry.IsSimulated);
        Assert.Equal("target", entry.TranslatedText);
    }

    [Fact]
    public void PublicContracts_DoNotContainSecretOrEndpointProperties()
    {
        IEnumerable<PropertyInfo> properties = typeof(AppSettings).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("VoicePilot.Core", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(properties, property =>
            property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }

}
