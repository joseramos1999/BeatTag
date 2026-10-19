using Etiquetador.Core.Analysis;

namespace Etiquetador.Core.Dj;

/// <summary>Cómo de amplia es la búsqueda de compatibles.</summary>
public sealed record OpcionesCompatibles
{
    /// <summary>
    /// Margen de tempo para una canción normal, en tanto por uno (0,06 = 6 %).
    ///
    /// Un 6 % es, más o menos, un semitono (2^(1/12) ≈ 1,059). Por encima, sin bloqueo de tono el
    /// cambio de tempo mueve la tonalidad un semitono y deshace el encaje armónico que la lista
    /// promete; con el bloqueo puesto, el estiramiento empieza a oírse. Es el margen que suelen
    /// recomendar las guías de mezcla armónica, y vale para cualquier estilo porque es relativo.
    /// </summary>
    public double MargenBpm { get; init; } = 0.06;

    /// <summary>
    /// Margen de tempo cuando la consultada es una acapella: 3 %. La voz sola es lo que peor soporta
    /// el estiramiento de tiempo, y lanzarla sobre una base que exige más ajuste suena artificial.
    /// </summary>
    public double MargenBpmAcapella { get; init; } = 0.03;

    /// <summary>
    /// Incluir los saltos de dos pasos en la rueda (8A → 10A). No es mezcla armónica estricta, pero
    /// muchos DJs lo usan a propósito para subir la energía; por eso es opcional.
    /// </summary>
    public bool IncluirSaltos { get; init; }
}

/// <summary>A qué tempo entra la candidata respecto a la consultada.</summary>
public enum RelacionTempo
{
    Mismo,
    Doble,
    Mitad,
    /// <summary>Alguna de las dos no tiene BPM en las etiquetas: no se sabe.</summary>
    Desconocida,
}

/// <summary>Una canción que entra bien después de la consultada (o sobre ella, si es una acapella).</summary>
/// <param name="DiferenciaBpm">Como en <see cref="OrdenMezcla.DiferenciaBpm"/>: ya contando doble y mitad.</param>
/// <param name="Coste">El de <see cref="OrdenMezcla.Coste"/>: cuanto menor, mejor entra.</param>
public sealed record Compatible(Track Track, TipoPieza Tipo, EncajeTono Tono, int DiferenciaBpm, RelacionTempo Relacion, double Coste)
{
    /// <summary>«+2 BPM», «Mismo tempo», «-1 BPM (a doble tempo)», «Sin BPM».</summary>
    public string TextoDiferencia
    {
        get
        {
            if (Relacion == RelacionTempo.Desconocida) return "Sin BPM";
            var d = DiferenciaBpm == 0 ? "Mismo tempo" : $"{DiferenciaBpm:+0;-0} BPM";
            return Relacion switch
            {
                RelacionTempo.Doble => d + " (a doble tempo)",
                RelacionTempo.Mitad => d + " (a mitad de tempo)",
                _ => d,
            };
        }
    }
}

/// <summary>Lo que ha encontrado la búsqueda, y lo que ha dejado fuera por falta de datos.</summary>
/// <param name="EsAcapella">La consultada es una acapella: se buscan bases y el margen es el estrecho.</param>
/// <param name="Margen">El margen de tempo aplicado, en tanto por uno.</param>
/// <param name="SinBpm">Candidatas con tono compatible que no se han podido comprobar porque no tienen BPM.</param>
/// <param name="Aviso">Por qué la lista está vacía o es parcial, en una frase. Vacío si no hay nada que decir.</param>
public sealed record ResultadoCompatibles(IReadOnlyList<Compatible> Canciones, bool EsAcapella, double Margen, int SinBpm, string Aviso);

