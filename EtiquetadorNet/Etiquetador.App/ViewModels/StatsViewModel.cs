using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Etiquetador.App.Services;
using Etiquetador.Core.Analysis;

namespace Etiquetador.App.ViewModels;

/// <summary>Una barra del gráfico: etiqueta, recuento y ancho proporcional en píxeles.</summary>
public sealed class StatBarItem
{
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public double BarWidth { get; init; }
}

/// <summary>Pestaña Estadísticas: reparto de la biblioteca por BPM, género, calidad, década y explícito.</summary>
public partial class StatsViewModel : ScanViewModelBase
{
    private const double MaxBar = 240;

    public ObservableCollection<StatBarItem> ByBpm { get; } = new();
    public ObservableCollection<StatBarItem> ByGenre { get; } = new();
    public ObservableCollection<StatBarItem> ByQuality { get; } = new();
    public ObservableCollection<StatBarItem> ByDecade { get; } = new();
    public ObservableCollection<StatBarItem> ByExplicit { get; } = new();

    [ObservableProperty] private string _summary = "Pulsa Analizar para ver las estadísticas.";

    public StatsViewModel(AppEngine engine) : base(engine.Library)
    {
        if (Store.IsScanned) Recompute();
    }

    protected override void Recompute()
    {
        var s = LibraryStats.Compute(Store.Tracks);
        Summary = $"{s.Total} canciones · {s.Incomplete} incompletas · {FormatDuration(s.TotalSeconds)} de música";
        Fill(ByBpm, s.ByBpm);
        Fill(ByGenre, s.ByGenre);
        Fill(ByQuality, s.ByQuality);
        Fill(ByDecade, s.ByDecade);
        Fill(ByExplicit, s.ByExplicit);
        Status = $"Estadísticas de {s.Total} canciones.";
    }

    private static void Fill(ObservableCollection<StatBarItem> dst, IReadOnlyList<StatItem> src)
    {
        dst.Clear();
        var max = src.Count == 0 ? 0 : src.Max(x => x.Count);
        foreach (var it in src)
            dst.Add(new StatBarItem { Label = it.Label, Count = it.Count, BarWidth = max == 0 ? 0 : (double)it.Count / max * MaxBar });
    }

    private static string FormatDuration(long sec)
    {
        var ts = TimeSpan.FromSeconds(sec);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}h {ts.Minutes}m" : $"{ts.Minutes}m";
    }
}
