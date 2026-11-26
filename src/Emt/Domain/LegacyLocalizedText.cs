namespace FSDEV.MOD.Emt.Emt.Domain;

/// <summary>
/// Error code texts (title, description, instructions) used to be stored per language ({"de": "...", "en": "..."}).
/// They are a single text now; this reads data that was saved in the old form.
/// </summary>
public static class LegacyLocalizedText
{
    private static readonly string[] PREFERRED_LANGUAGES = ["de", "en"];

    /// <summary>German first (the former fallback language), then English, then the first language that has a text.</summary>
    public static string ToSingleText(IReadOnlyDictionary<string, string> text)
    {
        foreach (var language in PREFERRED_LANGUAGES)
        {
            if (text.TryGetValue(language, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return text.Values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
