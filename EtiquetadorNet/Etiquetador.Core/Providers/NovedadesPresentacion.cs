using System.Globalization;

namespace Etiquetador.Core.Providers;

/// <summary>Qué tipos de lanzamiento se enseñan en Novedades.</summary>
public enum FiltroTipoLanzamiento { Todos, Singles, Ep, Albumes }

/// <summary>
/// Los filtros de Novedades que deciden QUÉ canciones forman la lista. Se aplican antes de cruzarla
/// con la biblioteca, así que lo que se guarda en M3U8, se copia a una carpeta o se añade a pendientes
/// es lo mismo que se ve.
/// </summary>
/// <param name="SoloEstrenos">Fuera las que ya se habían publicado antes del plazo y las reediciones.</param>
/// <param name="IncluirSinConfirmar">Con las de artistas que no se han podido confirmar en Deezer (pueden ser de un homónimo).</param>
public sealed record FiltroNovedades(FiltroTipoLanzamiento Tipo = FiltroTipoLanzamiento.Todos, bool SoloEstrenos = false,
                                     bool IncluirSinConfirmar = false);

/// <summary>Lo que se enseña de una canción de Novedades además de artista, título y duración.</summary>
public sealed record DetalleNovedad(PistaNovedad Pista)
{
    public DateTime Fecha => Pista.Lanzamiento.Fecha;
    public string FechaTexto => Fecha.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    public string Lanzamiento => Pista.Lanzamiento.Titulo;
    public string Tipo => PresentacionNovedades.NombreTipo(Pista.Lanzamiento.Tipo);
    public string Por => Pista.Por;
    public string Aviso => PresentacionNovedades.Aviso(Pista);
    public string EnlaceLanzamiento => Pista.Lanzamiento.Enlace;
}

/// <summary>Un artista consultado (o excluido), como fila de la tabla «Artistas» de Novedades.</summary>
/// <param name="Recientes">Lanzamientos dentro del plazo en la última búsqueda; -1 si no se sabe (excluido, o corregido después).</param>
public sealed record FilaArtistaNovedades(string Clave, string Nombre, int Canciones, long DeezerId, string DeezerNombre, int Fans,
                                          EstadoArtistaDeezer? Estado, int Coincidencias, int Recientes, bool Excluido, bool FalloDeRed)
{
    public string CancionesTexto => Canciones > 0 ? Canciones.ToString(CultureInfo.InvariantCulture) : "";
    public string FansTexto => DeezerId > 0 ? Fans.ToString("N0", CultureInfo.GetCultureInfo("es-ES")) : "";
    public string EstadoTexto => PresentacionNovedades.TextoEstado(this);
    public string RecientesTexto => Recientes >= 0 && !Excluido ? Recientes.ToString(CultureInfo.InvariantCulture) : "";
    public string Enlace => DeezerId > 0 ? NovedadesDeezer.EnlaceArtista(DeezerId) : "";
    public bool TieneEnlace => DeezerId > 0;
}

/// <summary>
/// Lo que la página Novedades enseña de una búsqueda: qué canciones entran con cada filtro, cómo se
/// describe cada una y cómo se resume el resultado. Sin interfaz, para poder probarlo.
/// </summary>
public static class PresentacionNovedades
{
    /// <summary>
    /// Artistas que se pueden consultar. 100 por defecto: en una biblioteca real de 13.590 canciones
    /// el artista número 100 ya solo tenía 39, y la consulta de 200 tardó 2 min 24 s la primera vez
    /// (con la caché de red vacía). Con 100 es la mitad, y cubre a quienes más se pinchan en cualquier
    /// biblioteca, sea del estilo que sea.
    /// </summary>
    public static readonly int[] OpcionesArtistas = { 50, 100, 200, 300 };

    public const int ArtistasPorDefecto = 100;

    /// <summary>Plazos que se pueden mirar, en días.</summary>
    public static readonly int[] OpcionesDias = { 30, 60, 90 };

    public const int DiasPorDefecto = 30;

    /// <summary>La opción más cercana a un valor guardado (un config.net.json editado a mano puede traer cualquier cosa).</summary>
    public static int Normalizar(int valor, IReadOnlyList<int> opciones, int porDefecto)
        => valor <= 0 ? porDefecto : opciones.OrderBy(o => Math.Abs(o - valor)).ThenBy(o => o).First();

    public static string NombreTipo(TipoLanzamiento tipo) => tipo switch
    {
        TipoLanzamiento.Single => "Single",
        TipoLanzamiento.Ep => "EP",
        TipoLanzamiento.Recopilatorio => "Recopilatorio",
        _ => "Álbum",
    };

    /// <summary>¿Pasa el filtro de tipo? Los recopilatorios cuentan como álbumes.</summary>
    public static bool EsDelTipo(TipoLanzamiento tipo, FiltroTipoLanzamiento filtro) => filtro switch
    {
        FiltroTipoLanzamiento.Singles => tipo == TipoLanzamiento.Single,
        FiltroTipoLanzamiento.Ep => tipo == TipoLanzamiento.Ep,
        FiltroTipoLanzamiento.Albumes => tipo is TipoLanzamiento.Album or TipoLanzamiento.Recopilatorio,
        _ => true,
    };

