using System.IO.Compression;

namespace GnosiaPatcher;

/// <summary>Minimal PNG writer (RGBA8, top-down rows) for image previews.</summary>
static class Png
{
    static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in type.Concat(data)) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    public static void Write(string path, byte[] rgba, int w, int h)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            fs.Write(BigEndian((uint)data.Length)); fs.Write(t); fs.Write(data); fs.Write(BigEndian(Crc(t, data)));
        }
        var ihdr = BigEndian((uint)w).Concat(BigEndian((uint)h)).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
        Chunk("IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgba, y * w * 4, w * 4); }
        Chunk("IDAT", raw.ToArray());
        Chunk("IEND", Array.Empty<byte>());
    }

    static byte[] BigEndian(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}
