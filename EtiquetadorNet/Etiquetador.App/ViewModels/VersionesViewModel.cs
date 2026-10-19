using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>Una pieza en la lista de versiones.</summary>
public sealed class VersionFila : IFilaConArchivo
{
    public PiezaVersion Pieza { get; }
    public VersionFila(PiezaVersion pieza) => Pieza = pieza;
    string IFilaConArchivo.RutaArchivo => FilePath;

    public string FilePath => Pieza.Track.FilePath;
    public string Archivo => Pieza.Track.FileName;
    public string Tipo => IndiceVersiones.Nombre(Pieza.Tipo);
    public string Titulo => string.IsNullOrWhiteSpace(Pieza.Track.Title) ? Path.GetFileNameWithoutExtension(FilePath) : Pieza.Track.Title;
    public string Artista => Pieza.Track.Artist ?? "";
    public uint BpmValor => Pieza.Track.Bpm;
    public string Bpm => Pieza.Track.Bpm > 0 ? Pieza.Track.Bpm.ToString() : "";
    public string Tono => Pieza.Track.KeyCamelot;
    public string Cancion => Pieza.Cancion;
    /// <summary>Marca la canción desde la que se ha abierto el diálogo.</summary>
    public string Esta => Pieza.EsLaConsultada ? "●" : "";
}

/// <summary>
/// «Ver sus versiones»: todas las piezas que tienes de una canción (original, intros y extended,
/// remixes, acapellas, instrumentales, ediciones de DJ y los mashups que la usan). Las reglas están en
/// <see cref="IndiceVersiones"/>; aquí se presenta el resultado.
/// </summary>
public sealed partial class VersionesViewModel : ObservableObject
{
    /// <summary>
    /// El índice de versiones de cada biblioteca, reutilizado hasta que cambie. MEDIDO en una biblioteca
    /// de 13.590 canciones: construirlo cuesta unos 2 s (cada nombre pasa por la lectura y la limpieza de
    /// títulos) y comprobar que no ha cambiado, 8 ms; cada consulta después es inmediata.
    /// </summary>
    private static readonly ConditionalWeakTable<LibraryStore, CacheIndiceVersiones> PorBiblioteca = new();

    /// <summary>La caché de esa biblioteca; sin biblioteca (pruebas), una propia de cada diálogo.</summary>
    internal static CacheIndiceVersiones CacheDe(LibraryStore? store)
        => store == null ? new CacheIndiceVersiones() : PorBiblioteca.GetValue(store, _ => new CacheIndiceVersiones());

    private readonly EntornoCanciones _entorno;
    private readonly Track? _consultada;
    private readonly HashSet<string> _escuchadas = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<VersionFila> Filas { get; } = new();

    [ObservableProperty] private VersionFila? _seleccionada;
    [ObservableProperty] private string _titulo = "";
    [ObservableProperty] private string _detalle = "";
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _ocupado;

    /// <summary>La consultada es un mashup: la tabla enseña con cuál de sus canciones casa cada pieza.</summary>
    [ObservableProperty] private bool _esMashup;

    public VersionesViewModel(EntornoCanciones entorno, Track? consultada)
    {
        _entorno = entorno;
        _consultada = consultada;
    }

    /// <summary>Busca las versiones. El índice se construye (o se reutiliza) fuera del hilo de la interfaz.</summary>
    public async Task CargarAsync()
    {
        if (_consultada is not { } q)
        {
            Titulo = "Versiones";
            Detalle = "Esa canción ya no está en la biblioteca. Vuelve a escanear y prueba otra vez.";
            return;
        }

        Titulo = $"Versiones de «{EntornoCanciones.Nombre(q)}»";
        Ocupado = true;
        Status = "Buscando versiones…";
        var cache = CacheDe(_entorno.Store);
        IReadOnlyList<PiezaVersion> piezas;
        try
        {
            piezas = await Task.Run(() => cache.Obtener(_entorno.Biblioteca, out _).De(q));
        }
        finally { Ocupado = false; }

        Filas.Clear();
        foreach (var p in piezas) Filas.Add(new VersionFila(p));
        EsMashup = piezas.Any(p => p.Cancion.Length > 0);
        Detalle = EsMashup
            ? "Es un mashup: se buscan las versiones de cada una de las canciones que lleva. La columna «Canción» dice de cuál es cada pieza."
            : "Se reconocen por el título sin la versión. Si el título es corto o muy común, solo cuentan las que comparten algún artista.";
        Resumen = TextoResumen(piezas);
        Status = "";
    }

    /// <summary>«7 piezas · Original: 1 · Extended / Intro: 2 · Mashup / transición: 4».</summary>
    internal static string TextoResumen(IReadOnlyList<PiezaVersion> piezas)
    {
        if (piezas.Count <= 1) return "No hay otras versiones de esta canción en la biblioteca.";
        var partes = piezas.GroupBy(p => p.Tipo).OrderBy(g => g.Key)
                           .Select(g => $"{IndiceVersiones.Nombre(g.Key)}: {g.Count()}");
        return $"{piezas.Count} piezas · {string.Join(" · ", partes)}";
    }

    [RelayCommand]
    private void Escuchar()
    {
        if (Seleccionada is not { } f) { Status = "Selecciona antes una pieza."; return; }
        _escuchadas.Add(f.FilePath);
        Status = _entorno.Escuchar(f.FilePath);
    }

    [RelayCommand]
    private async Task AbrirCarpetaAsync()
    {
        if (Seleccionada is { } f) await Shell.OpenContainingFolderAsync(f.FilePath);
    }

    /// <summary>Al cerrar: si suena algo que se puso desde aquí, se para.</summary>
    public void AlCerrar() => _entorno.PararSiSuena(_escuchadas);
}
