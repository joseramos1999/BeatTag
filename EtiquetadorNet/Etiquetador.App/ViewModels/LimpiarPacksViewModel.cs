using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

// =====================================================================================================
// Asistente · Limpiar la publicidad de los packs (idea P8). Solo reglas, sin IA.
// =====================================================================================================

/// <summary>Una etiqueta con publicidad de un pack: el álbum o el artista de una canción.</summary>
public sealed partial class PublicidadFila : ObservableObject, IFilaConArchivo, IFilaMarcable
{
    public PropuestaPublicidad Propuesta { get; }

    public PublicidadFila(PropuestaPublicidad p, bool alComentario)
    {
        Propuesta = p;
        _aplicar = p.Marcada;
        _nuevo = p.Nuevo;
        _alComentario = alComentario;
    }

    [ObservableProperty] private bool _aplicar;

    /// <summary>Editable: si la propuesta está casi bien, se corrige aquí antes de aplicarla.</summary>
    [ObservableProperty] private string _nuevo;

    /// <summary>Lo que se quita va al comentario (lo decide la opción de la herramienta, para todas las filas).</summary>
    [ObservableProperty] private bool _alComentario;

    partial void OnAlComentarioChanged(bool value) => OnPropertyChanged(nameof(LoQueSeQuita));

    string IFilaConArchivo.RutaArchivo => Ruta;
    bool IFilaMarcable.Marcada { get => Aplicar; set => Aplicar = value; }

    public string Ruta => Propuesta.Ruta;
    public bool EsAlbum => Propuesta.Campo == CampoPublicidad.Album;
    public string Archivo => Path.GetFileName(Propuesta.Ruta);
    public string Campo => EsAlbum ? "Álbum" : "Artista";
    public string Actual => Propuesta.Actual;
    public string PorQue => Propuesta.PorQue;

    /// <summary>Qué pasa con el texto que se quita, según la opción elegida.</summary>
    public string LoQueSeQuita
    {
        get
        {
            var actual = Propuesta.Actual.Trim();
            if (!AlComentario) return "Se borra";
            if (EsAlbum) return "Al comentario: " + LimpiezaPacks.BloquePack(actual);
            return actual.StartsWith('@') ? "Al comentario: " + LimpiezaPacks.BloqueEditor(actual) : "Se borra (lleva el nombre del pool)";
        }
    }
}

public sealed partial class LimpiarPacksViewModel : ObservableObject
{
    private readonly AsistenteViewModel _p;

    public LimpiarPacksViewModel(AsistenteViewModel padre)
    {
        _p = padre;
        _destino = padre.Engine.Config.PacksAlComentario ? 0 : 1;
    }

    /// <summary>Las propuestas. Se sustituye entera al proponer: añadir dos mil filas una a una avisaba a la tabla dos mil veces.</summary>
    [ObservableProperty] private ObservableCollection<PublicidadFila> _filas = new();
    [ObservableProperty] private string _resumen = "";

    public IReadOnlyList<string> Destinos { get; } = new[] { "Guardarlo en el comentario", "Borrarlo" };

    /// <summary>0 = lo que se quita va al comentario; 1 = se borra. Se recuerda entre sesiones.</summary>
    [ObservableProperty] private int _destino;

    public bool AlComentario => Destino != 1;

    partial void OnDestinoChanged(int value)
    {
        foreach (var f in Filas) f.AlComentario = AlComentario;
        if (_p.Engine.Config.PacksAlComentario == AlComentario) return;
        _p.Engine.Config.PacksAlComentario = AlComentario;
        _p.Engine.SaveConfig();
    }

    [RelayCommand]
    private Task ProponerAsync() => _p.TrabajarAsync("Buscando la publicidad de los packs…", async ct =>
    {
        if (!_p.BibliotecaLista(out var motivo)) { _p.Status = motivo; return; }

        var canciones = _p.Engine.Library.Tracks.ToList();   // instantánea en el hilo de la interfaz
        var (propuestas, sinPropuesta) = await Task.Run(() =>
        {
            var l = LimpiezaPacks.Proponer(canciones, ct);
            return (l, LimpiezaPacks.ArtistasSinPropuesta(canciones, l));
        }, ct);

        // Las del mismo pack, juntas: se revisan (y se marcan o desmarcan) de una vez.
        var alComentario = AlComentario;
        Filas = new ObservableCollection<PublicidadFila>(propuestas
            .OrderBy(p => p.Campo)
            .ThenBy(p => p.Actual, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => Path.GetFileName(p.Ruta), StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PublicidadFila(p, alComentario)));

        Resumen = TextoResumen(propuestas, sinPropuesta);
        _p.Status = propuestas.Count == 0
            ? "No hay publicidad de packs en el álbum ni en el artista." + (sinPropuesta > 0 ? " " + TextoSinPropuesta(sinPropuesta) + "." : "")
            : Resumen + ". Lo dudoso sale desmarcado: la columna «Por qué» explica cada caso.";
    });

