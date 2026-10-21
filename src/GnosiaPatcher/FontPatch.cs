using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace GnosiaPatcher;

/// <summary>
/// Adds accented Latin letters (and ¿ ¡) to the game's Latin TextMeshPro SDF fonts.
///
/// The fonts only ship with ASCII. Instead of rasterizing a new font, every missing letter is
/// composed from glyphs already in the atlas: a base letter plus a mark taken from an ASCII
/// glyph (` ^ ~ , .), scaled, mirrored or rotated as needed. Signed distance fields can be
/// combined with max(), so the composite keeps the same smooth outline the shader expects.
/// The atlas texture is doubled in height and the new glyphs are packed into the new half.
/// </summary>
static class FontPatch
{
    enum Mark { None, Grave, Acute, Circ, Tilde, Cedilla, Diaeresis }

    record Recipe(char Ch, char Base, Mark Mark, bool Rotate = false);

    static readonly Recipe[] Recipes = BuildRecipes();

    static Recipe[] BuildRecipes()
    {
        var list = new List<Recipe>();
        void Add(string chars, string bases, Mark m)
        {
            for (int i = 0; i < chars.Length; i++) list.Add(new Recipe(chars[i], bases[i], m));
        }
        Add("àèìòùÀÈÙ", "aeiouAEU", Mark.Grave);
        Add("áéíóúýÁÉÍÓÚÝ", "aeiouyAEIOUY", Mark.Acute);
        Add("âêîôûÂÊÎÔÛ", "aeiouAEIOU", Mark.Circ);
        Add("ãõñÃÕÑ", "aonAON", Mark.Tilde);
        Add("äëïöüÿÄËÏÖÜ", "aeiouyAEIOU", Mark.Diaeresis);
        Add("çÇ", "cC", Mark.Cedilla);
        list.Add(new Recipe('¿', '?', Mark.None, Rotate: true));
        list.Add(new Recipe('¡', '!', Mark.None, Rotate: true));
        return list.ToArray();
    }

    /// <summary>Characters this patch can add.</summary>
    public static IEnumerable<char> AddedCharacters => Recipes.Select(r => r.Ch);

    /// <summary>One source glyph drawn into the composite.</summary>
    record Layer(G Glyph, float Scale, float OriginX, float OriginY, bool Mirror = false, bool Rotate = false, float? ClipAbove = null);

