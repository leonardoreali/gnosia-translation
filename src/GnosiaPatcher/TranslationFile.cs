using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace GnosiaPatcher;

/// <summary>
/// A translation file: one entry per line, "key&lt;TAB&gt;text". Lines starting with '#' are comments.
/// Keys look like "CharaText/0:Takashi/12" (asset / sheet index:name / row index).
/// Line breaks inside a text are written as the two characters "\n", exactly as the game stores them.
/// </summary>
sealed class TranslationFile
{
    public Dictionary<string, string> Entries { get; } = new();

    public static TranslationFile FromResource(Language lang)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(lang.ResourceName)
            ?? throw new InvalidOperationException($"No embedded translation for {lang.Code}.");
        return Parse(new StreamReader(stream, Encoding.UTF8));
    }

    public static TranslationFile FromPath(string path) => Parse(new StreamReader(path, Encoding.UTF8));

    static TranslationFile Parse(TextReader reader)
    {
        var file = new TranslationFile();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            file.Entries[line[..tab]] = Sanitize(line[(tab + 1)..]);
        }
        return file;
    }

    /// <summary>
    /// Replaces typographic characters the game's fonts cannot draw with plain equivalents,
    /// so translators can write naturally.
    /// </summary>
    public static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case 'ß': sb.Append("ss"); break;
                case 'ẞ': sb.Append("SS"); break;
                case 'œ': sb.Append("oe"); break;
                case 'Œ': sb.Append("OE"); break;
                case 'æ': sb.Append("ae"); break;
                case 'Æ': sb.Append("AE"); break;
                case '«': case '»': case '„': case '“': case '”': sb.Append("\\\""); break;
                case '‘': case '’': case '‚': sb.Append('\''); break;
                case '—': case '–': sb.Append('-'); break;
                case ' ': case ' ': sb.Append(' '); break;
                case 'Ÿ': sb.Append('Y'); break;
                case 'Ì': sb.Append('I'); break;
                case 'Ò': sb.Append('O'); break;
                case 'ª': sb.Append('a'); break;
                case 'º': sb.Append('o'); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Placeholders like {0} that must survive translation.</summary>
    public static IEnumerable<string> Placeholders(string s) =>
        Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Distinct();
}
