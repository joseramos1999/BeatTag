using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;

namespace Etiquetador.App.ViewModels;

/// <summary>
/// Base para las pestañas de análisis: trabajan sobre la biblioteca compartida (LibraryStore).
/// Al escanearse la biblioteca (desde cualquier pestaña) se recalcula la vista automáticamente.
/// </summary>
public abstract partial class ScanViewModelBase : ViewModelBase
{
    protected readonly LibraryStore Store;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Pulsa Analizar (usa la biblioteca compartida).";

    protected ScanViewModelBase(LibraryStore store)
    {
        Store = store;
        Store.Changed += Recompute;
    }

    /// <summary>Reconstruye las filas a partir de <c>Store.Tracks</c>. Se llama tras cada escaneo.</summary>
    protected abstract void Recompute();

    /// <summary>Si la biblioteca aún no está escaneada, la escanea (compartida); si ya lo está, recalcula.</summary>
    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;
        if (Store.Folders.Count == 0) { Status = "No hay carpetas. Añádelas en Biblioteca o Enriquecer."; return; }
        IsBusy = true;
        try
        {
            if (!Store.IsScanned) { Status = "Escaneando biblioteca…"; await Store.ScanAsync(); } // Changed -> Recompute
            else Recompute();
        }
        finally { IsBusy = false; }
    }
}
