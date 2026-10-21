using System.Reflection;
using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Microsoft.Win32;

namespace GnosiaPatcher;

/// <summary>Finds the game, keeps a backup of the original data file and writes translated copies.</summary>
sealed class GamePatcher
{
    const string DataFile = "sharedassets0.assets";
    const string BackupSuffix = ".original";

    /// <summary>MD5 of the unmodified sharedassets0.assets this patcher was built against (Steam, 2022-02 build).</summary>
    public const string KnownOriginalMd5 = "c7c6cb807aa0e80981556c5f6c3abe1a";

    /// <summary>Column of each text row that holds English; the patch replaces it.</summary>
    const int EnglishColumn = 1;

    public string DataDir { get; }
    string Target => Path.Combine(DataDir, DataFile);
    string Backup => Target + BackupSuffix;

    public GamePatcher(string dataDir) => DataDir = dataDir;

    public bool IsPatched => File.Exists(Backup);

    /// <summary>Looks for Gnosia_Data in every Steam library, then in the given extra folders.</summary>
    public static string? FindGame(IEnumerable<string>? extra = null)
    {
        var roots = new List<string>();
        if (extra != null) roots.AddRange(extra);
        if (OperatingSystem.IsWindows())
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (steam != null)
            {
                roots.Add(steam);
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        roots.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        roots.Add(@"C:\Program Files (x86)\Steam");
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in new[] { root, Path.Combine(root, "Gnosia_Data"), Path.Combine(root, "steamapps", "common", "GNOSIA", "Gnosia_Data") })
                if (File.Exists(Path.Combine(candidate, DataFile)) && Directory.Exists(Path.Combine(candidate, "Managed")))
                    return Path.GetFullPath(candidate);
        }
        return null;
    }

    static string Md5(string path)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Returns the original data file, creating the backup the first time.
    /// Warns (but continues) when the original is not the exact build the patch was made for.
    /// </summary>
    string EnsureBackup(Action<string> log)
    {
        if (!File.Exists(Backup))
        {
            log("Creating backup of the original game file...");
            File.Copy(Target, Backup);
        }
        if (Md5(Backup) != KnownOriginalMd5)
            log("WARNING: this game version is different from the one the translation was made for. Rows that no longer match are skipped.");
        return Backup;
    }

    /// <summary>
    /// Writes the translation into the game. With <paramref name="imagePreviewDir"/> nothing is written to
    /// the game: only the translated UI images are rendered there as PNG.
    /// </summary>
    public PatchResult Apply(TranslationFile translation, Action<string> log, string? imagePreviewDir = null)
    {
        string source = imagePreviewDir == null ? EnsureBackup(log) : File.Exists(Backup) ? Backup : Target;

        var am = new AssetsManager();
        using (var tpk = Assembly.GetExecutingAssembly().GetManifestResourceStream("classdata.tpk")!)
            am.LoadClassPackage(tpk);
        am.MonoTempGenerator = new MonoCecilTempGenerator(Path.Combine(DataDir, "Managed"));

        log("Reading game data...");
        var inst = am.LoadAssetsFile(source, true);
        am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);

        var result = new PatchResult();
        var used = new HashSet<string>();
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            var bf = am.GetBaseField(inst, info);
            string cls = FontPatch.ScriptName(am, inst, bf);
            if (!cls.StartsWith("Entity_")) continue;
            bool changed = false;
            var sheets = bf["sheets.Array"].Children;
            for (int s = 0; s < sheets.Count; s++)
            {
                var rows = sheets[s]["list.Array"].Children;
                string sheetKey = $"{cls[7..]}/{s}:{sheets[s]["name"].AsString}/";
                for (int r = 0; r < rows.Count; r++)
                {
                    var cells = rows[r]["text.Array"].Children;
                    if (cells.Count <= EnglishColumn) continue;
                    result.Rows++;
                    string key = sheetKey + r;
                    if (!translation.Entries.TryGetValue(key, out var text)) continue;
                    used.Add(key);
                    // a translation must keep every placeholder of the text it replaces
                    var original = cells[EnglishColumn].AsString;
                    if (TranslationFile.Placeholders(original).Any(p => !text.Contains(p))) { result.Skipped++; continue; }
                    cells[EnglishColumn].AsString = TranslationFile.MatchEdgeSpaces(original, text);
                    result.Translated++;
                    changed = true;
                }
            }
            if (changed) info.SetNewData(bf);
        }
        result.UnknownKeys = translation.Entries.Keys.Count(k => !k.StartsWith("Image/") && !used.Contains(k));

        log("Adding accented letters to the fonts...");
        var fonts = new List<SdfFont>();
        result.GlyphsAdded = FontPatch.PatchAll(am, inst, fonts);

        log("Translating text in images...");
        result.ImageTexts = ImagePatch.Apply(DataDir, translation, fonts, log, imagePreviewDir);
        if (imagePreviewDir != null) { am.UnloadAll(); return result; }

        log("Writing patched file...");
        string temp = Target + ".tmp";
        using (var writer = new AssetsFileWriter(temp)) inst.file.Write(writer);
        am.UnloadAll();
        File.Move(temp, Target, overwrite: true);
        return result;
    }

    /// <summary>
    /// Writes every text row of the original game as "key TAB english TAB japanese TAB chinese",
    /// the reference translators work from. It contains the game's own script, so it stays local.
    /// </summary>
    public int ExportReference(string outPath)
    {
        string source = File.Exists(Backup) ? Backup : Target;
        var am = new AssetsManager();
        using (var tpk = Assembly.GetExecutingAssembly().GetManifestResourceStream("classdata.tpk")!)
            am.LoadClassPackage(tpk);
        am.MonoTempGenerator = new MonoCecilTempGenerator(Path.Combine(DataDir, "Managed"));
        var inst = am.LoadAssetsFile(source, true);
        am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
        var lines = new List<string> { "# key\tenglish\tjapanese\tchinese" };
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            var bf = am.GetBaseField(inst, info);
            string cls = FontPatch.ScriptName(am, inst, bf);
            if (!cls.StartsWith("Entity_")) continue;
            var sheets = bf["sheets.Array"].Children;
            for (int s = 0; s < sheets.Count; s++)
            {
                var rows = sheets[s]["list.Array"].Children;
                for (int r = 0; r < rows.Count; r++)
                {
                    var cells = rows[r]["text.Array"].Children.Select(c => c.AsString.Replace("\r", "").Replace("\n", "\\n").Replace("\t", " ")).ToList();
                    if (cells.Count <= EnglishColumn) continue;
                    string Cell(int i) => i < cells.Count ? cells[i] : "";
                    lines.Add($"{cls[7..]}/{s}:{sheets[s]["name"].AsString}/{r}\t{Cell(1)}\t{Cell(0)}\t{Cell(2)}");
                }
            }
        }
        am.UnloadAll();
        foreach (var (key, en) in ImagePatch.ReferenceRows()) lines.Add($"{key}	{en}		");
        File.WriteAllLines(outPath, lines);
        return lines.Count - 1;
    }

    /// <summary>Puts the original file back and removes the backup.</summary>
    public bool Restore()
    {
        bool images = ImagePatch.Restore(DataDir);
        if (!File.Exists(Backup)) return images;
        File.Copy(Backup, Target, overwrite: true);
        File.Delete(Backup);
        return true;
    }
}

sealed class PatchResult
{
    public int Rows, Translated, Skipped, UnknownKeys, GlyphsAdded, ImageTexts;
}