    /// <summary>Las canciones que forman la lista con esos filtros, en el mismo orden.</summary>
    public static List<PistaNovedad> Filtrar(IEnumerable<PistaNovedad> pistas, FiltroNovedades filtro)
        => pistas.Where(p => EsDelTipo(p.Lanzamiento.Tipo, filtro.Tipo)
                          && (!filtro.SoloEstrenos || p.EsEstreno)
                          && (filtro.IncluirSinConfirmar || !p.DeArtistaDudoso))
                 .ToList();

    /// <summary>
    /// Sin lo que salió por ese artista de la biblioteca, tras corregirlo o excluirlo: las canciones que
    /// salieron solo por él se quitan, y de las que salieron también por otros se le quita de «Por».
    /// </summary>
    public static List<PistaNovedad> SinLasDe(IEnumerable<PistaNovedad> pistas, string artista)
    {
        var salida = new List<PistaNovedad>();
        foreach (var p in pistas)
        {
            var por = p.Por.Split(", ", StringSplitOptions.RemoveEmptyEntries);
            if (!por.Contains(artista, StringComparer.OrdinalIgnoreCase)) { salida.Add(p); continue; }
            var resto = por.Where(n => !string.Equals(n, artista, StringComparison.OrdinalIgnoreCase)).ToList();
            if (resto.Count > 0) salida.Add(p with { Por = string.Join(", ", resto) });
        }
        return salida;
    }

    /// <summary>Las canciones como filas de una lista, numeradas desde 1, para cruzarlas con la biblioteca.</summary>
    public static List<ChartTrack> ComoFilas(IReadOnlyList<PistaNovedad> pistas)
        => pistas.Select((p, i) => p.ComoFila(i + 1)).ToList();

    /// <summary>
    /// Lo que conviene saber de la canción antes de buscarla: que ya había salido, que es una reedición
    /// o una versión acelerada, o que su artista no se ha podido confirmar. Vacío si nada.
    /// </summary>
    public static string Aviso(PistaNovedad p)
    {
        var partes = new List<string>();
        if (p.PublicadaAntes is { } antes)
            partes.Add(p.PublicadaEn.Length > 0
                ? $"Publicada antes, el {antes.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} en «{p.PublicadaEn}»"
                : $"Publicada antes, el {antes.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
        else if (p.EsReedicion) partes.Add("Reedición o remasterización");
        if (p.EsVersionAlterada) partes.Add("Versión acelerada, ralentizada o similar");
        if (p.DeArtistaDudoso) partes.Add("Artista sin confirmar en Deezer");
        return string.Join(" · ", partes);
    }

    /// <summary>
    /// De dónde sale la canción, para la columna «Origen» de Pendientes: «Novedades: Bicho raro
    /// (single, 16/10/2026)». Así se sabe de qué lanzamiento era meses después.
    /// </summary>
    public static string Origen(PistaNovedad p)
    {
        var tipo = p.Lanzamiento.Tipo == TipoLanzamiento.Ep ? "EP" : NombreTipo(p.Lanzamiento.Tipo).ToLowerInvariant();
        return $"Novedades: {p.Lanzamiento.Titulo} ({tipo}, {p.Lanzamiento.Fecha.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)})";
    }

    /// <summary>Cómo se describe el estado de un artista en la tabla «Artistas».</summary>
    public static string TextoEstado(FilaArtistaNovedades a)
    {
        if (a.Excluido) return "Excluido: no se buscan sus novedades";
        if (a.Estado == null) return a.FalloDeRed ? "Deezer no respondió" : "";
        var texto = a.Estado switch
        {
            EstadoArtistaDeezer.Confirmado => a.Coincidencias == 1
                ? "Confirmado (1 canción en común)"
                : $"Confirmado ({a.Coincidencias} canciones en común)",
            EstadoArtistaDeezer.Dudoso => a.Coincidencias == 1
                ? "Sin confirmar (solo 1 canción en común)"
                : "Sin confirmar (ninguna canción en común)",
            EstadoArtistaDeezer.NoEncontrado => "No está en Deezer con ese nombre",
            _ => "Corregido a mano",
        };
        return a.FalloDeRed ? texto + " · Deezer no respondió a todo" : texto;
    }

    /// <summary>
    /// Las filas de la tabla «Artistas»: los consultados, en el orden de la consulta, con lo que diga
    /// AHORA el almacén (puede haberse corregido después), y detrás los excluidos.
    /// </summary>
    public static List<FilaArtistaNovedades> FilasArtistas(IReadOnlyList<ConsultaArtista> consultas, AlmacenArtistasDeezer almacen,
                                                           IReadOnlySet<string>? corregidosDespues = null)
    {
        var filas = new List<FilaArtistaNovedades>();
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in consultas)
        {
            if (!vistos.Add(c.Artista.Clave)) continue;
            var g = almacen.Buscar(c.Artista.Clave) ?? c.Deezer;
            var desconocido = corregidosDespues?.Contains(c.Artista.Clave) == true || g?.Excluido == true;
            filas.Add(new FilaArtistaNovedades(c.Artista.Clave, c.Artista.Nombre, c.Artista.Canciones, g?.DeezerId ?? 0,
                g?.DeezerNombre ?? "", g?.Fans ?? 0, g?.Estado, g?.Coincidencias ?? 0, desconocido ? -1 : c.Recientes,
                g?.Excluido ?? false, c.FalloDeRed));
        }
        foreach (var e in almacen.Excluidos())
        {
            if (!vistos.Add(e.Clave)) continue;
            filas.Add(new FilaArtistaNovedades(e.Clave, e.Nombre, 0, e.DeezerId, e.DeezerNombre, e.Fans,
                e.DeezerId > 0 ? e.Estado : null, e.Coincidencias, -1, true, false));
        }
        return filas;
    }

