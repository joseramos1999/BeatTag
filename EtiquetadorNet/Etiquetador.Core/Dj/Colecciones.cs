namespace Etiquetador.Core.Dj;

/// <summary>
/// Una lista que se rellena sola a partir de unas condiciones: «house vocal, energía 6-7, para
/// apertura». No guarda canciones, guarda el criterio; por eso sigue al día cuando entra música
/// nueva o se completa una ficha.
///
/// Todas las condiciones se tienen que cumplir a la vez. Dentro de los momentos basta con uno de
/// los marcados (una canción de «Subida» sirve en una colección de «Subida o Pico»); en ambiente y
/// etiquetas, en cambio, hacen falta todos, porque cada uno se añade para afinar.
/// </summary>
public sealed class ColeccionInteligente
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Nombre { get; set; } = "Nueva colección";

    /// <summary>Palabras que deben aparecer en artista, título o nombre del archivo.</summary>
    public string Texto { get; set; } = "";

    /// <summary>
    /// Texto que debe contener el género («house» encuentra también «Deep House»). Varios separados
    /// por comas valen como «cualquiera de ellos»: «cumbia, merengue».
    /// </summary>
    public string Genero { get; set; } = "";

    /// <summary>Límites de año de publicación. Cero significa sin límite.</summary>
    public int AnioMin { get; set; }
    public int AnioMax { get; set; }

    /// <summary>Límites de tempo. Cero significa sin límite.</summary>
    public int BpmMin { get; set; }
    public int BpmMax { get; set; }

    /// <summary>Límites de energía, de 1 a 10. Cero significa sin límite.</summary>
    public int EnergiaMin { get; set; }
    public int EnergiaMax { get; set; }

    public MomentoSet Momentos { get; set; }
    public TipoVoz Voz { get; set; }
    public TipoLetra Letra { get; set; }
    public string Idioma { get; set; } = "";
    public List<string> Ambiente { get; set; } = new();
    public List<string> Etiquetas { get; set; } = new();
    public bool SoloArmasSecretas { get; set; }

    /// <summary>Otra colección con las mismas condiciones y un Id nuevo.</summary>
    public ColeccionInteligente Copia(string nombre) => new()
    {
        Nombre = nombre,
        Texto = Texto, Genero = Genero, BpmMin = BpmMin, BpmMax = BpmMax, AnioMin = AnioMin, AnioMax = AnioMax,
        EnergiaMin = EnergiaMin, EnergiaMax = EnergiaMax, Momentos = Momentos, Voz = Voz, Letra = Letra,
        Idioma = Idioma, Ambiente = new List<string>(Ambiente), Etiquetas = new List<string>(Etiquetas),
        SoloArmasSecretas = SoloArmasSecretas,
    };
}

public static class FiltroColeccion
{
    /// <summary>
    /// Tiene alguna condición. Una colección sin ninguna abarcaría la biblioteca entera, que no es
    /// una colección sino la biblioteca, y exportarla como lista sería un error fácil de cometer.
    /// </summary>
    public static bool TieneCriterios(ColeccionInteligente c) =>
        c.Texto.Trim().Length > 0 || c.Genero.Trim().Length > 0 ||
        c.BpmMin > 0 || c.BpmMax > 0 || c.EnergiaMin > 0 || c.EnergiaMax > 0 || c.AnioMin > 0 || c.AnioMax > 0 ||
        c.Momentos != MomentoSet.Ninguno || c.Voz != TipoVoz.SinIndicar || c.Letra != TipoLetra.SinIndicar ||
        c.Idioma.Trim().Length > 0 || c.Ambiente.Count > 0 || c.Etiquetas.Count > 0 || c.SoloArmasSecretas;

    /// <summary>
    /// La canción entra en la colección. Si una condición pide un dato de la ficha y la canción no
    /// lo tiene, NO entra: una colección de «energía 8 o más» no puede llenarse de temas que nadie ha
    /// valorado todavía.
    /// </summary>
    public static bool Cumple(ColeccionInteligente c, Track t, FichaDj? f)
    {
        if (!TieneCriterios(c)) return false;

        if (c.Texto.Trim().Length > 0 && !BusquedaTexto.Coincide(c.Texto, t.Artist, t.Title, t.FileName)) return false;

        if (c.Genero.Trim().Length > 0)
        {
            var buscados = Fichas.PartirLista(c.Genero).Select(TextUtils.Nk).Where(g => g.Length > 0).ToList();
            var genero = TextUtils.Nk(t.Genre);
            if (buscados.Count > 0 && !buscados.Any(b => genero.Contains(b, StringComparison.Ordinal))) return false;
        }

        // Sin año conocido no se puede afirmar que sea de esa época.
        if (c.AnioMin > 0 && (t.Year == 0 || t.Year < c.AnioMin)) return false;
        if (c.AnioMax > 0 && (t.Year == 0 || t.Year > c.AnioMax)) return false;

        // Sin BPM conocido no se puede decir que esté dentro del margen.
        if (c.BpmMin > 0 && (t.Bpm == 0 || t.Bpm < c.BpmMin)) return false;
        if (c.BpmMax > 0 && (t.Bpm == 0 || t.Bpm > c.BpmMax)) return false;

        if (c.EnergiaMin > 0 || c.EnergiaMax > 0)
        {
            var e = f?.Energia ?? 0;
            if (e == 0) return false;
            if (c.EnergiaMin > 0 && e < c.EnergiaMin) return false;
            if (c.EnergiaMax > 0 && e > c.EnergiaMax) return false;
        }

        if (c.Momentos != MomentoSet.Ninguno && (f == null || (f.Momentos & c.Momentos) == 0)) return false;
        if (c.Voz != TipoVoz.SinIndicar && f?.Voz != c.Voz) return false;
        if (c.Letra != TipoLetra.SinIndicar && Fichas.LetraEfectiva(f, t) != c.Letra) return false;

        if (c.Idioma.Trim().Length > 0 && (f == null || Fichas.Clave(f.Idioma) != Fichas.Clave(c.Idioma))) return false;

        if (!ContieneTodas(f?.Ambiente, c.Ambiente)) return false;
        if (!ContieneTodas(f?.Etiquetas, c.Etiquetas)) return false;

        if (c.SoloArmasSecretas && f?.ArmaSecreta != true) return false;
        return true;
    }

    private static bool ContieneTodas(List<string>? tiene, List<string> pedidas)
    {
        if (pedidas.Count == 0) return true;
        if (tiene == null || tiene.Count == 0) return false;
        var claves = tiene.Select(Fichas.Clave).ToHashSet(StringComparer.Ordinal);
        return pedidas.All(p => claves.Contains(Fichas.Clave(p)));
    }
}

/// <summary>Las colecciones guardadas, en el orden en que el usuario las creó.</summary>
public sealed class AlmacenColecciones
{
    private readonly string _archivo;

    public List<ColeccionInteligente> Todas { get; }
    public string AvisoCarga { get; }

    public AlmacenColecciones(string archivo)
    {
        _archivo = archivo;
        Todas = ArchivoJson.Leer(archivo, () => new List<ColeccionInteligente>(), out var aviso);
        AvisoCarga = aviso;
    }

    /// <summary>Devuelve "" si fue bien, o el motivo del fallo.</summary>
    public string Guardar() => ArchivoJson.Escribir(_archivo, Todas);
}
