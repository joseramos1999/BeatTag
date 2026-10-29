using System.Buffers.Binary;
using System.Text;

namespace Etiquetador.Tests;

/// <summary>
/// MP3 sintéticos para probar el ajuste de volumen con lo que traen los archivos de verdad: tramas
/// con un global_gain conocido, la etiqueta APEv2 que deja MP3Gain y el ID3v1 del final.
///
/// Lee y escribe los bits por su cuenta, sin usar el código de Mp3Gain: si los dos coinciden, es
/// porque los dos leen el formato igual, no porque compartan el mismo error.
/// </summary>
internal static class Mp3GainSintetico
{
    /// <summary>MPEG1 capa III, 128 kbps, 44,1 kHz, estéreo y sin CRC: 417 bytes por trama.</summary>
    public const int LargoTrama = 417;

    /// <summary>
    /// Posición en bits del global_gain de cada gránulo y canal dentro del side info de MPEG1
    /// estéreo: 20 bits de cabecera (main_data_begin 9, private_bits 3, scfsi 8), bloques de 59
    /// bits y, dentro de cada bloque, 21 bits antes (part2_3_length 12 y big_values 9).
    /// </summary>
    private static readonly int[] BitsDeGanancia = { 41, 100, 159, 218 };

    /// <summary>Posición en bits del part2_3_length de cada gránulo y canal: 21 bits antes del global_gain.</summary>
    private static readonly int[] BitsDeAudio = BitsDeGanancia.Select(b => b - 21).ToArray();

    /// <summary>Bits de audio que se declaran en un gránulo con audio (cualquier valor distinto de 0).</summary>
    private const int BitsDeAudioPorGranulo = 1000;

    /// <summary>
    /// Una trama cuyos cuatro global_gain valen <paramref name="ganancia"/>. Con
    /// <paramref name="vacia"/>, sus gránulos no llevan audio (part2_3_length = 0, silencio digital),
    /// como el 42 % de los MP3 reales en algún punto; si no, declaran bits de audio.
    /// </summary>
    public static byte[] Trama(int ganancia, bool vacia = false)
    {
        var t = new byte[LargoTrama];
        t[0] = 0xFF; t[1] = 0xFB; t[2] = 0x90; t[3] = 0x00;
        foreach (var bit in BitsDeGanancia) EscribirByte(t, 4, bit, (byte)ganancia);
        if (!vacia)
            foreach (var bit in BitsDeAudio) EscribirBits(t, 4, bit, BitsDeAudioPorGranulo, 12);
        return t;
    }

    /// <summary>
    /// Varias tramas seguidas; la ganancia de cada una la decide <paramref name="ganancia"/>, y
    /// <paramref name="vacia"/> cuáles no llevan audio (por defecto, ninguna).
    /// </summary>
    public static byte[] Tramas(int cuantas, Func<int, int> ganancia, Func<int, bool>? vacia = null)
        => Enumerable.Range(0, cuantas).SelectMany(k => Trama(ganancia(k), vacia?.Invoke(k) ?? false)).ToArray();

    /// <summary>Los cuatro part2_3_length de la trama que empieza en <paramref name="inicio"/>.</summary>
    public static int[] BitsDeAudioDe(byte[] archivo, int inicio)
        => BitsDeAudio.Select(bit => LeerBits(archivo, inicio + 4, bit, 12)).ToArray();

    /// <summary>Los cuatro global_gain de la trama que empieza en <paramref name="inicio"/>.</summary>
    public static int[] Ganancias(byte[] archivo, int inicio)
        => BitsDeGanancia.Select(bit => LeerByte(archivo, inicio + 4, bit)).ToArray();

    /// <summary>
    /// Etiqueta APEv2 como la escribe MP3Gain: cabecera (opcional), elementos de texto y pie.
    /// Versión 2000; en la cabecera los flags A0000000 y en el pie 80000000, como en la biblioteca.
    /// </summary>
    public static byte[] Ape(bool conCabecera, params (string Clave, byte[] Valor, uint Flags)[] elementos)
    {
        using var cuerpo = new MemoryStream();
        foreach (var (clave, valor, flags) in elementos)
        {
            cuerpo.Write(U32(valor.Length));
            cuerpo.Write(U32((int)flags));
            cuerpo.Write(Encoding.ASCII.GetBytes(clave));
            cuerpo.WriteByte(0);
            cuerpo.Write(valor);
        }
        var tam = (int)cuerpo.Length + 32;

        using var r = new MemoryStream();
        if (conCabecera) r.Write(Bloque(tam, elementos.Length, 0xA0000000));
        r.Write(cuerpo.ToArray());
        r.Write(Bloque(tam, elementos.Length, conCabecera ? 0x80000000 : 0));
        return r.ToArray();
    }

