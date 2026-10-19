using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.App.ViewModels;

/// <summary>Una opción de lista desplegable. Valor null = «varias» (la selección no coincide).</summary>
public sealed record Opcion<T>(T? Valor, string Texto) where T : struct
{
    public override string ToString() => Texto;
}

/// <summary>
/// Una canción en una tabla con su ficha de DJ. La usan la página de fichas, las colecciones y la
/// bandeja, para que las tres enseñen la ficha con las mismas palabras.
/// </summary>
public partial class FichaRow : ObservableObject, IFilaConArchivo
{
    public Track Track { get; }
    public FichaDj? Ficha { get; private set; }

    public FichaRow(Track track, FichaDj? ficha)
    {
        Track = track;
        Ficha = ficha;
    }

    public string FilePath => Track.FilePath;
    string IFilaConArchivo.RutaArchivo => FilePath;
    public string FileName => Track.FileName;
    public string Artist => Track.Artist ?? "";
    public string Title => Track.Title ?? "";
    public string Genre => Track.Genre ?? "";
    public string Folder => Track.Folder;
    public uint Bpm => Track.Bpm;
    public string BpmTexto => Track.Bpm > 0 ? Track.Bpm.ToString() : "";
    public string Camelot => Track.KeyCamelot;
    public string Duracion => Track.Duration;

    public bool TieneFicha => Ficha != null && !Ficha.EstaVacia;
    public int EnergiaValor => Ficha?.Energia ?? 0;
    public string Energia => EnergiaValor > 0 ? EnergiaValor.ToString() : "";
    public string Momentos => Ficha == null ? "" : Fichas.NombreMomentos(Ficha.Momentos);
    public string Voz => Ficha == null ? "" : Fichas.Nombre(Ficha.Voz);
    public string Letra => Fichas.Nombre(Fichas.LetraEfectiva(Ficha, Track));
    public string Idioma => Ficha?.Idioma ?? "";
    public string Ambiente => Fichas.UnirLista(Ficha?.Ambiente);
    public string Etiquetas => Fichas.UnirLista(Ficha?.Etiquetas);
    public string ArmaSecreta => Ficha?.ArmaSecreta == true ? "★" : "";