    /// <summary>
    /// El resumen de una búsqueda: «100 artistas consultados: 64 lanzamientos de los últimos 30 días
    /// con 210 canciones.», y lo que haya que advertir (Deezer no respondió, artistas sin confirmar,
    /// cancelada).
    /// </summary>
    /// <param name="ocultasSinConfirmar">Canciones apartadas por ser de artistas sin confirmar (0 si se incluyen).</param>
    public static string Resumen(ResultadoNovedades r, int dias, int ocultasSinConfirmar)
    {
        var artistas = r.Artistas.Count;
        var texto = (artistas == 1 ? "1 artista consultado" : $"{artistas} artistas consultados") + ": "
                  + (r.Lanzamientos == 0
                        ? $"ningún lanzamiento en los últimos {dias} días."
                        : (r.Lanzamientos == 1 ? "1 lanzamiento" : $"{r.Lanzamientos} lanzamientos")
                          + $" en los últimos {dias} días, "
                          + (r.Pistas.Count == 1 ? "con 1 canción." : $"con {r.Pistas.Count} canciones."));

        var noEncontrados = r.Artistas.Count(a => a.Deezer is { Estado: EstadoArtistaDeezer.NoEncontrado });
        var sinConfirmar = r.Artistas.Count(a => a.Deezer is { Estado: EstadoArtistaDeezer.Dudoso });
        if (sinConfirmar > 0)
            texto += sinConfirmar == 1 ? " 1 artista sin confirmar en Deezer" : $" {sinConfirmar} artistas sin confirmar en Deezer";
        if (sinConfirmar > 0 && ocultasSinConfirmar > 0)
            texto += ocultasSinConfirmar == 1 ? " (su canción no se muestra)." : $" ({ocultasSinConfirmar} canciones que no se muestran).";
        else if (sinConfirmar > 0) texto += ".";
        if (noEncontrados > 0)
            texto += noEncontrados == 1 ? " 1 artista no está en Deezer con ese nombre." : $" {noEncontrados} artistas no están en Deezer con ese nombre.";

        var conFallo = r.Artistas.Count(a => a.FalloDeRed);
        if (conFallo > 0)
            texto += conFallo == 1
                ? " Deezer no respondió con 1 artista: puede faltar algo suyo."
                : $" Deezer no respondió con {conFallo} artistas: puede faltar algo suyo.";
        if (r.LanzamientosSinLeer > 0)
            texto += r.LanzamientosSinLeer == 1
                ? " No se pudieron leer las canciones de 1 lanzamiento."
                : $" No se pudieron leer las canciones de {r.LanzamientosSinLeer} lanzamientos.";
        if (r.Cancelada) texto += " Búsqueda cancelada: es lo encontrado hasta ese momento.";
        return texto;
    }

    /// <summary>
    /// Avance de 0 a 100 de toda la búsqueda. La primera fase (identificar a cada artista y leer su
    /// catálogo) es la larga: MEDIDO con 200 artistas, 1.287 peticiones, de ellas 769 en la primera
    /// fase, 438 en la segunda y 80 en la tercera.
    /// </summary>
    public static double Avance(ProgresoNovedades p)
    {
        var parte = p.Total <= 0 ? 1.0 : Math.Clamp((double)p.Hechos / p.Total, 0, 1);
        return p.Fase switch
        {
            1 => parte * 60,
            2 => 60 + parte * 34,
            _ => 94 + parte * 6,
        };
    }

    /// <summary>Lo que se dice en la barra de estado mientras se busca.</summary>
    public static string TextoAvance(ProgresoNovedades p) => p.Fase switch
    {
        1 => $"Consultando artistas en Deezer: {p.Hechos} de {p.Total} ({p.Que})…",
        2 => $"Leyendo las canciones de los lanzamientos: {p.Hechos} de {p.Total}…",
        _ => $"Comprobando si ya se habían publicado antes: {p.Hechos} de {p.Total}…",
    };
}