    /// <summary>
    /// Patches every small (Latin-only) TMP font in the file. Returns the glyph count added.
    /// The resulting fonts (Latin ones falling back to the Japanese font) are added to <paramref name="fonts"/>.
    /// </summary>
    public static int PatchAll(AssetsManager am, AssetsFileInstance inst, List<SdfFont>? fonts = null)
    {
        long cjkFallback = 0;
        SdfFont? cjkFont = null;
        var latinFonts = new List<AssetFileInfo>();
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.MonoBehaviour))
        {
            var bf = am.GetBaseField(inst, info);
            if (ScriptName(am, inst, bf) != "TMP_FontAsset") continue;
            int count = bf["m_CharacterTable.Array"].Children.Count;
            if (count < 500) latinFonts.Add(info);
            // The Japanese font also covers Cyrillic; Latin fonts fall back to it for Russian.
            if (bf["m_Name"].AsString.StartsWith("cinecaption", StringComparison.OrdinalIgnoreCase))
            {
                cjkFallback = info.PathId;
                if (fonts != null) cjkFont = Read(am, inst, bf);
            }
        }
        int total = 0;
        foreach (var info in latinFonts)
        {
            total += Patch(am, inst, info, cjkFallback, out var font);
            font.Fallback = cjkFont;
            fonts?.Add(font);
        }
        if (cjkFont != null) fonts?.Add(cjkFont);
        return total;
    }

    public static string ScriptName(AssetsManager am, AssetsFileInstance inst, AssetTypeValueField bf) =>
        am.GetExtAsset(inst, bf["m_Script"]).baseField?["m_ClassName"].AsString ?? "";

    /// <summary>Reads a font without changing it.</summary>
    static SdfFont Read(AssetsManager am, AssetsFileInstance inst, AssetTypeValueField bf)
    {
        var tex = am.GetBaseField(inst, inst.file.GetAssetInfo(bf["m_AtlasTextures.Array"].Children[0]["m_PathID"].AsLong));
        var (glyphs, charToGlyph) = ReadTables(bf);
        return new SdfFont
        {
            Name = bf["m_Name"].AsString, Atlas = tex["image data"].AsByteArray, W = tex["m_Width"].AsInt, H = tex["m_Height"].AsInt,
            Pad = bf["m_AtlasPadding"].AsInt, PointSize = bf["m_FaceInfo.m_PointSize"].AsFloat,
            Glyphs = charToGlyph.Where(p => glyphs.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => glyphs[p.Value]),
        };
    }

    static (Dictionary<uint, G>, Dictionary<char, uint>) ReadTables(AssetTypeValueField bf)
    {
        var glyphs = new Dictionary<uint, G>();
        foreach (var g in bf["m_GlyphTable.Array"].Children)
        {
            var r = g["m_GlyphRect"]; var m = g["m_Metrics"];
            glyphs[g["m_Index"].AsUInt] = new G(m["m_HorizontalBearingX"].AsFloat, m["m_HorizontalBearingY"].AsFloat,
                m["m_Width"].AsFloat, m["m_Height"].AsFloat, m["m_HorizontalAdvance"].AsFloat,
                r["m_X"].AsInt, r["m_Y"].AsInt, r["m_Width"].AsInt, r["m_Height"].AsInt);
        }
        var charToGlyph = new Dictionary<char, uint>();
        foreach (var c in bf["m_CharacterTable.Array"].Children)
        {
            uint u = c["m_Unicode"].AsUInt;
            if (u <= 0xFFFF) charToGlyph[(char)u] = c["m_GlyphIndex"].AsUInt;
        }
        return (glyphs, charToGlyph);
    }

    static int Patch(AssetsManager am, AssetsFileInstance inst, AssetFileInfo fontInfo, long fallbackPathId, out SdfFont font)
    {
        var bf = am.GetBaseField(inst, fontInfo);
        int pad = bf["m_AtlasPadding"].AsInt;
        var face = bf["m_FaceInfo"];
        float pt = face["m_PointSize"].AsFloat, mean = face["m_MeanLine"].AsFloat, cap = face["m_CapLine"].AsFloat;

        var texInfo = inst.file.GetAssetInfo(bf["m_AtlasTextures.Array"].Children[0]["m_PathID"].AsLong);
        var tex = am.GetBaseField(inst, texInfo);
        int W = tex["m_Width"].AsInt, H = tex["m_Height"].AsInt;
        byte[] src = tex["image data"].AsByteArray;
        if (src.Length != W * H) throw new InvalidOperationException("Unexpected font atlas format.");

        int H2 = H * 2;
        var dst = new byte[W * H2];
        Buffer.BlockCopy(src, 0, dst, 0, src.Length);

        var glyphArr = bf["m_GlyphTable.Array"];
        var charArr = bf["m_CharacterTable.Array"];
        var (glyphs, charToGlyph) = ReadTables(bf);

        // bilinear sample of a glyph's padded atlas region, in glyph space (y up, baseline 0)
        float Sample(G g, float gx, float gy)
        {
            float ax = g.rx + (gx - g.bx) * (g.rw / Math.Max(g.w, 0.001f)) - 0.5f;
            float ay = g.ry + (gy - (g.by - g.h)) * (g.rh / Math.Max(g.h, 0.001f)) - 0.5f;
            if (ax < g.rx - pad || ay < g.ry - pad || ax > g.rx + g.rw + pad - 1 || ay > g.ry + g.rh + pad - 1) return 0;
            int x0 = (int)Math.Floor(ax), y0 = (int)Math.Floor(ay);
            float fx = ax - x0, fy = ay - y0;
            float P(int x, int y) => (x < 0 || y < 0 || x >= W || y >= H) ? 0 : src[y * W + x] / 255f;
            return (P(x0, y0) * (1 - fx) + P(x0 + 1, y0) * fx) * (1 - fy) + (P(x0, y0 + 1) * (1 - fx) + P(x0 + 1, y0 + 1) * fx) * fy;
        }

        float SampleLayer(Layer L, float gx, float gy)
        {
            if (L.ClipAbove is float clip && gy > clip) return 0;
            float lx = (gx - L.OriginX) / L.Scale, ly = (gy - L.OriginY) / L.Scale;
            if (L.Mirror) lx = L.Glyph.w - lx;
            if (L.Rotate) { lx = L.Glyph.w - lx; ly = L.Glyph.h - ly; }
            return Sample(L.Glyph, L.Glyph.bx + lx, L.Glyph.by - L.Glyph.h + ly);
        }

        uint nextIndex = glyphs.Keys.Max() + 1000;
        int cursorX = pad + 1, cursorY = H + pad + 1, rowH = 0, added = 0;

        foreach (var rcp in Recipes)
        {
            if (charToGlyph.ContainsKey(rcp.Ch) || !charToGlyph.ContainsKey(rcp.Base)) continue;
            var b = glyphs[charToGlyph[rcp.Base]];
            bool upper = char.IsUpper(rcp.Base);
            var layers = new List<Layer>();
            float advance = b.adv;

            if (rcp.Rotate)
            {
                // ¿ ¡: the source turned upside down, hanging below the baseline
                float bottom = -0.25f * b.h;
                layers.Add(new Layer(b, 1, b.bx, bottom, Rotate: true));
            }
            else
            {
                bool dotless = rcp.Base == 'i' && rcp.Mark != Mark.None;
                layers.Add(new Layer(b, 1, b.bx, b.by - b.h, ClipAbove: dotless ? mean + 0.04f * pt : null));

                char mc = rcp.Mark switch { Mark.Grave or Mark.Acute => '`', Mark.Circ => '^', Mark.Tilde => '~', Mark.Cedilla => ',', _ => '.' };
                if (!charToGlyph.ContainsKey(mc)) continue;
                var mg = glyphs[charToGlyph[mc]];
                float targetW = rcp.Mark switch
                {
                    Mark.Grave or Mark.Acute => 0.17f * pt,
                    Mark.Circ => 0.30f * pt,
                    Mark.Tilde => 0.32f * pt,
                    Mark.Cedilla => 0.13f * pt,
                    _ => 0.10f * pt,
                };
                float s = Math.Min(1.15f, targetW / Math.Max(mg.w, 1));
                if (upper && rcp.Mark != Mark.Cedilla) s *= 0.9f;
                float mw = mg.w * s, mh = mg.h * s;
                float centerX = b.bx + b.w / 2f + (rcp.Mark == Mark.Cedilla ? 0 : 0.5f);
                float bottom = rcp.Mark == Mark.Cedilla ? 0.04f * pt - mh : (upper ? cap : mean) + 0.07f * pt;
                if (rcp.Mark == Mark.Diaeresis)
                {
                    float spread = (upper ? 0.16f : 0.13f) * pt;
                    layers.Add(new Layer(mg, s, centerX - spread - mw / 2, bottom));
                    layers.Add(new Layer(mg, s, centerX + spread - mw / 2, bottom));
                }
                else
                {
                    layers.Add(new Layer(mg, s, centerX - mw / 2, bottom, Mirror: rcp.Mark == Mark.Acute));
                }
            }

            // union bounds in glyph space
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var L in layers)
            {
                float w = L.Glyph.w * L.Scale, h = L.Glyph.h * L.Scale;
                float top = L.ClipAbove is float c ? Math.Min(L.OriginY + h, c) : L.OriginY + h;
                minX = Math.Min(minX, L.OriginX); maxX = Math.Max(maxX, L.OriginX + w);
                minY = Math.Min(minY, L.OriginY); maxY = Math.Max(maxY, top);
            }
            int cw = (int)Math.Ceiling(maxX - minX), chh = (int)Math.Ceiling(maxY - minY);
            int boxW = cw + 2 * pad, boxH = chh + 2 * pad;

            if (cursorX + boxW + pad >= W) { cursorX = pad + 1; cursorY += rowH + 2; rowH = 0; }
            if (cursorY + boxH >= H2) throw new InvalidOperationException("Font atlas is full.");
            int rx = cursorX + pad, ry = cursorY + pad;

            for (int j = -pad; j < chh + pad; j++)
                for (int i = -pad; i < cw + pad; i++)
                {
                    float gx = minX + i + 0.5f, gy = minY + j + 0.5f, val = 0;
                    foreach (var L in layers) val = Math.Max(val, SampleLayer(L, gx, gy));
                    dst[(ry + j) * W + (rx + i)] = (byte)Math.Clamp((int)Math.Round(val * 255), 0, 255);
                }
            cursorX += boxW + 2;
            rowH = Math.Max(rowH, boxH);

            var ng = ValueBuilder.DefaultValueFieldFromArrayTemplate(glyphArr);
            CopyValues(glyphArr.Children.First(g => g["m_Index"].AsUInt == charToGlyph[rcp.Base]), ng);
            ng["m_Index"].AsUInt = nextIndex;
            ng["m_Metrics.m_Width"].AsFloat = cw; ng["m_Metrics.m_Height"].AsFloat = chh;
            ng["m_Metrics.m_HorizontalBearingX"].AsFloat = minX; ng["m_Metrics.m_HorizontalBearingY"].AsFloat = maxY;
            ng["m_Metrics.m_HorizontalAdvance"].AsFloat = advance;
            ng["m_GlyphRect.m_X"].AsInt = rx; ng["m_GlyphRect.m_Y"].AsInt = ry;
            ng["m_GlyphRect.m_Width"].AsInt = cw; ng["m_GlyphRect.m_Height"].AsInt = chh;
            glyphArr.Children.Add(ng);

            var nc = ValueBuilder.DefaultValueFieldFromArrayTemplate(charArr);
            CopyValues(charArr.Children.First(c => c["m_Unicode"].AsUInt == rcp.Base), nc);
            nc["m_Unicode"].AsUInt = rcp.Ch; nc["m_GlyphIndex"].AsUInt = nextIndex;
            charArr.Children.Add(nc);
            glyphs[nextIndex] = new G(minX, maxY, cw, chh, advance, rx, ry, cw, chh);
            charToGlyph[rcp.Ch] = nextIndex;
            nextIndex++; added++;
        }

        // Cyrillic and other scripts: fall back to the Japanese font, which covers them.
        if (fallbackPathId != 0)
        {
            var fb = bf["m_FallbackFontAssetTable.Array"];
            if (!fb.Children.Any(p => p["m_PathID"].AsLong == fallbackPathId))
            {
                var p = ValueBuilder.DefaultValueFieldFromArrayTemplate(fb);
                p["m_FileID"].AsInt = 0; p["m_PathID"].AsLong = fallbackPathId;
                fb.Children.Add(p);
            }
        }

        font = new SdfFont
        {
            Name = bf["m_Name"].AsString, Atlas = dst, W = W, H = H2, Pad = pad, PointSize = pt,
            Glyphs = charToGlyph.Where(p => glyphs.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => glyphs[p.Value]),
        };

        bf["m_AtlasHeight"].AsInt = H2;
        fontInfo.SetNewData(bf);

        tex["m_Height"].AsInt = H2;
        tex["m_CompleteImageSize"].AsInt = dst.Length;
        tex["image data"].AsByteArray = dst;
        texInfo.SetNewData(tex);

        // keep the material's texture size property in sync
        var matPtr = bf["material"].IsDummy ? bf["m_Material"] : bf["material"];
        var matInfo = matPtr.IsDummy ? null : inst.file.GetAssetInfo(matPtr["m_PathID"].AsLong);
        if (matInfo != null)
        {
            var mat = am.GetBaseField(inst, matInfo);
            foreach (var prop in mat["m_SavedProperties.m_Floats.Array"].Children)
                if (prop["first"].AsString == "_TextureHeight") prop["second"].AsFloat = H2;
            matInfo.SetNewData(mat);
        }
        return added;
    }

    static void CopyValues(AssetTypeValueField from, AssetTypeValueField to)
    {
        if (from.Value != null && to.Value != null) to.Value = new AssetTypeValue(from.Value.ValueType, from.Value.AsObject);
        for (int i = 0; i < from.Children.Count && i < to.Children.Count; i++) CopyValues(from.Children[i], to.Children[i]);
    }
}