    /// <summary>Cambia la ficha y avisa de que TODAS las columnas pueden haber cambiado.</summary>
    public void Actualizar(FichaDj? ficha)
    {
        Ficha = ficha;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// El panel donde se rellena la ficha de una o varias canciones.
///
/// Con varias seleccionadas enseña lo que tienen en común, y al guardar aplica solo lo que se ha
/// tocado (ver <see cref="CambiosFicha"/>). Es la única forma razonable de fichar una biblioteca
/// grande: se seleccionan cuarenta temas, se les marca «Pico», y a ninguno se le borra lo que ya
/// tenía.
/// </summary>
public partial class FichaEditorViewModel : ViewModelBase
{
    private FichaComun _inicial = FichaComun.Vacia;
    private IReadOnlyList<FichaDj?> _cargadas = Array.Empty<FichaDj?>();
    private bool _cargando;

    public ObservableCollection<Opcion<int>> OpcionesEnergia { get; } = new();
    public ObservableCollection<Opcion<TipoVoz>> OpcionesVoz { get; } = new();
    public ObservableCollection<Opcion<TipoLetra>> OpcionesLetra { get; } = new();

    [ObservableProperty] private Opcion<int>? _energia;
    [ObservableProperty] private Opcion<TipoVoz>? _voz;
    [ObservableProperty] private Opcion<TipoLetra>? _letra;

    [ObservableProperty] private bool _apertura;
    [ObservableProperty] private bool _subida;
    [ObservableProperty] private bool _pico;
    [ObservableProperty] private bool _cierre;
    [ObservableProperty] private bool _after;

    [ObservableProperty] private string _idioma = "";
    [ObservableProperty] private string _idiomaMarcador = "";
    [ObservableProperty] private string _ambiente = "";
    [ObservableProperty] private string _etiquetas = "";
    [ObservableProperty] private bool? _armaSecreta = false;

    [ObservableProperty] private int _cuantas;
    [ObservableProperty] private string _titulo = "Selecciona una o varias canciones.";
    [ObservableProperty] private bool _hayCambios;

    /// <summary>Con varias seleccionadas, explica qué se ve y qué pasa al guardar.</summary>
    [ObservableProperty] private string _nota = "";

    public bool HaySeleccion => Cuantas > 0;

    /// <summary>La página que lo contiene guarda: sabe a qué canciones corresponde la selección.</summary>
    public event Action? GuardarPedido;

    public FichaEditorViewModel() => Cargar(Array.Empty<FichaDj?>());

    [RelayCommand]
    private void Guardar() => GuardarPedido?.Invoke();

    /// <summary>
    /// Si hay algo escrito sin guardar, lo guarda (pidiéndoselo a la página, que sabe a qué canciones
    /// corresponde). Devuelve true si guardó. Se llama ANTES de cargar otra selección o de rehacer la
    /// tabla.
    ///
    /// Antes, escribir «boda, público mayor, cierre» en Etiquetas de 30 temas y hacer clic en otra fila
    /// para comprobar algo lo borraba sin aviso ni forma de recuperarlo. Guardar sin preguntar es seguro:
    /// solo se aplica lo que se ha tocado (<see cref="CambiosFicha"/>), y lo que no se quería se quita
    /// igual que se puso.
    /// </summary>
    public bool GuardarSiHayCambios()
    {
        if (Cuantas == 0 || !HayCambios || !Cambios().HayAlguno) return false;
        GuardarPedido?.Invoke();
        return true;
    }

    /// <summary>Vuelve a lo que había antes de tocar nada.</summary>
    [RelayCommand]
    private void Descartar() => Cargar(_cargadas);

    /// <summary>Enseña la ficha común de las canciones seleccionadas.</summary>
    public void Cargar(IReadOnlyList<FichaDj?> fichas)
    {
        _cargando = true;
        try
        {
            _cargadas = fichas;
            _inicial = FichaComun.De(fichas);
            Cuantas = fichas.Count;
            OnPropertyChanged(nameof(HaySeleccion));

            Titulo = fichas.Count switch
            {
                0 => "Selecciona una o varias canciones.",
                1 => "Ficha de la canción",
                _ => $"Ficha de {fichas.Count} canciones",
            };
            Nota = fichas.Count > 1
                ? "Se muestra lo que tienen en común. Al guardar solo se aplica lo que cambies; el resto de cada ficha se conserva."
                : "";

            Rellenar(OpcionesEnergia, _inicial.Energia == null ? "Varias" : null,
                     new[] { new Opcion<int>(0, "Sin indicar") }
                         .Concat(Enumerable.Range(1, Fichas.EnergiaMaxima).Select(i => new Opcion<int>(i, i.ToString()))));
            Rellenar(OpcionesVoz, _inicial.Voz == null ? "Varias" : null,
                     new[] { new Opcion<TipoVoz>(TipoVoz.SinIndicar, "Sin indicar"),
                             new Opcion<TipoVoz>(TipoVoz.Vocal, "Vocal"),
                             new Opcion<TipoVoz>(TipoVoz.Instrumental, "Instrumental") });
            Rellenar(OpcionesLetra, _inicial.Letra == null ? "Varias" : null,
                     new[] { new Opcion<TipoLetra>(TipoLetra.SinIndicar, "Sin indicar"),
                             new Opcion<TipoLetra>(TipoLetra.Limpia, "Limpia"),
                             new Opcion<TipoLetra>(TipoLetra.Explicita, "Explícita") });

            Energia = OpcionesEnergia.First(o => o.Valor == _inicial.Energia);
            Voz = OpcionesVoz.First(o => o.Valor == _inicial.Voz);
            Letra = OpcionesLetra.First(o => o.Valor == _inicial.Letra);

            Apertura = (_inicial.Momentos & MomentoSet.Apertura) != 0;
            Subida = (_inicial.Momentos & MomentoSet.Subida) != 0;
            Pico = (_inicial.Momentos & MomentoSet.Pico) != 0;
            Cierre = (_inicial.Momentos & MomentoSet.Cierre) != 0;
            After = (_inicial.Momentos & MomentoSet.After) != 0;

            Idioma = _inicial.Idioma ?? "";
            IdiomaMarcador = _inicial.Idioma == null ? "Varios" : "ES, EN, PT…";
            Ambiente = Fichas.UnirLista(_inicial.Ambiente);
            Etiquetas = Fichas.UnirLista(_inicial.Etiquetas);
            ArmaSecreta = _inicial.ArmaSecreta;
            HayCambios = false;
        }
        finally { _cargando = false; }
    }

    private static void Rellenar<T>(ObservableCollection<Opcion<T>> lista, string? varias, IEnumerable<Opcion<T>> opciones) where T : struct
    {
        lista.Clear();
        if (varias != null) lista.Add(new Opcion<T>(null, varias));
        foreach (var o in opciones) lista.Add(o);
    }

    /// <summary>Lo que se ha cambiado respecto a lo que se enseñó.</summary>
    public CambiosFicha Cambios() => CambiosFicha.Calcular(_inicial, Editado());

    private FichaComun Editado()
    {
        var momentos = MomentoSet.Ninguno;
        if (Apertura) momentos |= MomentoSet.Apertura;
        if (Subida) momentos |= MomentoSet.Subida;
        if (Pico) momentos |= MomentoSet.Pico;
        if (Cierre) momentos |= MomentoSet.Cierre;
        if (After) momentos |= MomentoSet.After;

        return new FichaComun(
            Energia?.Valor,
            momentos,
            Voz?.Valor,
            Letra?.Valor,
            // Con «varios» y la casilla vacía, el idioma no se ha tocado.
            _inicial.Idioma == null && Idioma.Trim().Length == 0 ? null : Idioma,
            Fichas.PartirLista(Ambiente),
            Fichas.PartirLista(Etiquetas),
            ArmaSecreta);
    }

    private static readonly HashSet<string> NoSonDeLaFicha = new()
    {
        nameof(HayCambios), nameof(Titulo), nameof(Cuantas), nameof(Nota), nameof(IdiomaMarcador), nameof(HaySeleccion),
    };

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_cargando || e.PropertyName == null || NoSonDeLaFicha.Contains(e.PropertyName)) return;
        HayCambios = Cuantas > 0 && Cambios().HayAlguno;
    }
}