    internal static string TextoResumen(IReadOnlyCollection<PropuestaPublicidad> propuestas, int sinPropuesta)
    {
        var albumes = propuestas.Count(p => p.Campo == CampoPublicidad.Album);
        var artistas = propuestas.Count(p => p.Campo == CampoPublicidad.Artista);
        var texto = $"{EscrituraPorReglas.Cuenta(albumes, "álbum", "álbumes")} y {EscrituraPorReglas.Cuenta(artistas, "artista", "artistas")} con publicidad";
        return sinPropuesta > 0 ? $"{texto} · {TextoSinPropuesta(sinPropuesta)}" : texto;
    }

    /// <summary>Cuenta canciones, no artistas distintos: un mismo «@usuario» puede firmar muchas.</summary>
    internal static string TextoSinPropuesta(int canciones)
        => canciones == 1
            ? "1 canción con un artista «@usuario» o de un pool se queda sin propuesta: el nombre del archivo no deja claro cuál es el artista"
            : $"{canciones:N0} canciones con un artista «@usuario» o de un pool se quedan sin propuesta: el nombre del archivo no deja claro cuál es el artista";

    [RelayCommand]
    private void MarcarTodas() { foreach (var f in Filas) f.Aplicar = true; }

    [RelayCommand]
    private void MarcarNinguna() { foreach (var f in Filas) f.Aplicar = false; }

    /// <summary>Las marcadas que se pueden aplicar: un artista vaciado a mano no se escribe (quedaría sin artista).</summary>
    public IReadOnlyList<PublicidadFila> Marcadas
        => Filas.Where(f => f.Aplicar && (f.EsAlbum || f.Nuevo.Trim().Length > 0)).ToList();

    /// <summary>Los textos de la confirmación de «Aplicar las marcadas»: cuántas, qué pasa con lo que se quita y lo que hay que saber.</summary>
    internal static (string Cuerpo, string Nota) Confirmacion(IReadOnlyList<PublicidadFila> marcadas, bool alComentario)
    {
        var canciones = marcadas.Select(f => f.Ruta).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var albumes = marcadas.Count(f => f.EsAlbum);
        var artistas = marcadas.Count - albumes;
        var cuerpo = $"Se limpiarán las etiquetas de {EscrituraPorReglas.Cuenta(canciones, "canción", "canciones")}: "
                     + $"{EscrituraPorReglas.Cuenta(albumes, "álbum", "álbumes")} y {EscrituraPorReglas.Cuenta(artistas, "artista", "artistas")}.";
        var nota = (alComentario
                       ? "Lo que se quita se guarda al final del comentario («[Pack: …]», «[Editor: …]»). "
                       : "Lo que se quita se borra. ")
                 + "Si una etiqueta ha cambiado desde que se propuso, se deja como está. "
                 + "rekordbox no vuelve a leer las etiquetas de las pistas ya importadas hasta que usas «Recargar etiqueta». "
                 + "Se puede deshacer desde Enriquecer.";
        if (artistas > 0)
            nota = "Duplicados y la Bandeja reconocen cada canción por su artista y su título: después, vuelve a buscar duplicados. " + nota;
        var dudosas = marcadas.Count(f => !f.Propuesta.Marcada);
        if (dudosas > 0)
            nota = (dudosas == 1
                       ? "⚠ 1 no salía marcada porque es dudosa: comprueba que la has revisado. "
                       : $"⚠ {dudosas:N0} no salían marcadas porque son dudosas: comprueba que las has revisado. ")
                   + nota;
        return (cuerpo, nota);
    }

