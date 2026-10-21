namespace GnosiaPatcher;

/// <summary>Metrics of one glyph in a TextMeshPro atlas (glyph units, y up from the baseline) and its atlas rect.</summary>
record G(float bx, float by, float w, float h, float adv, int rx, int ry, int rw, int rh);

/// <summary>
/// A TextMeshPro signed-distance-field font read from the game, used to draw translated text into
/// the game's UI images with the same typeface the game itself uses.
/// </summary>
sealed class SdfFont
{
    public required string Name;
    public required byte[] Atlas;   // Alpha8, bottom-up rows
    public required int W, H, Pad;
    public required float PointSize;
    public required Dictionary<char, G> Glyphs;
    public SdfFont? Fallback;

    G? Find(char c, out SdfFont font)
    {
        for (var f = this; f != null; f = f.Fallback)
            if (f.Glyphs.TryGetValue(c, out var g)) { font = f; return g; }
        font = this;
        return null;
    }

    /// <summary>Ink bounds of a string at point size 1: left, bottom, right, top (y up, baseline 0).</summary>
    public (float l, float b, float r, float t) Measure(string s)
    {
        float pen = 0, l = float.MaxValue, b = float.MaxValue, r = float.MinValue, t = float.MinValue;
        foreach (char c in s)
        {
            var g = Find(c, out var f);
            float k = 1 / f.PointSize;
            if (g == null) { pen += 0.25f; continue; }
            if (g.w > 0 && g.h > 0)
            {
                l = Math.Min(l, pen + g.bx * k); r = Math.Max(r, pen + (g.bx + g.w) * k);
                b = Math.Min(b, (g.by - g.h) * k); t = Math.Max(t, g.by * k);
            }
            pen += g.adv * k;
        }
        return l == float.MaxValue ? (0, 0, 0, 0) : (l, b, r, t);
    }

    /// <summary>Pen advance of a string at point size 1.</summary>
    public float Advance(string s)
    {
        float pen = 0;
        foreach (char c in s) { var g = Find(c, out var f); pen += g == null ? 0.25f : g.adv / f.PointSize; }
        return pen;
    }

    float Sample(G g, float gx, float gy)
    {
        float ax = g.rx + (gx - g.bx) * (g.rw / Math.Max(g.w, 0.001f)) - 0.5f;
        float ay = g.ry + (gy - (g.by - g.h)) * (g.rh / Math.Max(g.h, 0.001f)) - 0.5f;
        if (ax < g.rx - Pad || ay < g.ry - Pad || ax > g.rx + g.rw + Pad - 1 || ay > g.ry + g.rh + Pad - 1) return 0;
        int x0 = (int)Math.Floor(ax), y0 = (int)Math.Floor(ay);
        float fx = ax - x0, fy = ay - y0;
        float P(int x, int y) => (x < 0 || y < 0 || x >= W || y >= H) ? 0 : Atlas[y * W + x] / 255f;
        return (P(x0, y0) * (1 - fx) + P(x0 + 1, y0) * fx) * (1 - fy) + (P(x0, y0 + 1) * (1 - fx) + P(x0 + 1, y0 + 1) * fx) * fy;
    }

    /// <summary>
    /// Draws a string into a coverage mask (top-down rows). (x, baseline) is the pen origin in mask pixels,
    /// size is the em size in pixels, squeeze narrows the text horizontally, dilate thickens strokes (pixels).
    /// </summary>
    public float Draw(float[] mask, int mw, int mh, string s, float x, float baseline, float size, float squeeze = 1, float dilate = 0)
    {
        float pen = 0;
        foreach (char c in s)
        {
            var g = Find(c, out var f);
            float k = size / f.PointSize;          // mask pixels per glyph unit (vertical)
            float kx = k * squeeze;
            if (g == null) { pen += 0.25f * size * squeeze; continue; }
            if (g.w > 0 && g.h > 0)
            {
                float spread = f.Pad;              // SDF covers ±Pad atlas pixels around the edge
                int x0 = (int)Math.Floor(x + pen + (g.bx - spread) * kx), x1 = (int)Math.Ceiling(x + pen + (g.bx + g.w + spread) * kx);
                int y0 = (int)Math.Floor(baseline - (g.by + spread) * k), y1 = (int)Math.Ceiling(baseline - (g.by - g.h - spread) * k);
                for (int py = Math.Max(0, y0); py < Math.Min(mh, y1); py++)
                    for (int px = Math.Max(0, x0); px < Math.Min(mw, x1); px++)
                    {
                        float gx = (px + 0.5f - x - pen) / kx, gy = (baseline - (py + 0.5f)) / k;
                        float v = f.Sample(g, gx, gy);
                        // distance to the edge in mask pixels; use the smaller scale so squeezed text stays crisp
                        float d = (v - 0.5f) * 2 * spread * Math.Min(k, kx) + dilate;
                        float cov = Math.Clamp(d + 0.5f, 0, 1);
                        int i = py * mw + px;
                        if (cov > mask[i]) mask[i] = cov;
                    }
            }
            pen += g.adv * kx;
        }
        return pen;
    }
}
