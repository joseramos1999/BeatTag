using CommunityToolkit.Mvvm.ComponentModel;

namespace Etiquetador.App.Services;

/// <summary>Una carpeta de la biblioteca con su estado marcado/desmarcado (para incluir/excluir del análisis).</summary>
public sealed partial class FolderItem : ObservableObject
{
    public string Path { get; }
    [ObservableProperty] private bool _enabled = true;

    public FolderItem(string path, bool enabled = true) { Path = path; _enabled = enabled; }
}