    /// <summary>
    /// Los cambios por archivo: el álbum y el artista de una misma canción van juntos, en una sola
    /// escritura y un solo registro para deshacer.
    /// </summary>
    internal static List<CambioEtiquetas> Cambios(IEnumerable<PublicidadFila> marcadas, bool alComentario)
        => marcadas.GroupBy(f => f.Ruta, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var c = new CambioEtiquetas(g.First().Ruta) { GuardarEnComentario = alComentario };
            foreach (var f in g)
                c = f.EsAlbum
                    ? c with { AlbumVisto = f.Actual, AlbumNuevo = f.Nuevo.Trim() }
                    : c with { ArtistaVisto = f.Actual, ArtistaNuevo = f.Nuevo.Trim() };
            return c;
        }).ToList();

    /// <summary>Escribe las marcadas. La confirmación la pide la vista.</summary>
    public Task AplicarAsync() => _p.EscribirArchivosAsync("Limpiando la publicidad de los packs…", async ct =>
    {
        var marcadas = Marcadas;
        if (marcadas.Count == 0) { _p.Status = "No hay ninguna propuesta marcada."; return; }

        _p.Engine.ReleaseAudio();
        var cambios = Cambios(marcadas, AlComentario);
        var progreso = new Progress<double>(v => _p.Progress = v);
        var res = await Task.Run(() => EscrituraPorReglas.EscribirConHistorial(_p.Engine, cambios, progreso, ct, "Limpiar packs",
                                                                                Etiquetador.Core.Pipeline.TipoEjecucion.LimpiezaPacks));

        var quedan = QuedanTrasAplicar(Filas, marcadas, res);
        if (quedan.Count != Filas.Count) Filas = new ObservableCollection<PublicidadFila>(quedan);
        if (res.Escritos.Count > 0) await _p.Engine.Library.RefrescarAsync(res.Escritos);   // solo lo escrito, sin recorrer las carpetas

        var escritos = new HashSet<string>(res.Escritos, StringComparer.OrdinalIgnoreCase);
        var artistas = marcadas.Count(f => !f.EsAlbum && escritos.Contains(f.Ruta));
        _p.Status = $"Etiquetas limpiadas en {EscrituraPorReglas.Cuenta(res.Escritos.Count, "canción", "canciones")}" + EscrituraPorReglas.Cola(res)
                    + (res.Escritos.Count > 0 ? " " + AvisoTrasAplicar(artistas) : "");
    });

    /// <summary>
    /// Las filas que siguen en la tabla tras aplicar. Salen las que se aplicaron (marcadas en ese
    /// momento) de los archivos escritos o con cambios posteriores (su propuesta ya no vale). Las
    /// DESMARCADAS se quedan aunque su archivo se escribiera por la otra etiqueta: visto en revisión,
    /// con el artista «@usuario» marcado y el álbum «El Cangri.com» desmarcado en el mismo archivo, el
    /// álbum desaparecía de la tabla sin haberse aplicado y no volvía hasta buscar otra vez. Siguen
    /// valiendo: lo aplicado no toca su campo, y si su campo cambiara, al aplicarla se respetaría.
    /// </summary>
    internal static List<PublicidadFila> QuedanTrasAplicar(IEnumerable<PublicidadFila> filas, IReadOnlyCollection<PublicidadFila> aplicadas,
                                                         EscrituraPorReglas.Resultado res)
    {
        var hechos = new HashSet<string>(res.Escritos.Concat(res.Respetados), StringComparer.OrdinalIgnoreCase);
        var enviadas = new HashSet<PublicidadFila>(aplicadas);
        return filas.Where(f => !(enviadas.Contains(f) && hechos.Contains(f.Ruta))).ToList();
    }

    /// <summary>
    /// Lo que conviene saber después: rekordbox no se entera solo, y el artista es parte de cómo se
    /// reconocen los duplicados.
    /// </summary>
    internal static string AvisoTrasAplicar(int artistasCambiados)
    {
        var texto = "rekordbox no vuelve a leer las etiquetas de las pistas que ya tiene importadas: selecciónalas allí y usa «Recargar etiqueta».";
        if (artistasCambiados > 0)
            texto += $" Has cambiado el artista de {EscrituraPorReglas.Cuenta(artistasCambiados, "canción", "canciones")}: "
                     + "Duplicados y la Bandeja reconocen cada canción por su artista y su título, así que vuelve a buscar duplicados para ver los grupos al día.";
        return texto;
    }
}
