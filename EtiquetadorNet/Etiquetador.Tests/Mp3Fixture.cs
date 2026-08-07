namespace Etiquetador.Tests;

/// <summary>Genera un MP3 mínimo válido (silencio) para probar lectura/escritura de tags sin ficheros externos.</summary>
internal static class Mp3Fixture
{
    public static void WriteMinMp3(string path, int frames = 25)
    {
        using var ms = new MemoryStream();
        for (int k = 0; k < frames; k++)
        {
            ms.WriteByte(0xFF); ms.WriteByte(0xFB); ms.WriteByte(0x90); ms.WriteByte(0x00);
            for (int j = 0; j < 413; j++) ms.WriteByte(0x00);
        }
        File.WriteAllBytes(path, ms.ToArray());
    }

    public static void SetTags(string p, string? title, string? artist, string? album)
    {
        var t = TagLib.File.Create(p);
        if (title != null) t.Tag.Title = title;
        if (artist != null) t.Tag.Performers = new[] { artist };
        if (album != null) t.Tag.Album = album;
        t.Save(); t.Dispose();
    }

    public static string GetTitle(string p)
    {
        var t = TagLib.File.Create(p);
        var v = t.Tag.Title ?? "";
        t.Dispose();
        return v;
    }

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "etq_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
