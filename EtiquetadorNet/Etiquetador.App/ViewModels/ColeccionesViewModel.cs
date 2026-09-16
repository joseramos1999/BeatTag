using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>Una colección en la lista de la izquierda. Existe para que renombrarla se vea al momento.</summary>
public sealed partial class ColeccionItem : ObservableObject
{
    public ColeccionInteligente Modelo { get; }
    public ColeccionItem(ColeccionInteligente modelo) => Modelo = modelo;

    public string Nombre
    {
        get => Modelo.Nombre;
        set { if (Modelo.Nombre != value) { Modelo.Nombre = value; OnPropertyChanged(); } }
    }
}

/// <summary>
/// Página Colecciones: listas que se rellenan solas a partir de unas condiciones, y que se pueden
/// llevar a cualquier programa de DJ como M3U8.
/// </summary>
public partial class ColeccionesViewModel : ScanViewModelBase
{
    private readonly AppEngine _engine;
    private readonly DispatcherTimer _espera;
    private bool _cargando;

    public ObservableCollection<ColeccionItem> Colecciones { get; } = new();
    public ObservableCollection<FichaRow> Resultados { get; } = new();
    public DataGridCollectionView ResultadosView { get; }

    [ObservableProperty] private ColeccionItem? _seleccionada;
    [ObservableProperty] private FichaRow? _selectedRow;
    [ObservableProperty] private string _resumen = "";
    [ObservableProperty] private string _etiquetasEnUso = "";

    public bool HaySeleccionada => Seleccionada != null;

    // --- Condiciones de la colección abierta ---
    [ObservableProperty] private string _nombre = "";
    [ObservableProperty] private string _texto = "";
    [ObservableProperty] private string _genero = "";
    [ObservableProperty] private decimal? _bpmMin;
    [ObservableProperty] private decimal? _bpmMax;
    [ObservableProperty] private decimal? _anioMin;
    [ObservableProperty] private decimal? _anioMax;
    [ObservableProperty] private Opcion<int>? _energiaMin;
    [ObservableProperty] private Opcion<int>? _energiaMax;
    [ObservableProperty] private bool _apertura;
    [ObservableProperty] private bool _subida;
    [ObservableProperty] private bool _pico;
    [ObservableProperty] private bool _cierre;
    [ObservableProperty] private bool _after;
    [ObservableProperty] private Opcion<TipoVoz>? _voz;
    [ObservableProperty] private Opcion<TipoLetra>? _letra;
    [ObservableProperty] private string _idioma = "";
    [ObservableProperty] private string _ambiente = "";
    [ObservableProperty] private string _etiquetas = "";
    [ObservableProperty] private bool _soloArmasSecretas;

    public IReadOnlyList<Opcion<int>> OpcionesEnergia { get; } =
        new[] { new Opcion<int>(0, "Sin límite") }
            .Concat(Enumerable.Range(1, Fichas.EnergiaMaxima).Select(i => new Opcion<int>(i, i.ToString()))).ToList();

    public IReadOnlyList<Opcion<TipoVoz>> OpcionesVoz { get; } = new[]
    {
        new Opcion<TipoVoz>(TipoVoz.SinIndicar, "Cualquiera"),
        new Opcion<TipoVoz>(TipoVoz.Vocal, "Vocal"),
        new Opcion<TipoVoz>(TipoVoz.Instrumental, "Instrumental"),
    };

    public IReadOnlyList<Opcion<TipoLetra>> OpcionesLetra { get; } = new[]
    {
        new Opcion<TipoLetra>(TipoLetra.SinIndicar, "Cualquiera"),
        new Opcion<TipoLetra>(TipoLetra.Limpia, "Limpia"),
        new Opcion<TipoLetra>(TipoLetra.Explicita, "Explícita"),
    };

    public ColeccionesViewModel(AppEngine engine) : base(engine.Library)
    {
        _engine = engine;
        ResultadosView = new DataGridCollectionView(Resultados);

        // Escribir en una casilla no puede recalcular la biblioteca entera y guardar en disco a cada
        // tecla. Se espera a que el usuario pare un momento.
        _espera = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _espera.Tick += (_, _) => { _espera.Stop(); GuardarYRecalcular(); };

        foreach (var c in _engine.Colecciones.Todas) Colecciones.Add(new ColeccionItem(c));
        _engine.FichasCambiadas += Recompute;

        Seleccionada = Colecciones.FirstOrDefault();
        if (Seleccionada == null) CargarCondiciones(null);
        Status = Colecciones.Count == 0 ? "Crea una colección y define sus condiciones." : "";
        if (Store.IsScanned) Recompute();
    }

    partial void OnSeleccionadaChanged(ColeccionItem? value)
    {
        OnPropertyChanged(nameof(HaySeleccionada));
        CargarCondiciones(value?.Modelo);
        Recompute();
    }

