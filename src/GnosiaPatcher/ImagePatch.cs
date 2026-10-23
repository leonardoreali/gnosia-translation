using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace GnosiaPatcher;

/// <summary>
/// Translates text that is painted into the game's UI images (menus, buttons, title screen...).
///
/// translations/images.json lists, for each image, the boxes that hold English text. For each box the
/// patcher rebuilds the background without the text (by interpolating the pixels at the box edges),
/// measures the English text to recover its size, baseline, color and stroke weight, and then draws the
/// translation in the same style with the game's own SDF font. The images live in the English
/// Addressables bundles; they are backed up and rewritten, and the Addressables catalog is updated so
/// the game accepts the modified bundles.
/// </summary>
static class ImagePatch
{
    const string BackupSuffix = ".original";
    const int RGBA32 = 4;

    public sealed class Spec { public List<TextureSpec> Textures { get; set; } = new(); }

    public sealed class TextureSpec
    {
        public string Bundle { get; set; } = "";
        public List<string> Textures { get; set; } = new();
        public List<Item> Items { get; set; } = new();
    }

    public sealed class Item
    {
        public string Id { get; set; } = "";
        public string En { get; set; } = "";
        public int[] Box { get; set; } = Array.Empty<int>();
        public string Align { get; set; } = "auto";      // left, right, center, auto
        public string Erase { get; set; } = "rows";      // rows, cols, clear, copy
        public int[]? From { get; set; }                  // copy: top-left of a clean area of the same size
        public float? Size { get; set; }                  // em size in pixels (default: measured from the English text)
        public float? Baseline { get; set; }              // first baseline, image y (default: measured)
        public float? LineHeight { get; set; }
        public float? Dilate { get; set; }
        public string? Color { get; set; }                // #rrggbb[aa] (default: measured)
        public string? Accent { get; set; }               // color of [bracketed] words
        public bool Wrap { get; set; }
        public int[][]? Skip { get; set; }                // areas inside the box to leave alone (x, y, w, h), e.g. a key icon
        public int[]? Ink { get; set; }                   // where the English text is (x0, y0, x1, y1), when detection needs help
        public string? Font { get; set; }                 // game font to draw with, by name prefix (default Dosis)
        public string? Glow { get; set; }                 // color of a soft glow around the letters
        public float GlowRadius { get; set; } = 3;
        public bool Manual { get; set; }                  // use Ink as the exact text area (needs Size and Color)
        public bool Enclosed { get; set; }                // only shapes fully inside the box are text (box crosses borders)
        public int Cut { get; set; }                      // ignore this many pixels of the box's bottom-right corner (slanted button edge)
        public bool FitInk { get; set; }                  // no wider than the English text (the game shows only that part of the image)
        public int? WrapWidth { get; set; }               // narrower lines than the box (the game draws a button over part of it)
        public int[][]? ClearArea { get; set; }           // polygon (image x, y points) to make transparent instead of the text's rectangle:
                                                          // the inside of a banner whose slanted text crosses its outline
        public int[][]? Outline { get; set; }             // closed polygon redrawn as a line after clearing (the banner's border)
        public float OutlineWidth { get; set; } = 5;
        public bool EraseOnly { get; set; }               // only removes this part of the English (another item draws the translation)
        public bool Redraw { get; set; }                  // draw even when the translation equals the English (to match its neighbours' font)
    }

    public static string Key(Item it) => "Image/" + it.Id;