/// <summary>
/// Qué canciones de la biblioteca entran bien después de una dada, y sobre qué bases se puede lanzar
/// una acapella.
///
/// Usa las mismas reglas que <see cref="OrdenMezcla"/>: la rueda Camelot de la tonalidad que traen
/// las etiquetas, el tempo contando doble y mitad, y el mismo coste para ordenarlas. No usa el
/// detector de tonalidad propio (medido en un 26 % de acierto) ni la IA: solo lo que dicen las
/// etiquetas, que es lo que el DJ ve en su programa.
///
/// MEDIDO en una biblioteca de 13.590 canciones: unos 36 ms por consulta, y una media de 585
/// compatibles por canción (186 bases por acapella, con su margen estrecho). Se llama fuera del hilo de
/// la interfaz igualmente.
/// </summary>
public static class Compatibles
{
    /// <summary>
    /// Las canciones compatibles con <paramref name="x"/>, de la que mejor entra a la que peor.
    ///
    /// Entran las del mismo tono o un paso de rueda (y dos, con <see cref="OpcionesCompatibles.IncluirSaltos"/>)
    /// cuyo tempo esté dentro del margen. Se excluyen la propia canción y las demás versiones suyas
    /// (otra copia, su extended, su acapella…), que para eso está «Ver sus versiones». Si
    /// <paramref name="x"/> es una acapella, se buscan BASES: fuera otras acapellas y los mashups (ya
    /// llevan voz, y a veces dos), y el margen de tempo es el estrecho.
    /// </summary>
    /// <param name="fichaDe">La ficha de DJ de cada canción, si la tiene: su energía y su momento entran en el coste.</param>
    public static ResultadoCompatibles Buscar(Track x, IEnumerable<Track> biblioteca, OpcionesCompatibles? opciones = null,
                                              Func<Track, FichaDj?>? fichaDe = null)
    {
        opciones ??= new OpcionesCompatibles();
        var esAcapella = Identificacion.EsAcapella(x.FilePath);
        var margen = esAcapella ? opciones.MargenBpmAcapella : opciones.MargenBpm;

        if (x.KeyCamelot.Length == 0)
            return new ResultadoCompatibles(Array.Empty<Compatible>(), esAcapella, margen, 0,
                "Esta canción no tiene tonalidad en las etiquetas, así que no se puede saber qué encaja con ella. "
                + "Puedes importarla de rekordbox en Biblioteca.");

        var fx = fichaDe?.Invoke(x);
        var mismaCancion = ClavesCancion(x, conPartes: IndiceVersiones.Clasificar(x.FilePath) == TipoPieza.Mezcla);
        var sinBpm = 0;
        var salida = new List<Compatible>();

        foreach (var c in biblioteca)
        {
            if (string.Equals(c.FilePath, x.FilePath, StringComparison.OrdinalIgnoreCase)) continue;

            // Primero lo barato (tono y tempo, que ya están calculados en la canción) y solo con las que
            // pasan, lo caro (leer el nombre). Con 15.000 canciones, la mayoría se descarta aquí.
            var tono = OrdenMezcla.Encaje(x.KeyCamelot, c.KeyCamelot);
            if (tono is not (EncajeTono.Igual or EncajeTono.Compatible) && !(opciones.IncluirSaltos && tono == EncajeTono.Salto))
                continue;

            if (x.Bpm > 0)
            {
                if (c.Bpm == 0) { sinBpm++; continue; }
                if (DiferenciaRelativa(x.Bpm, c.Bpm) > margen + 1e-9) continue;
            }

            var tipo = IndiceVersiones.Clasificar(c.FilePath);
            if (esAcapella && tipo is (TipoPieza.Acapella or TipoPieza.Mezcla)) continue;
            if (mismaCancion.Count > 0 && ClavesCancion(c).Overlaps(mismaCancion)) continue;

            var d = OrdenMezcla.DiferenciaBpm(x.Bpm, c.Bpm);
            salida.Add(new Compatible(c, tipo, tono, d, Relacion(x.Bpm, c.Bpm, d), OrdenMezcla.Coste(x, fx, c, fichaDe?.Invoke(c))));
        }

        var orden = salida
            .OrderBy(c => c.Coste)
            // Para una acapella, a igual coste, primero las instrumentales: son la base natural.
            .ThenBy(c => esAcapella && c.Tipo != TipoPieza.Instrumental)
            .ThenBy(c => c.Track.FilePath, StringComparer.OrdinalIgnoreCase)   // mismo resultado cada vez
            .ToList();

        var aviso = x.Bpm == 0
            ? "Esta canción no tiene BPM en las etiquetas: solo se ha comparado el tono."
            : "";
        return new ResultadoCompatibles(orden, esAcapella, margen, sinBpm, aviso);
    }

    /// <summary>
    /// La diferencia de tempo en tanto por uno, contando doble y mitad (85 y 172 BPM difieren un 1 %,
    /// no un 50 %). Se divide por el mayor de los dos tempos comparables, que es el que se ajusta.
    /// </summary>
    public static double DiferenciaRelativa(uint a, uint b)
    {
        if (a == 0 || b == 0) return 0;
        var d = Math.Abs(OrdenMezcla.DiferenciaBpm(a, b));
        return (double)d / Math.Max(a, b);
    }

    private static RelacionTempo Relacion(uint desde, uint hasta, int diferencia)
    {
        if (desde == 0 || hasta == 0) return RelacionTempo.Desconocida;
        if (diferencia == (int)hasta - (int)desde) return RelacionTempo.Mismo;
        return hasta > desde ? RelacionTempo.Doble : RelacionTempo.Mitad;
    }

    /// <summary>
    /// Las claves «artista|título» de la canción, sin versión, con las que se reconoce otra copia o
    /// versión suya: la de la bandeja (<see cref="RevisionBandeja.ClaveCancion"/>) y la que sale de
    /// cada artista por separado, del nombre del archivo si las etiquetas están vacías.
    ///
    /// En un mashup, además, las de cada una de sus partes: desde «Blue Vs Baby Hello» no tiene
    /// sentido proponer «Baby Hello», que ya está sonando dentro.
    /// </summary>
    private static HashSet<string> ClavesCancion(Track t, bool conPartes = false)
    {
        var claves = new HashSet<string>(StringComparer.Ordinal);
        var deEtiquetas = RevisionBandeja.ClaveCancion(t);
        if (deEtiquetas.Length > 0) claves.Add(deEtiquetas);

        var p = Pipeline.FileNameParser.Parse(t.FileName);
        var artistas = IndiceVersiones.ClavesArtista(t.Artist).Concat(IndiceVersiones.ClavesArtista(p.FnArtist)).ToList();
        var titulos = new List<string> { IndiceVersiones.ClaveTitulo(string.IsNullOrWhiteSpace(t.Title) ? p.FnTitle : t.Title) };
        if (conPartes)
            foreach (var fuente in new[] { t.Title, p.FnTitle, p.FnArtist })
                foreach (var parte in IndiceVersiones.Partes(fuente))
                {
                    titulos.Add(IndiceVersiones.ClaveTitulo(parte));
                    artistas.AddRange(IndiceVersiones.ClavesArtista(parte));
                }

        foreach (var titulo in titulos.Where(x => x.Length > 0))
            foreach (var a in artistas)
                claves.Add(a + "|" + titulo);
        return claves;
    }
}
