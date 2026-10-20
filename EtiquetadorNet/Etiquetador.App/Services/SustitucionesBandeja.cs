using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Etiquetador.App.ViewModels;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;

namespace Etiquetador.App.Services;

/// <summary>
/// Lo decidido sobre los archivos de una sustitución de la Bandeja (I14): qué se apunta de la copia
/// antigua antes de olvidarlo, y qué se devuelve a cada archivo cuando la sustitución se deshace
/// (ver <see cref="AlmacenSustituciones"/>). Va aparte del motor para poder probarlo sin él.
/// </summary>
internal static class SustitucionesBandeja
{
    /// <summary>
    /// Apunta en <paramref name="s"/> lo decidido sobre la copia antigua: si estaba aplicada o
    /// descartada en Enriquecer, sus casillas «Aplicar» y si se aceptó su aviso de «Comprobar audio».
    /// Hay que llamarlo ANTES de olvidarlo (<see cref="BandejaViewModel.OlvidarLoDecididoSobre"/>).
    /// </summary>
    public static void Apuntar(SustitucionHecha s, IgnoreList aplicadas, IgnoreList descartadas, ApplyMarks marcas,
                               IgnoreList audioAceptadas)
    {
        s.Aplicada = aplicadas.Contains(s.Vieja);
        s.Descartada = descartadas.Contains(s.Vieja);
        s.AudioAceptada = audioAceptadas.Contains(s.Vieja);
        s.Casillas.Clear();
        // ApplyMarks no deja recorrer sus claves; OlvidarSi sí las enseña, y con una condición que
        // siempre dice que no, no quita ninguna (ni marca nada para guardar).
        marcas.OlvidarSi(k =>
        {
            if (EsCasillaDe(k, s.Vieja) && marcas.Get(k) is bool v) s.Casillas[k] = v;
            return false;
        });
    }

    /// <summary>La clave de casilla es de esa ruta: con la huella de la propuesta, o solo la ruta (las de versiones antiguas).</summary>
    private static bool EsCasillaDe(string clave, string ruta)
        => string.Equals(clave, ruta, StringComparison.OrdinalIgnoreCase)
           || clave.StartsWith(EnrichViewModel.ClaveMarca(ruta, ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Devuelve a la copia antigua, que ha vuelto de la papelera, lo decidido sobre ella. Lo que se
    /// haya decidido de nuevo mientras tanto no se pisa. Devuelve si ha cambiado algo.
    /// </summary>
    public static bool Devolver(SustitucionHecha s, IgnoreList aplicadas, IgnoreList descartadas, ApplyMarks marcas,
                                IgnoreList audioAceptadas)
    {
        var cambia = false;
        foreach (var (si, lista) in new[] { (s.Aplicada, aplicadas), (s.Descartada, descartadas), (s.AudioAceptada, audioAceptadas) })
            if (si && !lista.Contains(s.Vieja)) { lista.Add(s.Vieja); cambia = true; }
        foreach (var (clave, valor) in s.Casillas)
            if (marcas.Get(clave) == null) { marcas.Set(clave, valor); cambia = true; }
        return cambia;
    }

    /// <summary>
    /// Asienta lo que ha encontrado una pasada de <see cref="AlmacenSustituciones.Conciliar"/> (las
    /// fichas ya están colocadas). De cada sustitución deshecha, las cachés de audio de la descarga
    /// vuelven con ella a la bandeja (<paramref name="seguir"/>: huella, identificación, sonoridad y
    /// aviso aceptado) y se olvida lo decidido sobre ella en la ruta de la biblioteca, que deja de ser
    /// suya. A cada copia antigua que ha vuelto se le devuelve lo suyo. Devuelve si han cambiado las
    /// listas de lo decidido (y hay que guardarlas).
    /// </summary>
    public static bool Asentar(ConciliacionSustituciones c, IgnoreList aplicadas, IgnoreList descartadas, ApplyMarks marcas,
                               IgnoreList audioAceptadas, Action<string, string> seguir)
    {
        var cambia = false;
        foreach (var s in c.Deshechas)
        {
            var antes = audioAceptadas.Contains(s.Destino);
            seguir(s.Destino, s.Bandeja);
            if (antes != audioAceptadas.Contains(s.Destino)) cambia = true;
            if (BandejaViewModel.OlvidarLoDecididoSobre(new[] { s.Destino }, aplicadas, descartadas, marcas, audioAceptadas))
                cambia = true;
        }
        foreach (var s in c.Recuperadas)
            if (Devolver(s, aplicadas, descartadas, marcas, audioAceptadas)) cambia = true;
        return cambia;
    }

    /// <summary>Lo que se apunta en el registro de cada sustitución deshecha o recuperada.</summary>
    public static IEnumerable<string> TextosRegistro(ConciliacionSustituciones c)
    {
        foreach (var s in c.Deshechas)
            yield return $"Bandeja: se ha deshecho la sustitución de «{Path.GetFileName(s.Vieja)}». La descarga vuelve a la bandeja "
                       + "con su ficha de DJ; la de la copia antigua se guarda hasta que la recuperes de la papelera.";
        foreach (var s in c.Recuperadas)
            yield return $"Bandeja: «{Path.GetFileName(s.Vieja)}» ha vuelto de la papelera y recupera su ficha de DJ y lo decidido en Enriquecer.";
        if (c.Olvidadas > 0)
            yield return c.Olvidadas == 1
                ? "Bandeja: se deja de esperar a la copia antigua de 1 sustitución deshecha (caducada, o su ruta la ocupa otro archivo)."
                : $"Bandeja: se deja de esperar a la copia antigua de {c.Olvidadas} sustituciones deshechas (caducadas, o su ruta la ocupa otro archivo).";
    }

    /// <summary>El tamaño de un archivo, o null si no existe o no se puede mirar.</summary>
    public static long? Tamano(string ruta)
    {
        try
        {
            var fi = new FileInfo(ruta);
            return fi.Exists ? fi.Length : null;
        }
        catch { return null; }
    }
}