    public static Spec Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("images.json")!;
        return JsonSerializer.Deserialize<Spec>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
    }

    /// <summary>Distinct (key, English) pairs, for the translators' reference file.</summary>
    public static IEnumerable<(string Key, string En)> ReferenceRows() =>
        Load().Textures.SelectMany(t => t.Items).GroupBy(Key).Select(g => (g.Key, g.First().En));

    static string BundleDir(string dataDir) => Path.Combine(dataDir, "StreamingAssets", "aa", "StandaloneWindows64");

    static string? FindBundle(string dataDir, string name) =>
        Directory.Exists(BundleDir(dataDir))
            ? Directory.GetFiles(BundleDir(dataDir), name + "_assets_all_*.bundle").FirstOrDefault()
            : null;

    /// <summary>
    /// Draws the translation into the images. With <paramref name="previewDir"/> the game is not touched:
    /// the translated images are written there as PNG. Returns the number of text boxes drawn.
    /// </summary>
    public static int Apply(string dataDir, TranslationFile translation, List<SdfFont> fonts, Action<string> log, string? previewDir = null)
    {
        var spec = Load();
        SdfFont FontFor(Item it) => fonts.FirstOrDefault(f => f.Name.StartsWith(it.Font ?? "Dosis", StringComparison.OrdinalIgnoreCase)) ?? fonts.First();
        if (previewDir == null) Restore(dataDir);
        int drawn = 0;
        bool anyBundle = false;

        foreach (var bundleGroup in spec.Textures.GroupBy(t => t.Bundle))
        {
            string? path = FindBundle(dataDir, bundleGroup.Key);
            if (path == null) { log($"  (image bundle {bundleGroup.Key} not found, skipped)"); continue; }
            if (!bundleGroup.SelectMany(t => t.Items).Any(it => Translated(translation, it) != null)) continue;

            var am = new AssetsManager();
            using (var tpk = Assembly.GetExecutingAssembly().GetManifestResourceStream("classdata.tpk")!)
                am.LoadClassPackage(tpk);
            var bun = am.LoadBundleFile(File.Exists(path + BackupSuffix) ? path + BackupSuffix : path, true);
            bool changedBundle = false;
            var names = bun.file.GetAllFileNames();
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i].EndsWith(".resS") || names[i].EndsWith(".resource")) continue;
                var inst = am.LoadAssetsFileFromBundle(bun, i, false);
                am.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);
                bool changedFile = false;
                foreach (var info in inst.file.GetAssetsOfType(AssetClassID.Texture2D))
                {
                    var bf = am.GetBaseField(inst, info);
                    string texName = bf["m_Name"].AsString;
                    var texSpecs = bundleGroup.Where(t => t.Textures.Contains(texName)).ToList();
                    var items = texSpecs.SelectMany(t => t.Items).Where(it => Translated(translation, it) != null).ToList();
                    if (items.Count == 0) continue;

                    var tf = TextureFile.ReadTextureFile(bf);
                    if (!tf.SetPictureDataFromBundle(bun)) tf.FillPictureData(inst);
                    var img = Img.FromRgba(tf.DecodeTextureRaw(tf.pictureData, false), tf.m_Width, tf.m_Height);
                    foreach (var it in items)
                    {
                        try { if (DrawItem(img, it, Translated(translation, it)!, FontFor(it), log)) drawn++; }
                        catch (Exception e) { log($"  image text \"{it.Id}\" skipped: {e.Message}"); }
                    }
                    if (previewDir != null)
                    {
                        Directory.CreateDirectory(previewDir);
                        Png.Write(Path.Combine(previewDir, texName + ".png"), img.ToRgba(flip: false), img.W, img.H);
                        continue;
                    }
                    var data = img.ToRgba(flip: true);   // Unity stores rows bottom-up
                    bf["m_TextureFormat"].AsInt = RGBA32;
                    bf["m_CompleteImageSize"].AsInt = data.Length;
                    bf["m_MipCount"].AsInt = 1;
                    bf["image data"].AsByteArray = data;
                    bf["m_StreamData.offset"].AsULong = 0;
                    bf["m_StreamData.size"].AsUInt = 0;
                    bf["m_StreamData.path"].AsString = "";
                    info.SetNewData(bf);
                    changedFile = true;
                }
                if (changedFile)
                {
                    bun.file.BlockAndDirInfo.DirectoryInfos[i].SetNewData(inst.file);
                    changedBundle = true;
                }
            }
            if (changedBundle && previewDir == null)
            {
                string backup = path + BackupSuffix;
                if (!File.Exists(backup)) File.Copy(path, backup);
                // write uncompressed, then pack with LZ4 like the original (otherwise the file grows ~4x)
                string temp = path + ".tmp", packed = path + ".lz4";
                using (var w = new AssetsFileWriter(temp)) bun.file.Write(w);
                am.UnloadAll(true);
                var tmpBundle = new AssetBundleFile();
                using (var r = new AssetsFileReader(File.OpenRead(temp)))
                {
                    tmpBundle.Read(r);
                    using var w = new AssetsFileWriter(packed);
                    tmpBundle.Pack(w, AssetBundleCompressionType.LZ4);
                }
                tmpBundle.Close();
                File.Delete(temp);
                File.Move(packed, path, overwrite: true);
                anyBundle = true;
            }
            else am.UnloadAll(true);
        }
        if (anyBundle) PatchCatalog(dataDir);
        return drawn;
    }

    /// <summary>The translation of a box, with quotes unescaped (game text stores them as \").</summary>
    static string? Translated(TranslationFile t, Item it) =>
        t.Entries.TryGetValue(Key(it), out var s) && s.Length > 0 && (s != it.En || it.Redraw) ? s.Replace("\\\"", "\"") : null;

    /// <summary>Puts back every original bundle and catalog file. Returns whether anything was restored.</summary>
    public static bool Restore(string dataDir)
    {
        var dir = BundleDir(dataDir);
        if (!Directory.Exists(dir)) return false;
        bool any = false;
        foreach (var backup in Directory.GetFiles(dir, "*" + BackupSuffix))
        {
            File.Copy(backup, backup[..^BackupSuffix.Length], overwrite: true);
            File.Delete(backup);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// The catalog stores a CRC per bundle and the game would reject a modified bundle. The CRCs are set to 0
    /// (no check) without changing the catalog's length, and the catalog hash is changed so the game
    /// reloads it instead of using the copy it cached in AppData.
    /// </summary>
    static void PatchCatalog(string dataDir)
    {
        var dir = BundleDir(dataDir);
        var json = Directory.GetFiles(dir, "catalog_*.json").FirstOrDefault();
        if (json == null) return;
        var hash = Path.ChangeExtension(json, ".hash");
        foreach (var f in new[] { json, hash })
            if (File.Exists(f) && !File.Exists(f + BackupSuffix)) File.Copy(f, f + BackupSuffix);

        string text = File.ReadAllText(json + BackupSuffix);
        var m = Regex.Match(text, "\"m_ExtraDataString\":\"([^\"]*)\"");
        if (!m.Success) return;
        // the extra data is binary with UTF-16 JSON inside: edit the digits in place, byte by byte
        var bytes = Convert.FromBase64String(m.Groups[1].Value);
        var key = Encoding.Unicode.GetBytes("\"m_Crc\":");
        for (int i = 0; i + key.Length < bytes.Length; i++)
        {
            if (!bytes.AsSpan(i, key.Length).SequenceEqual(key)) continue;
            bool first = true;
            for (int p = i + key.Length; p + 1 < bytes.Length && bytes[p + 1] == 0 && bytes[p] >= '0' && bytes[p] <= '9'; p += 2)
            {
                bytes[p] = first ? (byte)'0' : (byte)' ';
                first = false;
            }
        }
        var patched = Convert.ToBase64String(bytes);
        text = text[..m.Groups[1].Index] + patched + text[(m.Groups[1].Index + m.Groups[1].Length)..];
        File.WriteAllText(json, text);
        if (File.Exists(hash))
            File.WriteAllText(hash, Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    }

    // ---------------------------------------------------------------- drawing

    /// <summary>Premultiplied RGBA image, rows top-down.</summary>
    sealed class Img
    {
        public int W, H;
        public float[] P = Array.Empty<float>();

        public static Img FromRgba(byte[] rgba, int w, int h)
        {
            var img = new Img { W = w, H = h, P = new float[w * h * 4] };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = ((h - 1 - y) * w + x) * 4, d = (y * w + x) * 4;   // decoded data is bottom-up
                    float a = rgba[s + 3] / 255f;
                    img.P[d] = rgba[s] / 255f * a; img.P[d + 1] = rgba[s + 1] / 255f * a; img.P[d + 2] = rgba[s + 2] / 255f * a; img.P[d + 3] = a;
                }
            return img;
        }

        public byte[] ToRgba(bool flip)
        {
            var o = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int s = (y * W + x) * 4, d = ((flip ? H - 1 - y : y) * W + x) * 4;
                    float a = P[s + 3];
                    for (int c = 0; c < 3; c++) o[d + c] = (byte)Math.Clamp((int)Math.Round(a > 0 ? P[s + c] / a * 255 : 0), 0, 255);
                    o[d + 3] = (byte)Math.Clamp((int)Math.Round(a * 255), 0, 255);
                }
            return o;
        }
    }

    static float[] ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        float C(int i) => Convert.ToInt32(hex.Substring(i, 2), 16) / 255f;
        float a = hex.Length >= 8 ? C(6) : 1;
        return new[] { C(0) * a, C(2) * a, C(4) * a, a };
    }

    /// <summary>A word of the translation, split into plain and [accented] runs.</summary>
    sealed record Word(string Text, List<(string Text, bool Accent)> Runs);

    /// <summary>Splits a line into words; text between [ and ] is accented, even across several words.</summary>
    static List<Word> Words(string s)
    {
        var words = new List<Word>();
        bool accent = false;
        foreach (var token in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var runs = new List<(string, bool)>();
            var cur = new StringBuilder();
            foreach (char ch in token)
            {
                if (ch != '[' && ch != ']') { cur.Append(ch); continue; }
                if (cur.Length > 0) { runs.Add((cur.ToString(), accent)); cur.Clear(); }
                accent = ch == '[';
            }
            if (cur.Length > 0) runs.Add((cur.ToString(), accent));
            words.Add(new Word(string.Concat(runs.Select(r => r.Item1)), runs));
        }
        return words;
    }

    static string Plain(string s) => s.Replace("[", "").Replace("]", "");

    static bool DrawItem(Img img, Item it, string text, SdfFont font, Action<string> log)
    {
        int bx = it.Box[0], by = it.Box[1], bw = Math.Min(it.Box[2], img.W - bx), bh = Math.Min(it.Box[3], img.H - by);
        int n = bw * bh;
        float Px(int x, int y, int c) => img.P[((by + y) * img.W + bx + x) * 4 + c];
        bool Cut(int x, int y) => (bw - 1 - x) + (bh - 1 - y) < it.Cut
            || it.Skip != null && it.Skip.Any(s => bx + x >= s[0] && bx + x < s[0] + s[2] && by + y >= s[1] && by + y < s[1] + s[3]);

        // 1. find the English text: compare each pixel with a horizontal running median, which follows
        //    gradients and stripes but ignores thin letter strokes ("clear": text on transparency)
        var diff = new float[n];
        const int half = 18;
        var window = new List<float>(2 * half + 1);
        var med = new float[4];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (it.Erase == "clear") { med[c] = 0; continue; }
                    window.Clear();
                    for (int k = Math.Max(0, x - half); k <= Math.Min(bw - 1, x + half); k++) window.Add(Px(k, y, c));
                    window.Sort();
                    med[c] = window[window.Count / 2];
                }
                float d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(Px(x, y, c) - med[c]));
                // text on a button is opaque: transparent corners of the box are not text
                if (it.Erase != "clear" && Math.Min(Px(x, y, 3), med[3]) < 0.5f || Cut(x, y)) d = 0;
                diff[y * bw + x] = d;
            }
        if (it.Ink is int[] ink)
            for (int i = 0; i < n; i++)
            {
                int x = bx + i % bw, y = by + i / bw;
                if (x < ink[0] || x > ink[2] || y < ink[1] || y > ink[3]) diff[i] = 0;
            }
        var rawDiff = (float[])diff.Clone();
        if (it.Enclosed) KeepEnclosedShapes(diff, bw, bh, 0.45f * diff.Max(), (x, y) => Cut(x, y) || it.Erase != "clear" && Px(x, y, 3) < 0.5f);
        if (Environment.GetEnvironmentVariable("GNOSIA_IMAGE_DEBUG") is string dbg && Directory.Exists(dbg))
        {
            var o = new byte[n * 2 * 4];
            for (int i = 0; i < n; i++)
            {
                int y = i / bw, x = i % bw;
                byte a = (byte)Math.Clamp(rawDiff[i] * 255, 0, 255), k = (byte)Math.Clamp(diff[i] * 255, 0, 255);
                int p = (y * bw * 2 + x) * 4, q = (y * bw * 2 + bw + x) * 4;
                o[p] = o[p + 1] = o[p + 2] = a; o[p + 3] = 255;
                o[q] = o[q + 1] = o[q + 2] = k; o[q + 3] = 255;
            }
            Png.Write(Path.Combine(dbg, $"{it.Id}_{bx}_{by}.png"), o, bw * 2, bh);
        }
        float maxd = diff.Max();
        // a manual item may add text where the image had none (its area, size and color are given)
        if (maxd < 0.08f && !it.Manual) { log($"  image text \"{it.Id}\": no text found in its box"); return false; }
        var cov = diff.Select(d => { float v = Math.Clamp(d / (maxd * 0.9f), 0, 1); return v < 0.1f ? 0 : v; }).ToArray();
        int l = bw, r = -1, t = bh, b = -1, el = bw, er = -1, et = bh, eb = -1;
        var paint = new float[4]; int pn = 0;
        for (int i = 0; i < n; i++)
        {
            int y = i / bw, x = i % bw;
            if (cov[i] >= 0.5f) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); }
            if (cov[i] >= 0.12f) { el = Math.Min(el, x); er = Math.Max(er, x); et = Math.Min(et, y); eb = Math.Max(eb, y); }
            if (diff[i] >= maxd * 0.85f) { for (int c = 0; c < 4; c++) paint[c] += Px(x, y, c); pn++; }
        }
        for (int c = 0; c < 4; c++) paint[c] /= pn;
        el = Math.Max(0, el - 3); er = Math.Min(bw - 1, er + 3); et = Math.Max(0, et - 3); eb = Math.Min(bh - 1, eb + 3);
        if (it.Manual && it.Ink is int[] m)
        {
            // trust the given text area and size instead of what was detected (busy backgrounds)
            el = l = m[0] - bx; er = r = m[2] - bx; et = t = m[1] - by; eb = b = m[3] - by;
        }

        // 2. background without the text: rebuild the text's area from the pixels around it
        var bg = new float[n * 4];
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
                for (int c = 0; c < 4; c++)
                {
                    bool inside = it.ClearArea != null ? InPolygon(it.ClearArea, bx + x + 0.5f, by + y + 0.5f)
                                                       : x >= el && x <= er && y >= et && y <= eb && !Cut(x, y);
                    int rx = Math.Min(bw - 1, er + 1);
                    while (rx > el && Cut(rx, y)) rx--;
                    int lx = Math.Max(0, el - 1);
                    if (Cut(lx, y)) lx = rx;
                    if (Cut(rx, y)) rx = lx;
                    float v = !inside && it.Erase != "copy" ? Px(x, y, c) : it.Erase switch
                    {
                        "none" or "inpaint" => Px(x, y, c),
                        "clear" => 0,
                        "copy" => img.P[((it.From![1] + y) * img.W + it.From[0] + x) * 4 + c],
                        "cols" => Lerp(Px(x, Math.Max(0, et - 1), c), Px(x, Math.Min(bh - 1, eb + 1), c), (y - et + 1) / (float)(eb - et + 2)),
                        _ => Lerp(Px(lx, y, c), Px(rx, y, c), (x - el + 1) / (float)Math.Max(1, rx - el + 1)),
                    };
                    bg[(y * bw + x) * 4 + c] = v;
                }
        if (it.Erase == "inpaint")
        {
            // textured backgrounds: replace only the letters (and their glow), filled in from the pixels around them
            var hole = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int y = i / bw, x = i % bw;
                if (cov[i] <= 0 || x < el || x > er || y < et || y > eb) continue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        // transparent pixels around the panel stay transparent
                        if (nx >= 0 && ny >= 0 && nx < bw && ny < bh && bg[(ny * bw + nx) * 4 + 3] >= 0.5f) hole[ny * bw + nx] = true;
                    }
            }
            Inpaint(bg, hole, bw, bh);
        }
        if (it.Color != null) paint = ParseColor(it.Color);
        var accent = it.Accent != null ? ParseColor(it.Accent) : paint;

        // 3. size, baseline, letter spacing and weight of the English text
        string[] enLines = Plain(it.En).Split("\\n");
        var me = font.Measure(enLines[0]);
        bool multi = enLines.Length > 1 || it.Wrap;
        var mLast = font.Measure(enLines[^1]);
        int extra = enLines.Length - 1;   // a known line height lets the size be measured on several lines
        float size = it.Size ?? (b - t + 1 - extra * (it.LineHeight ?? 0)) / Math.Max(me.t - mLast.b, 0.01f);
        float lineH = it.LineHeight ?? size * 1.25f;
        float baseline = it.Baseline is float bl ? bl - by : b + 1 + mLast.b * size - extra * lineH;
        float squeeze = multi ? 1 : Math.Clamp((r - l + 1) / Math.Max((me.r - me.l) * size, 1), 0.85f, 1.15f);
        float dilate = it.Dilate ?? -0.3f;   // the UI images use a lighter cut than the game's Dosis Book
        if (Environment.GetEnvironmentVariable("GNOSIA_IMAGE_DEBUG") != null)
            log($"  {it.Id} @{bx},{by}: ink x {bx + l}-{bx + r} y {by + t}-{by + b}, erase x {bx + el}-{bx + er} y {by + et}-{by + eb}, size {size:0.0} squeeze {squeeze:0.00}");

        // 4. lay out the translation
        int margin = it.Enclosed ? 8 : 3;
        string align = it.Align;
        if (align == "auto") align = l < (bw - 1 - r) * 0.5f ? "left" : (bw - 1 - r) < l * 0.5f ? "right" : "center";
        // room from the anchor the text keeps (where the English started, ended or was centered) to the box edge
        float mid = (l + r + 1) / 2f;
        float avail = align switch
        {
            "left" => bw - margin - l,
            "right" => r + 1 - margin,
            _ => 2 * Math.Min(mid - margin, bw - margin - mid),
        };
        if (it.FitInk) avail = Math.Min(avail, r - l + 1 + 4);
        if (it.WrapWidth is int ww) avail = Math.Min(avail, ww);
        if (it.EraseOnly) text = "";
        var paragraphs = text.Split("\\n").Select(Words).ToList();
        List<List<Word>> lines = paragraphs;
        if (it.Wrap)
        {
            // wrap to the box; if the text then runs past the bottom, shrink it a little and try again
            for (int tries = 0; tries < 10; tries++)
            {
                lines = paragraphs.SelectMany(p => Wrap(p, font, size * squeeze, avail)).ToList();
                if (baseline + (lines.Count - 1) * lineH + 0.25f * size <= bh) break;
                size *= 0.95f; lineH *= 0.95f;
            }
        }
        float widest = lines.Max(ws => LineWidth(font, ws) * size * squeeze);
        if (widest > avail)
        {
            float k = avail / widest;
            if (squeeze * k >= 0.78f) squeeze *= k;
            else { float s2 = squeeze * k / 0.78f; size *= s2; squeeze = 0.78f; }
        }
        if (!multi)
        {
            // accents and descenders (Õ, Ç) the English did not have must stay inside the box:
            // move the line up or down into free room, and shrink it only when it is taller than the box
            var mt = font.Measure(string.Join(" ", lines[0].Select(w => w.Text)));
            float top = baseline - mt.t * size, bottom = baseline - mt.b * size, lo = 2, hi = bh - 3;
            if (bottom - top > hi - lo)
            {
                float k = (hi - lo) / (bottom - top);
                size *= k; top = baseline - mt.t * size; bottom = baseline - mt.b * size;
            }
            if (bottom > hi) baseline -= bottom - hi;
            else if (top < lo) baseline += lo - top;
        }
        var mask = new float[n]; var amask = new float[n];
        float space = font.Advance(" ") * size * squeeze;
        for (int li = 0; li < lines.Count; li++)
        {
            string plain = string.Join(" ", lines[li].Select(w => w.Text));
            var mt = font.Measure(plain);
            float w = (mt.r - mt.l) * size * squeeze;
            float inkL = align switch { "left" => l, "right" => r + 1 - w, _ => (l + r + 1) / 2f - w / 2 };
            float pen = inkL - mt.l * size * squeeze, y0 = baseline + li * lineH;
            foreach (var word in lines[li])
            {
                foreach (var (run, isAccent) in word.Runs)
                    pen += font.Draw(isAccent ? amask : mask, bw, bh, run, pen, y0, size, squeeze, dilate);
                pen += space;
            }
        }

        // optional soft glow around the letters (highlighted menu labels)
        float[]? glow = null, glowColor = null;
        if (it.Glow != null)
        {
            glowColor = ParseColor(it.Glow);
            glow = Blur(Blur(mask.Zip(amask, Math.Max).ToArray(), bw, bh, (int)it.GlowRadius), bw, bh, (int)it.GlowRadius);
            for (int i = 0; i < n; i++) glow[i] = Math.Clamp(glow[i] * 2.2f, 0, 1);
        }

        // 5. compose: background, redrawn border, glow, then text, in premultiplied color
        for (int i = 0; i < n; i++)
        {
            int y = i / bw, x = i % bw, o = ((by + y) * img.W + bx + x) * 4;
            if (Cut(x, y)) continue;
            float line = it.Outline == null ? 0 : Math.Clamp(it.OutlineWidth / 2 - DistanceToOutline(it.Outline, bx + x + 0.5f, by + y + 0.5f) + 0.5f, 0, 1);
            for (int c = 0; c < 4; c++)
            {
                float v = bg[i * 4 + c];
                v += (paint[c] - v) * line;
                if (glow != null) v += (glowColor![c] - v) * glow[i];
                v += (paint[c] - v) * mask[i];
                v += (accent[c] - v) * amask[i];
                img.P[o + c] = v;
            }
        }
        return true;
    }

    /// <summary>
    /// Keeps only the shapes (connected groups of differing pixels) that lie entirely inside the box:
    /// letters do, while button borders and other decorations run across the box edge.
    /// </summary>
    static void KeepEnclosedShapes(float[] diff, int bw, int bh, float on, Func<int, int, bool> blocked)
    {
        var label = new int[diff.Length];
        var stack = new Stack<int>();
        var members = new List<int>();
        int next = 0;
        for (int start = 0; start < diff.Length; start++)
        {
            if (diff[start] < on || label[start] != 0) continue;
            next++; members.Clear();
            bool touchesEdge = false;
            stack.Push(start); label[start] = next;
            while (stack.Count > 0)
            {
                int i = stack.Pop(); members.Add(i);
                int x = i % bw, y = i / bw;
                if (x == 0 || y == 0 || x == bw - 1 || y == bh - 1) touchesEdge = true;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                        if (blocked(nx, ny)) { touchesEdge = true; continue; }
                        int j = ny * bw + nx;
                        if (label[j] == 0 && diff[j] >= on) { label[j] = next; stack.Push(j); }
                    }
            }
            if (touchesEdge) foreach (int i in members) { diff[i] = 0; label[i] = -1; }
        }
        // faint pixels count only next to a kept shape (antialiasing of the letters)
        for (int i = 0; i < diff.Length; i++)
        {
            if (label[i] > 0 || diff[i] == 0) continue;
            int x = i % bw, y = i / bw;
            bool near = false;
            for (int dy = -2; dy <= 2 && !near; dy++)
                for (int dx = -2; dx <= 2 && !near; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < bw && ny < bh && label[ny * bw + nx] > 0) near = true;
                }
            if (!near) diff[i] = 0;
        }
    }

    /// <summary>
    /// Fills the hole pixels of an RGBA image layer by layer from the outside in, each one taking the average of
    /// its already known neighbours, so the surrounding texture reaches into the hole instead of a smooth gradient.
    /// </summary>
    static void Inpaint(float[] px, bool[] hole, int w, int h)
    {
        var todo = (bool[])hole.Clone();
        var layer = new List<(int I, float R, float G, float B, float A)>();
        while (true)
        {
            layer.Clear();
            for (int i = 0; i < todo.Length; i++)
            {
                if (!todo[i]) continue;
                int x = i % w, y = i / w, k = 0;
                float r = 0, g = 0, b = 0, a = 0;
                // opaque neighbours first: a transparent edge next to the hole must not fade it
                for (int pass = 0; pass < 2 && k == 0; pass++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h || todo[ny * w + nx]) continue;
                            int o = (ny * w + nx) * 4;
                            if (pass == 0 && px[o + 3] < 0.9f) continue;
                            r += px[o]; g += px[o + 1]; b += px[o + 2]; a += px[o + 3]; k++;
                        }
                if (k > 0) layer.Add((i, r / k, g / k, b / k, a / k));
            }
            if (layer.Count == 0) return;
            foreach (var (i, r, g, b, a) in layer)
            {
                px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a;
                todo[i] = false;
            }
        }
    }

    /// <summary>Distance from a point to the nearest edge of a closed polygon given as [x, y] pairs.</summary>
    static float DistanceToOutline(int[][] poly, float x, float y)
    {
        float best = float.MaxValue;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            float ax = poly[j][0], ay = poly[j][1], dx = poly[i][0] - ax, dy = poly[i][1] - ay;
            float t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / Math.Max(dx * dx + dy * dy, 1e-6f), 0, 1);
            float ex = ax + t * dx - x, ey = ay + t * dy - y;
            best = Math.Min(best, MathF.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    /// <summary>Even-odd test of a point against a polygon given as [x, y] pairs.</summary>
    static bool InPolygon(int[][] poly, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i][1] > y) != (poly[j][1] > y) &&
                x < (poly[j][0] - poly[i][0]) * (y - poly[i][1]) / (float)(poly[j][1] - poly[i][1]) + poly[i][0])
                inside = !inside;
        return inside;
    }

    static float LineWidth(SdfFont font, List<Word> words)
    {
        var m = font.Measure(string.Join(" ", words.Select(w => w.Text)));
        return m.r - m.l;
    }

    /// <summary>Greedy word wrap of one paragraph.</summary>
    static List<List<Word>> Wrap(List<Word> words, SdfFont font, float scale, float avail)
    {
        var lines = new List<List<Word>>();
        var cur = new List<Word>();
        foreach (var word in words)
        {
            if (cur.Count > 0 && LineWidth(font, cur.Append(word).ToList()) * scale > avail) { lines.Add(cur); cur = new List<Word>(); }
            cur.Add(word);
        }
        lines.Add(cur);
        return lines;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Separable box blur of a mask.</summary>
    static float[] Blur(float[] m, int w, int h, int r)
    {
        var tmp = new float[m.Length]; var o = new float[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int c = 0;
                for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) { s += m[y * w + k]; c++; }
                tmp[y * w + x] = s / c;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int c = 0;
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) { s += tmp[k * w + x]; c++; }
                o[y * w + x] = s / c;
            }
        return o;
    }
}