    /// <summary>Atajo para elementos de texto.</summary>
    public static (string, byte[], uint) Texto(string clave, string valor) => (clave, Encoding.UTF8.GetBytes(valor), 0u);

    /// <summary>Un ID3v1 de 128 bytes con el título dado.</summary>
    public static byte[] Id3v1(string titulo)
    {
        var t = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(t, 0);
        var tit = Encoding.ASCII.GetBytes(titulo);
        Array.Copy(tit, 0, t, 3, Math.Min(30, tit.Length));
        t[127] = 255;   // género sin indicar
        return t;
    }

    /// <summary>
    /// Elementos de la etiqueta APE del final, leídos a mano: clave → valor en texto, en orden.
    /// También comprueba que la cabecera y el pie cuadran entre sí, que es lo primero que mira
    /// cualquier lector de APE.
    /// </summary>
    public static List<(string Clave, string Valor)> LeerApe(byte[] archivo, out bool conCabecera)
    {
        var fin = archivo.Length;
        if (fin >= 128 && Encoding.ASCII.GetString(archivo, fin - 128, 3) == "TAG") fin -= 128;
        var pie = fin - 32;
        Assert.Equal("APETAGEX", Encoding.ASCII.GetString(archivo, pie, 8));
        var tam = BinaryPrimitives.ReadInt32LittleEndian(archivo.AsSpan(pie + 12));
        var cuantos = BinaryPrimitives.ReadInt32LittleEndian(archivo.AsSpan(pie + 16));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(archivo.AsSpan(pie + 20));
        conCabecera = (flags & 0x80000000) != 0;
        var p = fin - tam;
        if (conCabecera)
        {
            var cab = p - 32;
            Assert.Equal("APETAGEX", Encoding.ASCII.GetString(archivo, cab, 8));
            Assert.Equal(tam, BinaryPrimitives.ReadInt32LittleEndian(archivo.AsSpan(cab + 12)));
            Assert.Equal(cuantos, BinaryPrimitives.ReadInt32LittleEndian(archivo.AsSpan(cab + 16)));
        }

        var r = new List<(string, string)>();
        for (var k = 0; k < cuantos; k++)
        {
            var largo = BinaryPrimitives.ReadInt32LittleEndian(archivo.AsSpan(p));
            var q = p + 8;
            var cero = Array.IndexOf(archivo, (byte)0, q);
            r.Add((Encoding.ASCII.GetString(archivo, q, cero - q), Encoding.UTF8.GetString(archivo, cero + 1, largo)));
            p = cero + 1 + largo;
        }
        Assert.Equal(pie, p);   // los elementos llenan la etiqueta justo hasta el pie
        return r;
    }

    public static string Valor(List<(string Clave, string Valor)> ape, string clave)
        => ape.Single(e => e.Clave == clave).Valor;

    private static byte[] Bloque(int tam, int cuantos, uint flags)
    {
        var b = new byte[32];
        Encoding.ASCII.GetBytes("APETAGEX").CopyTo(b, 0);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), 2000);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), tam);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), cuantos);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), flags);
        return b;
    }

    private static byte[] U32(int v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        return b;
    }

    // Bits de más significativo a menos, como van en el flujo MPEG.
    private static void EscribirByte(byte[] b, int baseByte, int bit, byte valor)
    {
        for (var k = 0; k < 8; k++)
        {
            var pos = baseByte + (bit + k) / 8;
            var desp = 7 - (bit + k) % 8;
            var v = (valor >> (7 - k)) & 1;
            b[pos] = (byte)((b[pos] & ~(1 << desp)) | (v << desp));
        }
    }

    private static void EscribirBits(byte[] b, int baseByte, int bit, int valor, int cuantos)
    {
        for (var k = 0; k < cuantos; k++)
        {
            var pos = baseByte + (bit + k) / 8;
            var desp = 7 - (bit + k) % 8;
            var v = (valor >> (cuantos - 1 - k)) & 1;
            b[pos] = (byte)((b[pos] & ~(1 << desp)) | (v << desp));
        }
    }

    private static int LeerBits(byte[] b, int baseByte, int bit, int cuantos)
    {
        var r = 0;
        for (var k = 0; k < cuantos; k++)
        {
            var pos = baseByte + (bit + k) / 8;
            var desp = 7 - (bit + k) % 8;
            r = (r << 1) | ((b[pos] >> desp) & 1);
        }
        return r;
    }

    private static int LeerByte(byte[] b, int baseByte, int bit)
    {
        var r = 0;
        for (var k = 0; k < 8; k++)
        {
            var pos = baseByte + (bit + k) / 8;
            var desp = 7 - (bit + k) % 8;
            r = (r << 1) | ((b[pos] >> desp) & 1);
        }
        return r;
    }
}
