using CommunityToolkit.Mvvm.ComponentModel;

namespace Etiquetador.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>
/// Lo que una página le cuenta a la barra de estado del pie de la ventana.
///
/// Antes cada pestaña pintaba su estado donde le venía bien: a media altura en unas, bajo los
/// botones en otras, y en algunas dos veces. Buscar dónde te está hablando la aplicación no debería
/// ser parte del trabajo. Con esto el estado sale SIEMPRE en el mismo sitio.
///
/// No hace falta implementar nada nuevo: las propiedades ya existían con estos nombres en todas las
/// pestañas. La interfaz solo pone por escrito lo que ya cumplían.
/// </summary>
public interface IEstadoPagina
{
    /// <summary>Qué está pasando, en una línea y en lenguaje llano.</summary>
    string Status { get; }

    /// <summary>Hay un proceso largo en marcha.</summary>
    bool IsBusy { get; }
}

/// <summary>
/// Páginas que además saben decir por dónde van, de 0 a 100. Va aparte porque no todas pueden:
/// contar canciones ya escaneadas tiene avance, y repasar una tabla en memoria no.
/// </summary>
public interface IProgresoPagina
{
    double Progress { get; }
}
