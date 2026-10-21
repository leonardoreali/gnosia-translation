namespace GnosiaPatcher;

/// <summary>A translation that can be written into the game's English text slot.</summary>
record Language(string Code, string EnglishName, string NativeName)
{
    public static readonly Language[] Patchable =
    {
        new("pt-BR", "Brazilian Portuguese", "Português (Brasil)"),
        new("es", "Spanish", "Español"),
        new("de", "German", "Deutsch"),
        new("fr", "French", "Français"),
        new("ru", "Russian", "Русский"),
    };

    /// <summary>Languages the game ships with; selected in the game's own options.</summary>
    public static readonly string[] Native = { "English", "日本語 (Japanese)", "简体中文 (Simplified Chinese)" };

    public string ResourceName => $"translations.{Code}.tsv";

    /// <summary>Languages whose embedded translation file actually has content.</summary>
    public static Language[] Available => Patchable.Where(l => TranslationFile.FromResource(l).Entries.Count > 0).ToArray();
}
