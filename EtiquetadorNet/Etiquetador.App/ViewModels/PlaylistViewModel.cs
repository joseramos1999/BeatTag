using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Providers;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Pestaña Playlists: como Tendencias, pero la lista la eliges tú pegando el enlace de una
/// playlist (o un álbum) de Spotify o Deezer. Se ve qué tienes ya y qué te falta, y la lista sale
/// en M3U8 o en una carpeta con lo que tienes.
///
/// Deezer se lee entera. De Spotify solo se pueden leer las 100 primeras canciones de una lista
/// (ver <see cref="PlaylistProvider"/>), y cuando una lista llega a ese tope se dice.
/// </summary>
public partial class PlaylistViewModel : ListaFrenteBibliotecaViewModel
{
    [ObservableProperty] private string _enlace = "";
    [ObservableProperty] private string _nombreLista = "";

    public PlaylistViewModel(AppEngine engine) : base(engine)
    {
        _enlace = engine.Config.UltimaPlaylist;
        Status = "Pega el enlace de una playlist de Spotify o Deezer y pulsa Ver lista.";
        Seguimiento.SeguidasCambiadas += RellenarSeguidas;
        RellenarSeguidas();
    }

    /// <summary>
    /// El nombre de la lista, sin emojis y recortado, para el M3U8 y la carpeta. Los títulos de las
    /// playlists son a menudo de este estilo: « REGUETÓN 2026 🔥MIX REGGAETON 2026 SUMMER 😍
    /// REGUETÓN EXITOS 2026 😍LO MAS NUEVO…», que como nombre de archivo no sirve.
    /// </summary>
    public override string NombreSugerido
    {
        get
        {
            var limpio = System.Text.RegularExpressions.Regex.Replace(NombreLista, @"[^\p{L}\p{N}\s\-&'.,()]", " ");
            limpio = System.Text.RegularExpressions.Regex.Replace(limpio, @"\s{2,}", " ").Trim();
            if (limpio.Length > 60) limpio = limpio[..60].TrimEnd(' ', '-', ',', '.');
            return $"{(limpio.Length > 0 ? limpio : "Playlist")} {DateTime.Now:yyyy-MM-dd}";
        }
    }

    protected override string Etiqueta => "Playlists";

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        var enlace = (Enlace ?? "").Trim();
        if (enlace.Length == 0) { Status = "Pega primero el enlace de la playlist."; return; }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        Status = "Leyendo la lista…";
        _engine.Logger.Head($"Playlists: {enlace}");
        try
        {
            await AsegurarBibliotecaAsync();
            var lista = await _engine.Playlists.LeerAsync(enlace, _cts.Token);
            if (!lista.Ok)
            {
                Status = lista.Error;
                _engine.Logger.Detail("Playlists: " + lista.Error);
                return;
            }

            NombreLista = lista.Nombre;
            _engine.Config.UltimaPlaylist = enlace;
            _engine.SaveConfig();

            var tengo = await CruzarAsync(lista.Pistas, _cts.Token);
            _enlaceCargado = enlace;
            AnotarLectura(lista.Pistas, DateTime.Now, SeguimientoListas.LecturaCompleta(lista), SeguimientoListas.AvisoLectura(lista));
            Status = $"«{lista.Nombre}» ({lista.Plataforma}): {Rows.Count} canciones. {Resumen}. Faltan {Rows.Count - tengo}."
                   + (lista.Recortada ? " " + lista.Aviso : "")
                   + (Novedades.Length > 0 ? " " + Novedades : "");
            _engine.Logger.Sum($"Playlists «{lista.Nombre}» ({lista.Plataforma}): tienes {tengo} de {Rows.Count}"
                             + (lista.Recortada ? $" (incompleta: {lista.Aviso})" : ""));
        }
        catch (OperationCanceledException) { Status = "Consulta cancelada."; }
        catch (Exception e) { Status = "No se pudo leer la lista: " + e.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    // --- Seguir la lista (P7) ---

    /// <summary>El enlace de la lista que se está viendo (el que se cargó, no lo que haya ahora en el cuadro).</summary>
    private string _enlaceCargado = "";

    protected override ListaSeguida? DescribirLista()
        => _enlaceCargado.Length == 0 ? null : DescribirPlaylist(_enlaceCargado, NombreLista);

    /// <summary>Una playlist como lista seguida: la identifica su enlace, se pegue como se pegue.</summary>
    internal static ListaSeguida DescribirPlaylist(string enlace, string nombre) => new()
    {
        Tipo = TipoListaSeguida.Playlist,
        Clave = ListasSeguidas.ClavePlaylist(enlace),
        Nombre = string.IsNullOrWhiteSpace(nombre) ? "Playlist" : nombre.Trim(),
        Enlace = enlace.Trim(),
    };

    /// <summary>Las playlists que sigues, para volver a abrirlas sin pegar el enlace.</summary>
    public ObservableCollection<ListaSeguida> Seguidas { get; } = new();

    public bool HaySeguidas => Seguidas.Count > 0;

    /// <summary>Elegir una lista seguida la abre. Después el selector se vacía, para poder volver a elegirla.</summary>
    [ObservableProperty] private ListaSeguida? _seguidaElegida;

    partial void OnSeguidaElegidaChanged(ListaSeguida? value)
    {
        if (value == null) return;
        // Se vacía después, no aquí dentro: cambiar la selección mientras el selector la está poniendo
        // le llega a medias.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SeguidaElegida = null);
        if (IsBusy) { Status = "Espera a que termine el proceso en curso."; return; }
        Enlace = value.Enlace;
        if (LoadCommand.CanExecute(null)) LoadCommand.Execute(null);
    }

    private void RellenarSeguidas()
    {
        Seguidas.Clear();
        foreach (var l in Seguimiento.Seguidas.Todas().Where(l => l.Tipo == TipoListaSeguida.Playlist && l.Enlace.Length > 0))
            Seguidas.Add(l);
        OnPropertyChanged(nameof(HaySeguidas));
    }
}
