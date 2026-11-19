using System.Text.Json;
using VoicePilot.Core.Configuration;

namespace VoicePilot.Infrastructure.Configuration;

public sealed class JsonLanguageCatalog(string? filePath = null) : ILanguageCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private readonly string _filePath = filePath ?? Path.Combine(
        AppContext.BaseDirectory, "Configuration", "languages.json");

    public async Task<IReadOnlyList<LanguageOption>> LoadAsync(CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(_filePath);
        List<LanguageOption>? languages = await JsonSerializer.DeserializeAsync<List<LanguageOption>>(
            stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (languages is null || languages.Count == 0)
        {
            throw new InvalidDataException("语言目录为空。");
        }

        return languages;
    }
}
