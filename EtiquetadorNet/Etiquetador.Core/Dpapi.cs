using System;
using System.Security.Cryptography;
using System.Text;

namespace Etiquetador.Core;

public enum UnprotectStatus { Empty, Cleartext, Ok, CryptoError }

/// <summary>
/// Cifrado DPAPI ligado al usuario Windows (equivalente a Protect-Str/Unprotect-Str del .ps1).
/// Distingue una config heredada EN CLARO de un ERROR criptográfico (perfil distinto / DPAPI
/// no disponible), para no re-cifrar por error un blob y perder la credencial.
/// </summary>
public static class Dpapi
{
    // Cabecera de un blob DPAPI (CurrentUser): permite distinguir "hex cifrado" de "texto en claro".
    private static readonly byte[] DpapiMagic = { 0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF };

    /// <summary>Cifra una cadena → hex. Vacío/nulo → "". Si DPAPI falla, PROPAGA la excepción.</summary>
    public static string Protect(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bytes = Encoding.Unicode.GetBytes(s);
        var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToHexString(enc);
    }

    /// <summary>
    /// Descifra distinguiendo el resultado:
    /// - Empty: vacío. - Cleartext: no es un blob DPAPI (config antigua en claro) → se devuelve tal cual.
    /// - Ok: descifrado correcto. - CryptoError: ES un blob DPAPI pero no se pudo descifrar (no re-cifrar/guardar).
    /// </summary>
    public static (string Value, UnprotectStatus Status) TryUnprotect(string? s)
    {
        if (string.IsNullOrEmpty(s)) return ("", UnprotectStatus.Empty);
        byte[] enc;
        try { enc = Convert.FromHexString(s); }
        catch { return (s, UnprotectStatus.Cleartext); }        // ni siquiera es hex → claro heredado

        if (!StartsWith(enc, DpapiMagic)) return (s, UnprotectStatus.Cleartext);   // hex pero no un blob DPAPI

        try
        {
            var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return (Encoding.Unicode.GetString(dec), UnprotectStatus.Ok);
        }
        catch { return ("", UnprotectStatus.CryptoError); }     // blob DPAPI ilegible en este perfil
    }

    /// <summary>Compat: descifra devolviendo solo el valor (claro heredado se devuelve tal cual).</summary>
    public static string Unprotect(string? s) => TryUnprotect(s).Value;

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++) if (data[i] != prefix[i]) return false;
        return true;
    }
}
