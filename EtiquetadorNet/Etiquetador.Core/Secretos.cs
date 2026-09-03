using System;
using System.Security.Cryptography;
using System.Text;

namespace Etiquetador.Core;

/// <summary>
/// Cifra y descifra las credenciales de la configuración, con el mecanismo propio de cada sistema.
///
///   Windows  DPAPI, ligado a la cuenta del usuario. No se ha tocado: <see cref="Dpapi"/> sigue
///            produciendo exactamente el mismo formato de siempre, así que los config.net.json
///            existentes se leen igual que antes.
///   macOS    AES-GCM con una clave guardada en el Llavero (ver <see cref="LlaveroMac"/>).
///
/// Las dos vías comparten forma: entra texto, sale hexadecimal, y al revés. Eso permite que
/// AppConfig no sepa en qué sistema está y que el archivo siga siendo el mismo de siempre.
///
/// Lo que NUNCA se hace es guardar en claro cuando el cifrado no está disponible: se lanza
/// excepción y AppConfig.Save la convierte en un error visible que impide el guardado. Escribir las
/// claves en claro «para que al menos funcione» sería la peor manera de resolver el problema.
/// </summary>
public static class Secretos
{
    // Cabecera propia del formato de macOS ("BTG1"), para distinguirlo tanto de un blob DPAPI como
    // de una configuración antigua guardada en claro.
    private static readonly byte[] MagicMac = { 0x42, 0x54, 0x47, 0x31 };

    private const int TamNonce = 12;   // AES-GCM
    private const int TamTag = 16;

    /// <summary>¿Se puede cifrar en este equipo? Si es falso, no se debe guardar ningún secreto.</summary>
    public static bool Disponible => OperatingSystem.IsWindows() ? Dpapi.Disponible : LlaveroMac.Disponible();

    /// <summary>Cifra una cadena → hex. Vacío/nulo → "". Si el cifrado falla, PROPAGA la excepción.</summary>
    public static string Protect(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (OperatingSystem.IsWindows()) return Dpapi.Protect(s);

        var clave = LlaveroMac.Clave();
        var claro = Encoding.UTF8.GetBytes(s);
        var nonce = RandomNumberGenerator.GetBytes(TamNonce);
        var cifrado = new byte[claro.Length];
        var tag = new byte[TamTag];

        using (var aes = new AesGcm(clave, TamTag))
            aes.Encrypt(nonce, claro, cifrado, tag);

        var salida = new byte[MagicMac.Length + TamNonce + TamTag + cifrado.Length];
        Buffer.BlockCopy(MagicMac, 0, salida, 0, MagicMac.Length);
        Buffer.BlockCopy(nonce, 0, salida, MagicMac.Length, TamNonce);
        Buffer.BlockCopy(tag, 0, salida, MagicMac.Length + TamNonce, TamTag);
        Buffer.BlockCopy(cifrado, 0, salida, MagicMac.Length + TamNonce + TamTag, cifrado.Length);
        return Convert.ToHexString(salida);
    }

    /// <summary>
    /// Descifra distinguiendo el resultado, igual que hacía DPAPI:
    /// - Empty: vacío. - Cleartext: no es un blob nuestro (config antigua en claro) → se devuelve tal cual.
    /// - Ok: descifrado correcto. - CryptoError: ES un blob nuestro pero ilegible (no re-cifrar/guardar).
    /// </summary>
    public static (string Value, UnprotectStatus Status) TryUnprotect(string? s)
    {
        if (string.IsNullOrEmpty(s)) return ("", UnprotectStatus.Empty);

        // Un blob de macOS puede llegar a un Windows (config copiada entre equipos) y al revés. Se
        // reconoce por su cabecera y se informa de que no se puede leer AQUÍ, en vez de tratarlo
        // como texto en claro y devolver un churro hexadecimal como si fuera la credencial.
        byte[] datos;
        try { datos = Convert.FromHexString(s); }
        catch { return (s, UnprotectStatus.Cleartext); }

        if (EmpiezaPor(datos, MagicMac))
            return OperatingSystem.IsWindows()
                ? ("", UnprotectStatus.CryptoError)   // cifrado en un Mac: aquí no hay con qué abrirlo
                : DescifrarMac(datos);

        // No es formato macOS: lo trata DPAPI, que ya distingue su propio blob del texto en claro.
        if (OperatingSystem.IsWindows()) return Dpapi.TryUnprotect(s);

        // En macOS, un blob DPAPI tampoco se puede abrir; el resto es config heredada en claro.
        return Dpapi.PareceBlobDpapi(datos) ? ("", UnprotectStatus.CryptoError) : (s, UnprotectStatus.Cleartext);
    }

    /// <summary>Compat: descifra devolviendo solo el valor (claro heredado se devuelve tal cual).</summary>
    public static string Unprotect(string? s) => TryUnprotect(s).Value;

    private static (string, UnprotectStatus) DescifrarMac(byte[] datos)
    {
        var minimo = MagicMac.Length + TamNonce + TamTag;
        if (datos.Length < minimo) return ("", UnprotectStatus.CryptoError);

        try
        {
            var clave = LlaveroMac.Clave();
            var nonce = new byte[TamNonce];
            var tag = new byte[TamTag];
            var cifrado = new byte[datos.Length - minimo];
            Buffer.BlockCopy(datos, MagicMac.Length, nonce, 0, TamNonce);
            Buffer.BlockCopy(datos, MagicMac.Length + TamNonce, tag, 0, TamTag);
            Buffer.BlockCopy(datos, minimo, cifrado, 0, cifrado.Length);

            var claro = new byte[cifrado.Length];
            using (var aes = new AesGcm(clave, TamTag))
                aes.Decrypt(nonce, cifrado, tag, claro);

            return (Encoding.UTF8.GetString(claro), UnprotectStatus.Ok);
        }
        catch { return ("", UnprotectStatus.CryptoError); }
    }

    private static bool EmpiezaPor(byte[] datos, byte[] prefijo)
    {
        if (datos.Length < prefijo.Length) return false;
        for (var i = 0; i < prefijo.Length; i++) if (datos[i] != prefijo[i]) return false;
        return true;
    }
}
