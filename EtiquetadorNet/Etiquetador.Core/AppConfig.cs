using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Etiquetador.Core;

/// <summary>
/// Configuración persistente (equivale a Save-Config/Load-Config del .ps1).
/// Los secretos se guardan cifrados con DPAPI (propiedades *Enc); en memoria van en claro.
/// Nota: a diferencia del .ps1, aquí la clave de AcoustID también se cifra.
/// </summary>
public sealed class AppConfig
{
    // --- Carpetas ---
    public List<string> Folders { get; set; } = new();
    /// <summary>Carpetas presentes pero desmarcadas (no se analizan).</summary>
    public List<string> DisabledFolders { get; set; } = new();

    // --- Credenciales (en claro en memoria; se serializan cifradas vía *Enc) ---
    [JsonIgnore] public string DiscogsToken { get; set; } = "";
    [JsonIgnore] public string SpotifySecret { get; set; } = "";
    [JsonIgnore] public string AcoustIdKey { get; set; } = "";
    [JsonIgnore] public string AiKey { get; set; } = "";
    public string SpotifyId { get; set; } = "";   // no secreto

    // --- Opciones de ejecución ---
    public bool Apply { get; set; }
    public bool Overwrite { get; set; }
    public bool SkipMixFolders { get; set; }
    public bool CleanOnly { get; set; }

    // --- Fuentes ---
    public bool UseSpotify { get; set; }
    public bool UseDiscogs { get; set; }
    public bool UseMusicBrainz { get; set; }
    public bool UseItunes { get; set; }
    public bool UseDeezer { get; set; } = true;
    public bool UseAcoustId { get; set; }
    public bool UseAi { get; set; }

    // --- Carátula ---
    public string CoverMode { get; set; } = "keep";   // keep | spotify | png
    public string CoverPath { get; set; } = "";

    // --- Campos a escribir ---
    public bool WriteTitle { get; set; } = true;
    public bool WriteAlbum { get; set; } = true;
    public bool WriteGenre { get; set; } = true;
    public bool WriteYear { get; set; } = true;
    public bool WriteArtist { get; set; } = true;
    public bool WriteBpm { get; set; } = true;

    // --- Varios ---
    public bool Cache { get; set; } = true;

    // --- Secretos serializados (cifrados). El getter cifra, el setter descifra. ---
    [JsonPropertyName("DiscogsTokenEnc")] public string DiscogsTokenEnc { get => Dpapi.Protect(DiscogsToken); set => DiscogsToken = Dec(value); }
    [JsonPropertyName("SpotifySecretEnc")] public string SpotifySecretEnc { get => Dpapi.Protect(SpotifySecret); set => SpotifySecret = Dec(value); }
    [JsonPropertyName("AcoustIdKeyEnc")] public string AcoustIdKeyEnc { get => Dpapi.Protect(AcoustIdKey); set => AcoustIdKey = Dec(value); }
    [JsonPropertyName("AiKeyEnc")] public string AiKeyEnc { get => Dpapi.Protect(AiKey); set => AiKey = Dec(value); }

    /// <summary>true si al cargar hubo un ERROR criptográfico descifrando algún secreto (bloquea el guardado).</summary>
    [JsonIgnore] public bool SecretsUnreadable { get; private set; }

    private string Dec(string? enc)
    {
        var (value, status) = Dpapi.TryUnprotect(enc);
        if (status == UnprotectStatus.CryptoError) SecretsUnreadable = true;
        return value;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load(AppPaths paths) => Load(paths, out _);

    public static AppConfig Load(AppPaths paths, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(paths.ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(paths.ConfigPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
        }
        catch (Exception e)
        {
            error = "No se pudo leer la configuración: " + e.Message;
            return new AppConfig();
        }
    }

    /// <summary>
    /// Guarda de forma segura: 1) cifra TODO en memoria (si falla, no toca el archivo anterior);
    /// 2) escribe a un temporal y lo intercambia de forma atómica. Devuelve false + error si falla.
    /// </summary>
    public bool Save(AppPaths paths, out string error)
    {
        error = "";
        // Si las credenciales guardadas no se pudieron descifrar (otro perfil de Windows / DPAPI no
        // disponible), NO guardamos: re-cifraríamos vacíos y perderíamos las originales del archivo.
        if (SecretsUnreadable)
        {
            error = "No se pueden descifrar las credenciales guardadas en este perfil de Windows; " +
                    "no se guardará para no perderlas. (Si son de otro equipo/usuario, borra config.net.json y vuelve a introducirlas.)";
            return false;
        }
        string json;
        try { json = JsonSerializer.Serialize(this, JsonOpts); }   // cifra los secretos aquí
        catch (Exception e) { error = "No se pudieron cifrar las credenciales: " + e.Message; return false; }

        var tmp = paths.ConfigPath + ".tmp";
        try
        {
            Directory.CreateDirectory(paths.DataDir);
            File.WriteAllText(tmp, json, Encoding.UTF8);
            if (File.Exists(paths.ConfigPath)) File.Replace(tmp, paths.ConfigPath, null);   // intercambio atómico
            else File.Move(tmp, paths.ConfigPath);
            return true;
        }
        catch (Exception e)
        {
            error = "No se pudo guardar la configuración: " + e.Message;
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
