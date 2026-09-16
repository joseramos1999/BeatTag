namespace Etiquetador.Core.Dj;

/// <summary>
/// Lo que tienen en común varias fichas, que es lo que el editor enseña al seleccionar varias
/// canciones a la vez. Un valor null significa «no coinciden»; las listas y los momentos llevan
/// solo lo que tienen TODAS.
/// </summary>
public sealed record FichaComun(
    int? Energia,
    MomentoSet Momentos,
    TipoVoz? Voz,
    TipoLetra? Letra,
    string? Idioma,
    IReadOnlyList<string> Ambiente,
    IReadOnlyList<string> Etiquetas,
    bool? ArmaSecreta)
{
    public static readonly FichaComun Vacia =
        new(0, MomentoSet.Ninguno, TipoVoz.SinIndicar, TipoLetra.SinIndicar, "", Array.Empty<string>(), Array.Empty<string>(), false);

    /// <summary>Lo común a las fichas dadas. Una canción sin ficha cuenta como ficha vacía.</summary>
    public static FichaComun De(IReadOnlyList<FichaDj?> fichas)
    {
        if (fichas.Count == 0) return Vacia;
        var todas = fichas.Select(f => f ?? new FichaDj()).ToList();
        var primera = todas[0];

        T? SiCoinciden<T>(Func<FichaDj, T> campo) where T : struct
        {
            var v = campo(primera);
            return todas.All(f => EqualityComparer<T>.Default.Equals(campo(f), v)) ? v : null;
        }

        var momentos = todas.Aggregate((MomentoSet)~0, (acc, f) => acc & f.Momentos);
        var idioma = todas.All(f => Fichas.Clave(f.Idioma) == Fichas.Clave(primera.Idioma)) ? primera.Idioma : null;

        return new FichaComun(
            SiCoinciden(f => f.Energia),
            momentos,
            SiCoinciden(f => f.Voz),
            SiCoinciden(f => f.Letra),
            idioma,
            Interseccion(todas.Select(f => f.Ambiente)),
            Interseccion(todas.Select(f => f.Etiquetas)),
            SiCoinciden(f => f.ArmaSecreta));
    }

    private static IReadOnlyList<string> Interseccion(IEnumerable<List<string>> listas)
    {
        var lista = listas.ToList();
        return lista[0]
            .Where(e => lista.All(l => l.Any(x => Fichas.Clave(x) == Fichas.Clave(e))))
            .ToList();
    }
}

/// <summary>
/// Los cambios que el usuario ha hecho en el editor, y solo esos.
///
/// Es lo que permite editar muchas canciones a la vez sin estropear nada. Subir la energía de
/// cincuenta temas no puede borrarles el ambiente que cada uno tenía, ni añadir la etiqueta «boda»
/// puede quitarles las demás. Por eso no se guarda «la ficha que se ve en el editor», sino la
/// DIFERENCIA entre lo que se enseñó y lo que el usuario dejó:
///
///   · Un campo simple solo cambia si el usuario lo tocó.
///   · En momentos y listas se calcula qué se añadió y qué se quitó, y se aplica eso a cada ficha.
///     Una etiqueta que tenían algunas y no todas no se enseña, así que tampoco se quita.
/// </summary>
public sealed record CambiosFicha
{
    public int? Energia { get; init; }
    public TipoVoz? Voz { get; init; }
    public TipoLetra? Letra { get; init; }
    public string? Idioma { get; init; }
    public bool? ArmaSecreta { get; init; }

    public MomentoSet MomentosPoner { get; init; }
    public MomentoSet MomentosQuitar { get; init; }

    public IReadOnlyList<string> AmbienteAnadir { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AmbienteQuitar { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EtiquetasAnadir { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> EtiquetasQuitar { get; init; } = Array.Empty<string>();

    public bool HayAlguno =>
        Energia != null || Voz != null || Letra != null || Idioma != null || ArmaSecreta != null ||
        MomentosPoner != MomentoSet.Ninguno || MomentosQuitar != MomentoSet.Ninguno ||
        AmbienteAnadir.Count > 0 || AmbienteQuitar.Count > 0 ||
        EtiquetasAnadir.Count > 0 || EtiquetasQuitar.Count > 0;

    /// <summary>
    /// Compara lo que se enseñó (<paramref name="inicial"/>) con lo que quedó en el editor
    /// (<paramref name="editado"/>). En <paramref name="editado"/>, un null en un campo simple
    /// significa que se dejó en «varios», es decir, sin tocar.
    /// </summary>
    public static CambiosFicha Calcular(FichaComun inicial, FichaComun editado)
    {
        return new CambiosFicha
        {
            Energia = Cambio(inicial.Energia, editado.Energia),
            Voz = Cambio(inicial.Voz, editado.Voz),
            Letra = Cambio(inicial.Letra, editado.Letra),
            ArmaSecreta = Cambio(inicial.ArmaSecreta, editado.ArmaSecreta),
            Idioma = CambioTexto(inicial.Idioma, editado.Idioma),

            MomentosPoner = editado.Momentos & ~inicial.Momentos,
            MomentosQuitar = inicial.Momentos & ~editado.Momentos,

            AmbienteAnadir = Menos(editado.Ambiente, inicial.Ambiente),
            AmbienteQuitar = Menos(inicial.Ambiente, editado.Ambiente),
            EtiquetasAnadir = Menos(editado.Etiquetas, inicial.Etiquetas),
            EtiquetasQuitar = Menos(inicial.Etiquetas, editado.Etiquetas),
        };
    }

    private static T? Cambio<T>(T? antes, T? despues) where T : struct
        => despues != null && !EqualityComparer<T?>.Default.Equals(antes, despues) ? despues : null;

    // Con el idioma «varios», dejar la casilla vacía es no tocarlo; escribir algo lo fija en todas.
    private static string? CambioTexto(string? antes, string? despues)
    {
        var d = (despues ?? "").Trim();
        if (antes == null) return d.Length > 0 ? d : null;
        return d == antes.Trim() ? null : d;
    }

    private static IReadOnlyList<string> Menos(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.Where(x => !b.Any(y => Fichas.Clave(y) == Fichas.Clave(x))).ToList();

    /// <summary>La ficha resultante de aplicar estos cambios a <paramref name="actual"/>. No la modifica.</summary>
    public FichaDj AplicarA(FichaDj? actual)
    {
        var f = actual?.Copia() ?? new FichaDj();
        if (Energia is int e) f.Energia = Math.Clamp(e, 0, Fichas.EnergiaMaxima);
        if (Voz is TipoVoz v) f.Voz = v;
        if (Letra is TipoLetra l) f.Letra = l;
        if (Idioma != null) f.Idioma = Idioma;
        if (ArmaSecreta is bool a) f.ArmaSecreta = a;
        f.Momentos = (f.Momentos | MomentosPoner) & ~MomentosQuitar;
        f.Ambiente = Combinar(f.Ambiente, AmbienteAnadir, AmbienteQuitar);
        f.Etiquetas = Combinar(f.Etiquetas, EtiquetasAnadir, EtiquetasQuitar);
        return f;
    }

    private static List<string> Combinar(List<string> actual, IReadOnlyList<string> anadir, IReadOnlyList<string> quitar)
    {
        var res = actual.Where(x => !quitar.Any(q => Fichas.Clave(q) == Fichas.Clave(x))).ToList();
        foreach (var n in anadir)
            if (!res.Any(x => Fichas.Clave(x) == Fichas.Clave(n))) res.Add(n);
        return res;
    }
}