    private void CargarCondiciones(ColeccionInteligente? c)
    {
        _cargando = true;
        try
        {
            c ??= new ColeccionInteligente { Nombre = "" };
            Nombre = c.Nombre;
            Texto = c.Texto;
            Genero = c.Genero;
            BpmMin = c.BpmMin > 0 ? c.BpmMin : null;
            BpmMax = c.BpmMax > 0 ? c.BpmMax : null;
            AnioMin = c.AnioMin > 0 ? c.AnioMin : null;
            AnioMax = c.AnioMax > 0 ? c.AnioMax : null;
            EnergiaMin = OpcionesEnergia.First(o => o.Valor == c.EnergiaMin);
            EnergiaMax = OpcionesEnergia.First(o => o.Valor == c.EnergiaMax);
            Apertura = (c.Momentos & MomentoSet.Apertura) != 0;
            Subida = (c.Momentos & MomentoSet.Subida) != 0;
            Pico = (c.Momentos & MomentoSet.Pico) != 0;
            Cierre = (c.Momentos & MomentoSet.Cierre) != 0;
            After = (c.Momentos & MomentoSet.After) != 0;
            Voz = OpcionesVoz.First(o => o.Valor == c.Voz);
            Letra = OpcionesLetra.First(o => o.Valor == c.Letra);
            Idioma = c.Idioma;
            Ambiente = Fichas.UnirLista(c.Ambiente);
            Etiquetas = Fichas.UnirLista(c.Etiquetas);
            SoloArmasSecretas = c.SoloArmasSecretas;
        }
        finally { _cargando = false; }
    }

    private static readonly HashSet<string> Condiciones = new()
    {
        nameof(Nombre), nameof(Texto), nameof(Genero), nameof(BpmMin), nameof(BpmMax), nameof(AnioMin), nameof(AnioMax), nameof(EnergiaMin), nameof(EnergiaMax),
        nameof(Apertura), nameof(Subida), nameof(Pico), nameof(Cierre), nameof(After), nameof(Voz), nameof(Letra),
        nameof(Idioma), nameof(Ambiente), nameof(Etiquetas), nameof(SoloArmasSecretas),
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_cargando || Seleccionada == null || e.PropertyName == null || !Condiciones.Contains(e.PropertyName)) return;

