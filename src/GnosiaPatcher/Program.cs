using System.Text;
using GnosiaPatcher;

// Gnosia Translation Patcher
//   Interactive:   GnosiaTranslationPatcher.exe
//   Command line:  GnosiaTranslationPatcher.exe --lang pt-BR [--game "<...>\Gnosia_Data"] [--file my.tsv]
//                  GnosiaTranslationPatcher.exe --restore
//   Translators:   --export reference.tsv          (key, English, Japanese, Chinese from your own copy)
//                  --check es.tsv --ref reference.tsv

Console.OutputEncoding = Encoding.UTF8;
var opts = Options.Parse(args);
bool interactive = args.Length == 0;

try
{
    if (opts.Check != null) return Check(opts.Check, opts.Reference);

    Console.WriteLine("Gnosia Translation Patcher " + typeof(GamePatcher).Assembly.GetName().Version?.ToString(3));
    Console.WriteLine("Unofficial fan translation. Gnosia © Petit Depotto / PLAYISM.");
    Console.WriteLine();

    string? dataDir = opts.Game != null ? Path.GetFullPath(opts.Game) : GamePatcher.FindGame(new[] { AppContext.BaseDirectory, Environment.CurrentDirectory });
    if (dataDir == null || !File.Exists(Path.Combine(dataDir, "sharedassets0.assets")))
    {
        Console.WriteLine("Could not find Gnosia (Steam). Drag the \"Gnosia_Data\" folder here and press Enter:");
        dataDir = Console.ReadLine()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(dataDir) || !File.Exists(Path.Combine(dataDir, "sharedassets0.assets")))
            return Exit(1, "Gnosia_Data folder not found.");
    }
    if (System.Diagnostics.Process.GetProcessesByName("Gnosia").Length > 0)
        return Exit(1, "Please close the game first.");

    var patcher = new GamePatcher(dataDir);
    Console.WriteLine($"Game found: {dataDir}");
    Console.WriteLine();

    if (opts.Restore) return RestoreGame(patcher);
    if (opts.Export != null) return Exit(0, $"Exported {patcher.ExportReference(opts.Export)} lines to {opts.Export}");
    if (opts.PreviewImages != null)
    {
        var t = opts.File != null ? TranslationFile.FromPath(opts.File) : TranslationFile.FromResource(Language.Patchable.First(l => l.Code.Equals(opts.Lang ?? "pt-BR", StringComparison.OrdinalIgnoreCase)));
        var r = patcher.Apply(t, Console.WriteLine, opts.PreviewImages);
        return Exit(0, $"{r.ImageTexts} image texts rendered to {opts.PreviewImages}");
    }

    Language? lang = opts.Lang == null ? null
        : Language.Available.FirstOrDefault(l => l.Code.Equals(opts.Lang, StringComparison.OrdinalIgnoreCase));

    if (lang == null)
    {
        if (!interactive) return Exit(1, $"Unknown language \"{opts.Lang}\". Available: {string.Join(", ", Language.Available.Select(l => l.Code))}");
        var langs = Language.Available;
        Console.WriteLine("Choose a language:");
        for (int i = 0; i < langs.Length; i++)
            Console.WriteLine($"  {i + 1}. {langs[i].NativeName}");
        Console.WriteLine($"  {langs.Length + 1}. Restore original game (English / Japanese / Chinese)");
        Console.WriteLine();
        Console.WriteLine("English, Japanese and Simplified Chinese are built into the game: pick them in the game's options.");
        Console.Write("> ");
        if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > langs.Length + 1)
            return Exit(1, "Invalid choice.");
        if (choice == langs.Length + 1) return RestoreGame(patcher);
        lang = langs[choice - 1];
    }

    var translation = opts.File != null ? TranslationFile.FromPath(opts.File) : TranslationFile.FromResource(lang);
    Console.WriteLine($"Installing {lang.NativeName}...");
    var result = patcher.Apply(translation, Console.WriteLine);
    Console.WriteLine();
    Console.WriteLine($"Done! {result.Translated} of {result.Rows} lines translated, {result.ImageTexts} texts in images, {result.GlyphsAdded} letters added to the fonts.");
    if (result.Skipped > 0) Console.WriteLine($"{result.Skipped} lines were skipped because a {{0}}-style placeholder was missing.");
    if (result.UnknownKeys > 0) Console.WriteLine($"{result.UnknownKeys} translated lines did not match this game version.");
    Console.WriteLine();
    Console.WriteLine("In the game's options, set the language to ENGLISH: the English text has been replaced.");
    Console.WriteLine("Run this again and choose \"Restore\" to undo. A Steam update or file verification also undoes it.");
    return Exit(0, null);
}
catch (Exception e)
{
    return Exit(2, "Error: " + e.Message);
}

// Validates a translation file: placeholders kept (needs --ref from --export) and characters the fonts can draw.
int Check(string path, string? reference)
{
    var t = TranslationFile.FromPath(path);
    var refText = new Dictionary<string, string>();
    if (reference != null)
        foreach (var line in File.ReadLines(reference))
        {
            if (line.StartsWith('#')) continue;
            var p = line.Split('\t');
            if (p.Length > 1) refText[p[0]] = p[1];
        }
    var drawable = new HashSet<char>(Enumerable.Range(0x20, 0x5F).Select(i => (char)i).Concat(FontPatch.AddedCharacters).Concat("…♪"));
    int problems = 0;
    var badChars = new SortedDictionary<char, int>();
    foreach (var (key, text) in t.Entries)
    {
        if (reference != null)
        {
            if (!refText.TryGetValue(key, out var en)) { Console.WriteLine($"unknown key: {key}"); problems++; continue; }
            foreach (var ph in TranslationFile.Placeholders(en))
                if (!text.Contains(ph)) { Console.WriteLine($"missing {ph}: {key}"); problems++; }
        }
        foreach (char c in text)
        {
            bool cyrillic = c >= 'Ѐ' && c <= 'ӿ', cjk = c >= '　';
            if (!drawable.Contains(c) && !cyrillic && !cjk) badChars[c] = badChars.GetValueOrDefault(c) + 1;
        }
    }
    foreach (var (c, n) in badChars) { Console.WriteLine($"character the fonts cannot draw: '{c}' (U+{(int)c:X4}) x{n}"); problems++; }
    if (reference != null) Console.WriteLine($"{t.Entries.Count} of {refText.Count} lines translated.");
    Console.WriteLine(problems == 0 ? "OK" : $"{problems} problem(s).");
    return problems == 0 ? 0 : 1;
}

int RestoreGame(GamePatcher patcher)
{
    if (patcher.Restore()) return Exit(0, "Original game restored.");
    return Exit(0, "The game is not patched (no backup found). Use Steam's \"Verify integrity of game files\" if needed.");
}

int Exit(int code, string? message)
{
    if (message != null) Console.WriteLine(message);
    if (interactive)
    {
        Console.WriteLine();
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
    return code;
}

sealed class Options
{
    public string? Lang, Game, File, Export, Check, Reference, PreviewImages;
    public bool Restore;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value after {args[i]}");
            switch (args[i].ToLowerInvariant())
            {
                case "--lang": o.Lang = Next(); break;
                case "--game": o.Game = Next(); break;
                case "--file": o.File = Next(); break;
                case "--restore": o.Restore = true; break;
                case "--export": o.Export = Next(); break;
                case "--check": o.Check = Next(); break;
                case "--ref": o.Reference = Next(); break;
                case "--preview-images": o.PreviewImages = Next(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return o;
    }
}
