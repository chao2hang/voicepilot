using System.Text.Json;
using VoicePilot.Core.Configuration;
using VoicePilot.Core.Speech;
using VoicePilot.Infrastructure.Configuration;
using Xunit;

namespace VoicePilot.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task Settings_RoundTripWithoutAudioOrSecrets()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoicePilotTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            using (var store = new JsonSettingsStore(path))
            {
                var settings = new AppSettings
                {
                    MicrophoneDeviceId = "stable-input-id",
                    PlaybackDeviceId = "stable-output-id",
                    Glossary = [new GlossaryTerm("原词", "target")],
                };
                await store.SaveAsync(settings, CancellationToken.None);
            }

            using (var store = new JsonSettingsStore(path))
            {
                AppSettings loaded = await store.LoadAsync(CancellationToken.None);
                Assert.Equal("stable-input-id", loaded.MicrophoneDeviceId);
                Assert.Equal("stable-output-id", loaded.PlaybackDeviceId);
                Assert.Equal("原词", Assert.Single(loaded.Glossary).Source);
            }

            string json = await File.ReadAllTextAsync(path);
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.False(document.RootElement.TryGetProperty("audio", out _));
            Assert.False(document.RootElement.TryGetProperty("token", out _));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task LanguageCatalog_IsLoadedFromDataNotViewLogic()
    {
        string path = Path.Combine(Path.GetTempPath(), $"languages-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path,
                "[{\"code\":\"zz-ZZ\",\"displayName\":\"Extensible\",\"supportsRecognition\":true,\"supportsTranslation\":true}]");
            var catalog = new JsonLanguageCatalog(path);
            LanguageOption language = Assert.Single(await catalog.LoadAsync(CancellationToken.None));
            Assert.Equal("zz-ZZ", language.Code);
            Assert.True(language.SupportsRecognition);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