        // El nombre se ve cambiar en la lista al momento; el resto espera a que se deje de escribir.
        if (e.PropertyName == nameof(Nombre)) Seleccionada.Nombre = Nombre;
        VolcarCondiciones(Seleccionada.Modelo);
        _espera.Stop();
        _espera.Start();
    }

    private void VolcarCondiciones(ColeccionInteligente c)
    {
        c.Texto = Texto.Trim();
        c.Genero = Genero.Trim();
        c.BpmMin = (int)Math.Max(0, BpmMin ?? 0);
        c.BpmMax = (int)Math.Max(0, BpmMax ?? 0);
        c.AnioMin = (int)Math.Max(0, AnioMin ?? 0);
        c.AnioMax = (int)Math.Max(0, AnioMax ?? 0);
        c.EnergiaMin = EnergiaMin?.Valor ?? 0;
        c.EnergiaMax = EnergiaMax?.Valor ?? 0;
        var m = MomentoSet.Ninguno;
        if (Apertura) m |= MomentoSet.Apertura;
        if (Subida) m |= MomentoSet.Subida;
        if (Pico) m |= MomentoSet.Pico;
        if (Cierre) m |= MomentoSet.Cierre;
        if (After) m |= MomentoSet.After;
        c.Momentos = m;
        c.Voz = Voz?.Valor ?? TipoVoz.SinIndicar;
        c.Letra = Letra?.Valor ?? TipoLetra.SinIndicar;
        c.Idioma = Idioma.Trim();
        c.Ambiente = Fichas.PartirLista(Ambiente);
        c.Etiquetas = Fichas.PartirLista(Etiquetas);
        c.SoloArmasSecretas = SoloArmasSecretas;
    }

    private void GuardarYRecalcular()
    {
        Guardar();
        Recompute();
    }

    private void Guardar()
    {
        var err = _engine.Colecciones.Guardar();
        if (err.Length > 0)
        {
            _engine.Logger.Err("No se pudieron guardar las colecciones: " + err);
            Status = "⚠ No se pudieron guardar las colecciones: " + err;
        }
    }

    protected override void Recompute()
    {
        Resultados.Clear();
        var c = Seleccionada?.Modelo;

        // Las fichas se leen una vez por recálculo y no una vez por canción.
        var fichas = new Dictionary<string, FichaDj>(StringComparer.OrdinalIgnoreCase);
        foreach (var ruta in _engine.Fichas.Rutas)
            if (_engine.Fichas.Obtener(ruta) is { } f) fichas[ruta] = f;
        EtiquetasEnUso = DescribirEnUso(fichas.Values);

        if (c == null) { Resumen = ""; return; }
        if (!FiltroColeccion.TieneCriterios(c))
        {
            Resumen = "";
            Status = "Define al menos una condición: sin ninguna, la colección sería la biblioteca entera.";
            return;
        }

        var filas = Store.Tracks
            .Select(t => (t, f: fichas.GetValueOrDefault(t.FilePath)))
            .Where(x => FiltroColeccion.Cumple(c, x.t, x.f))
            .OrderBy(x => x.t.Bpm == 0 ? uint.MaxValue : x.t.Bpm)
            .ThenBy(x => x.f?.Energia ?? 0)
            .ThenBy(x => x.t.Artist, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new FichaRow(x.t, x.f))
            .ToList();
        foreach (var r in filas) Resultados.Add(r);
        ResultadosView.Refresh();

        var segundos = filas.Sum(r => (long)r.Track.DurationSeconds);
        Resumen = $"{filas.Count} canciones · {DuracionTexto(segundos)}";
        Status = filas.Count == 0
            ? "Ninguna canción cumple todas las condiciones. Las que piden energía, momento o etiquetas solo encuentran canciones con ficha."
            : $"«{c.Nombre}»: {Resumen}.";
    }

    private static string DuracionTexto(long segundos)
    {
        var t = TimeSpan.FromSeconds(segundos);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min";
    }

    /// <summary>Qué ambientes y etiquetas existen, para no tener que recordarlos al escribir una condición.</summary>
    private static string DescribirEnUso(IEnumerable<FichaDj> fichas)
    {
        var lista = fichas.ToList();
        static string Top(IEnumerable<string> valores) => string.Join(", ",
            valores.GroupBy(Fichas.Clave).Where(g => g.Key.Length > 0)
                   .OrderByDescending(g => g.Count()).Take(12).Select(g => g.First()));

        var amb = Top(lista.SelectMany(f => f.Ambiente));
        var eti = Top(lista.SelectMany(f => f.Etiquetas));
        var partes = new List<string>();
        if (amb.Length > 0) partes.Add("Ambientes en uso: " + amb);
        if (eti.Length > 0) partes.Add("Etiquetas en uso: " + eti);
        return string.Join("\n", partes);
    }

    [RelayCommand]
    private void Nueva()
    {
        var c = new ColeccionInteligente { Nombre = NombreLibre("Nueva colección") };
        _engine.Colecciones.Todas.Add(c);
        var item = new ColeccionItem(c);
        Colecciones.Add(item);
        Guardar();
        Seleccionada = item;
    }

    /// <summary>Añade una colección creada en otra página (el Asistente IA) y la deja abierta.</summary>
    public void AnadirColeccion(ColeccionInteligente c)
    {
        c.Nombre = NombreLibre(c.Nombre.Trim().Length > 0 ? c.Nombre.Trim() : "Nueva colección");
        _engine.Colecciones.Todas.Add(c);
        var item = new ColeccionItem(c);
        Colecciones.Add(item);
        Guardar();
        Seleccionada = item;
    }

    [RelayCommand]
    private void Duplicar()
    {
        if (Seleccionada == null) return;
        var c = Seleccionada.Modelo.Copia(NombreLibre(Seleccionada.Modelo.Nombre + " (copia)"));
        _engine.Colecciones.Todas.Add(c);
        var item = new ColeccionItem(c);
        Colecciones.Add(item);
        Guardar();
        Seleccionada = item;
    }

    /// <summary>Borra la colección abierta. La confirmación la pide la vista. No toca ninguna canción.</summary>
    public void EliminarSeleccionada()
    {
        if (Seleccionada == null) return;
        _espera.Stop();
        var i = Colecciones.IndexOf(Seleccionada);
        _engine.Colecciones.Todas.Remove(Seleccionada.Modelo);
        Colecciones.Remove(Seleccionada);
        Guardar();
        Seleccionada = Colecciones.Count == 0 ? null : Colecciones[Math.Clamp(i, 0, Colecciones.Count - 1)];
        if (Seleccionada == null) { Resultados.Clear(); Resumen = ""; Status = "Crea una colección y define sus condiciones."; }
    }

    private string NombreLibre(string base_)
    {
        var nombre = base_;
        for (var n = 2; Colecciones.Any(c => string.Equals(c.Nombre, nombre, StringComparison.CurrentCultureIgnoreCase)); n++)
            nombre = $"{base_} {n}";
        return nombre;
    }

    /// <summary>Guarda los resultados como lista M3U8, en el orden en que se ven en la tabla.</summary>
    public void ExportarM3u(string destino)
    {
        var filas = ResultadosView.Cast<FichaRow>().ToList();
        if (filas.Count == 0) { Status = "La colección está vacía: no hay nada que guardar."; return; }
        var err = PlaylistWriter.Write(destino,
            filas.Select(r => new PlaylistItem(r.FilePath, r.Artist, r.Title, r.Track.DurationSeconds)));
        Status = err.Length > 0
            ? "No se pudo guardar la lista: " + err
            : $"Lista guardada con {filas.Count} canciones: {System.IO.Path.GetFileName(destino)}";
    }

    [RelayCommand]
    private void PlayPreview()
    {
        var path = SelectedRow?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try { _engine.Preview.Toggle(path); }
        catch (Exception e) { Status = "No se pudo reproducir: " + e.Message; }
    }

    [RelayCommand]
    private async Task OpenContainingFolderAsync() => await Shell.OpenContainingFolderAsync(SelectedRow?.FilePath);
}
