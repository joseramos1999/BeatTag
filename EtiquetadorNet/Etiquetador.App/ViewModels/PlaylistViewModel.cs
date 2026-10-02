using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
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

            var tengo = Cruzar(lista.Pistas);
            Status = $"«{lista.Nombre}» ({lista.Plataforma}): {Rows.Count} canciones. {Resumen}. Faltan {Rows.Count - tengo}."
                   + (lista.Recortada
                       ? $" Spotify solo deja leer las {PlaylistProvider.LimiteSpotify} primeras canciones de una lista: si tiene más, las demás no aparecen."
                       : "");
            _engine.Logger.Sum($"Playlists «{lista.Nombre}» ({lista.Plataforma}): tienes {tengo} de {Rows.Count}"
                             + (lista.Recortada ? " (Spotify: solo las 100 primeras)" : ""));
        }
        catch (OperationCanceledException) { Status = "Consulta cancelada."; }
        catch (Exception e) { Status = "No se pudo leer la lista: " + e.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }
}