/// <summary>
/// Las filas seleccionadas de una tabla con panel de ficha (Ficha DJ y Bandeja), y el ORDEN en que se
/// cambian: primero se guarda lo escrito en el panel, con la selección anterior todavía vigente, y
/// solo después se pasa a la nueva y se carga su ficha.
///
/// Ese orden es lo que evita escribir en canciones equivocadas. La página guarda en «las
/// seleccionadas» cuando el panel se lo pide; si la selección cambiase antes de guardar, lo escrito
/// para 30 temas de boda acabaría en la fila en la que se acaba de hacer clic. Está aparte de las
/// páginas para poder probarlo sin el motor de la aplicación.
/// </summary>
public sealed class SeleccionConFicha<T> where T : FichaRow
{
    private readonly FichaEditorViewModel _editor;

    public SeleccionConFicha(FichaEditorViewModel editor) => _editor = editor;

    /// <summary>Las filas seleccionadas ahora. Es a las que se aplica lo que se guarde.</summary>
    public IReadOnlyList<T> Filas { get; private set; } = Array.Empty<T>();

    /// <summary>
    /// Guarda lo escrito en el panel para las filas seleccionadas ahora, si hay algo. Devuelve true si
    /// guardó. Lo llaman las páginas antes de rehacer su tabla, al salir de ellas y al cerrar la
    /// aplicación.
    /// </summary>
    public bool GuardarLoEscrito() => Filas.Count > 0 && _editor.GuardarSiHayCambios();

    /// <summary>
    /// Pasa a <paramref name="filas"/>. Lo escrito y no guardado se guarda antes en la selección
    /// ANTERIOR. Devuelve true si guardó algo.
    /// </summary>
    public bool Cambiar(IEnumerable<T> filas)
    {
        // Se copian antes de guardar: guardar relee las fichas y puede tocar la colección de origen.
        var nuevas = filas.ToList();
        var guardada = GuardarLoEscrito();
        Filas = nuevas;
        Recargar();
        return guardada;
    }

    /// <summary>Vuelve a enseñar en el panel la ficha de las seleccionadas (tras guardar o releer).</summary>
    public void Recargar() => _editor.Cargar(Filas.Select(r => r.Ficha).ToList());
}
