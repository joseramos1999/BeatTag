using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Etiquetador.Core;

namespace Etiquetador.App.ViewModels;

// ---------------------------------------------------------------------------------------------
// Volumen: qué se ajusta y qué no (equipo «volumen_copias», tanda 4, E15)
// ---------------------------------------------------------------------------------------------
//
// POR QUÉ. «Ajustar volumen» tomaba TODAS las grabaciones MP3 desviadas más de 2 dB y el diálogo
// solo dejaba aceptar o cancelar el lote entero. Pero hay grabaciones que un DJ quiere más bajas a
// propósito: acapellas, intros, ediciones para entrar por debajo de la canción anterior. Con «Nivel
// medio de la biblioteca» se subían junto al resto, y la única forma de evitarlo era desmarcar su
// carpeta en Biblioteca, que la sacaba de todas las pestañas.
//
// Ahora cada fila lleva su casilla «Ajustar» (marcada por defecto en las que lo necesitan), se puede
// ajustar solo lo seleccionado, y carpetas o canciones concretas se excluyen del ajuste de forma
// permanente (en la configuración, como las carpetas excluidas de Duplicados). BeatTag NO decide por
// su cuenta qué es una acapella o una intro: lo decide el DJ.

/// <summary>Por qué una grabación no entra en el ajuste aunque esté desviada.</summary>
public enum ExclusionVolumen
{
    Ninguna,
    /// <summary>Está en una carpeta excluida, o en una subcarpeta suya.</summary>
    Carpeta,
    /// <summary>Se excluyó esa canción en concreto.</summary>
    Cancion,
}

/// <summary>Una exclusión guardada, para la lista visible de la pestaña (con su botón «Quitar»).</summary>
public sealed partial class ExclusionVolumenFila : ObservableObject
{
    public string Ruta { get; init; } = "";
    public bool EsCarpeta { get; init; }

    /// <summary>La canción ya no está en la biblioteca (BeatTag la renombró, se movió o se borró).</summary>
    public bool FueraDeLaBiblioteca { get; init; }

    /// <summary>Lo que se lee en la lista: la carpeta entera, o el nombre de la canción.</summary>
    public string Texto => EsCarpeta
        ? "📁 " + Ruta
        : "🎵 " + Path.GetFileName(Ruta) + (FueraDeLaBiblioteca ? "  (ya no está en la biblioteca)" : "");
}

/// <summary>Lo que se ajustaría con lo marcado o con lo seleccionado, y lo que queda fuera.</summary>
/// <param name="Ajustar">Las que se van a ajustar.</param>
/// <param name="Desmarcadas">Requieren ajuste pero tienen la casilla quitada (solo en el ajuste de lo marcado).</param>
/// <param name="Excluidas">Requieren ajuste pero están excluidas (por carpeta o por canción).</param>
/// <param name="SinNecesidad">Seleccionadas que no requieren ajuste: se desvían poco, no son MP3 o no tienen margen.</param>
public sealed record SeleccionAjuste(IReadOnlyList<LoudnessRow> Ajustar, int Desmarcadas, int Excluidas, int SinNecesidad);

/// <summary>Reglas y textos del ajuste selectivo de Volumen, aparte para poder probarlos sin la interfaz.</summary>
public static class AjusteVolumenSeleccion
{
    /// <summary>
    /// Si la grabación está excluida, y por qué. Una carpeta excluida cubre también sus subcarpetas:
    /// excluir «Acapellas» tiene que valer para «Acapellas\2025».
    /// </summary>
    public static ExclusionVolumen De(string ruta, IReadOnlyCollection<string> carpetas, IReadOnlySet<string> canciones)
    {
        if (canciones.Contains(ruta)) return ExclusionVolumen.Cancion;
        if (carpetas.Count == 0) return ExclusionVolumen.Ninguna;
        var dir = Path.GetDirectoryName(ruta) ?? "";
        foreach (var c in carpetas)
            if (Rutas.MismaCarpeta(dir, c) || Rutas.EstaDentroDe(dir, c)) return ExclusionVolumen.Carpeta;
        return ExclusionVolumen.Ninguna;
    }

    /// <summary>
    /// Cómo empieza la casilla de una fila: marcada si requiere ajuste y no está excluida, salvo que
    /// el usuario la haya cambiado a mano en esta sesión (eso se respeta al cambiar el nivel de
    /// referencia, que rehace la tabla). Lo que no se puede ajustar nunca sale marcado.
    /// </summary>
    public static bool MarcaInicial(bool puedeMarcarse, bool? aMano) => puedeMarcarse && (aMano ?? true);

    /// <summary>
    /// Añade carpetas a la lista de excluidas sin repetir: una que ya está cubierta por otra excluida
    /// no se añade, y las que quedan cubiertas por una nueva se quitan (la lista que se ve no crece con
    /// entradas que no cambian nada). Devuelve la lista nueva y cuántas carpetas se han añadido.
    /// </summary>
    public static (List<string> Lista, int Anadidas) AnadirCarpetas(IEnumerable<string> actuales, IEnumerable<string> nuevas)
    {
        var (lista, anadidas) = AnadirCarpetasConNombre(actuales, nuevas);
        return (lista, anadidas.Count);
    }

    /// <summary>
    /// Lo mismo, con las carpetas que de verdad han entrado en la lista: las de la lista final que no
    /// estaban en la inicial. Contar cada carpeta pedida contaba de más (House\2024 y luego House, que
    /// la sustituye, eran «2 carpetas» con una sola en la lista) y el aviso podía nombrar una que ya
    /// estaba excluida.
    /// </summary>
    public static (List<string> Lista, List<string> Anadidas) AnadirCarpetasConNombre(IEnumerable<string> actuales, IEnumerable<string> nuevas)
    {
        var inicial = actuales.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var lista = inicial.ToList();
        foreach (var n in nuevas.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (lista.Any(c => Rutas.MismaCarpeta(n, c) || Rutas.EstaDentroDe(n, c))) continue;
            lista.RemoveAll(c => Rutas.EstaDentroDe(c, n));
            lista.Add(n);
        }
        return (lista, lista.Where(c => !inicial.Any(i => Rutas.MismaCarpeta(i, c))).ToList());
    }

    /// <summary>Las carpetas de unas filas, sin repetir: lo que excluye «Excluir esta carpeta del ajuste».</summary>
    public static List<string> CarpetasDe(IEnumerable<LoudnessRow> filas)
        => filas.Select(f => Path.GetDirectoryName(f.FilePath) ?? "")
                .Where(d => d.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    /// <summary>
    /// El rótulo de la opción según la selección: con canciones de varias carpetas excluye todas, y
    /// «esta carpeta» no lo decía.
    /// </summary>
    public static string RotuloExcluirCarpetas(int carpetas)
        => carpetas <= 1 ? "Excluir esta carpeta del ajuste" : $"Excluir sus {carpetas} carpetas del ajuste";

    /// <summary>Añade canciones sin repetir (sin distinguir mayúsculas). Devuelve la lista y cuántas se han añadido.</summary>
    public static (List<string> Lista, int Anadidas) AnadirCanciones(IEnumerable<string> actuales, IEnumerable<string> nuevas)
    {
        var lista = actuales.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        var vistas = new HashSet<string>(lista, StringComparer.OrdinalIgnoreCase);
        var anadidas = 0;
        foreach (var n in nuevas.Where(c => !string.IsNullOrWhiteSpace(c)))
            if (vistas.Add(n)) { lista.Add(n); anadidas++; }
        return (lista, anadidas);
    }

    /// <summary>
    /// Lo que entra en el ajuste. Con <paramref name="seleccion"/> null, lo marcado en la columna
    /// «Ajustar»; con selección, las seleccionadas que requieren ajuste, marcadas o no (seleccionar es
    /// elegir), pero nunca las excluidas: una selección amplia (Ctrl+A, un grupo entero) no puede
    /// saltarse lo que el DJ ha protegido a propósito.
    /// </summary>
    public static SeleccionAjuste Elegir(IEnumerable<LoudnessRow> filas, IEnumerable<LoudnessRow>? seleccion = null)
    {
        var ajustar = new List<LoudnessRow>();
        int desmarcadas = 0, excluidas = 0, sinNecesidad = 0;
        foreach (var r in seleccion ?? filas)
        {
            if (!r.Candidata) { if (seleccion != null) sinNecesidad++; continue; }
            if (r.Exclusion != ExclusionVolumen.Ninguna) { excluidas++; continue; }
            if (seleccion == null && !r.Ajustar) { desmarcadas++; continue; }
            ajustar.Add(r);
        }
        return new SeleccionAjuste(ajustar, desmarcadas, excluidas, sinNecesidad);
    }

    private static string N(int n, string singular, string plural) => n == 1 ? $"1 {singular}" : $"{n} {plural}";

    /// <summary>«Exclusiones del ajuste: 2 carpetas y 1 canción».</summary>
    public static string TituloExclusiones(int carpetas, int canciones)
    {
        var partes = new List<string>();
        if (carpetas > 0) partes.Add(N(carpetas, "carpeta", "carpetas"));
        if (canciones > 0) partes.Add(N(canciones, "canción", "canciones"));
        return "Exclusiones del ajuste: " + (partes.Count == 0 ? "ninguna" : string.Join(" y ", partes));
    }

    /// <summary>
    /// El resumen de la tabla: cuántas están en nivel, por debajo y por encima, y cuántas se desvían
    /// más del umbral de ajuste. Con el umbral igual a la banda «Correcto», las que requieren ajuste
    /// son las que están por debajo o por encima.
    /// </summary>
    public static string TextoResumen(int total, int correctas, int bajas, int altas, int requierenAjuste)
    {
        if (total == 0) return "";
        if (requierenAjuste == 0)
            return total == 1 ? "El nivel de la grabación analizada es correcto." : $"El nivel es uniforme en las {total} grabaciones analizadas.";
        return $"{correctas} con el nivel correcto · {bajas} por debajo · {altas} por encima. "
             + (requierenAjuste == 1 ? "1 requiere ajuste." : $"{requierenAjuste} requieren ajuste.");
    }

    /// <summary>Lo que se ve mientras se mide el pico real de las que se aumentarían.</summary>
    public static string TextoComprobandoPicoReal(int n)
        => n == 1
            ? "Comprobando el pico real de 1 grabación que se aumentaría…"
            : $"Comprobando el pico real de {n} grabaciones que se aumentarían…";

    /// <summary>
    /// Tras cambiar el nivel de referencia o las exclusiones, cuántas se aumentarían sin el pico real
    /// medido. No se miden en ese momento (decodificar cientos de canciones por elegir otra opción
    /// bloqueaba la pestaña sin haberlo pedido): se miden al pulsar Analizar y, como última
    /// comprobación, al ajustar. "" si no hay ninguna.
    /// </summary>
    public static string TextoPicoRealPendiente(int n)
        => n switch
        {
            0 => "",
            1 => "1 grabación que se aumentaría aún no tiene el pico real medido: pulsa Analizar para medirlo antes de ajustar.",
            _ => $"{n} grabaciones que se aumentarían aún no tienen el pico real medido: pulsa Analizar para medirlo antes de ajustar.",
        };

    /// <summary>
    /// Las que iban a aumentarse y, medido el pico real al ajustar, no admitían ni un paso: se dejan
    /// como estaban. "" si no hay ninguna.
    /// </summary>
    public static string TextoSinMargen(int n)
        => n switch
        {
            0 => "",
            1 => " · 1 sin cambios: su pico real no deja margen para aumentarla",
            _ => $" · {n} sin cambios: su pico real no deja margen para aumentarlas",
        };

    /// <summary>Al terminar de analizar, si hubo que volver a medir grabaciones mono.</summary>
    public static string TextoMonoMedidas(int n)
        => n == 1
            ? "1 grabación mono se ha vuelto a medir tal como se escucha, por los dos altavoces."
            : $"{n} grabaciones mono se han vuelto a medir tal como se escuchan, por los dos altavoces.";

    /// <summary>«143 grabaciones marcadas para ajustar».</summary>
    public static string TextoMarcadas(int marcadas)
        => marcadas == 0 ? "Ninguna grabación marcada para ajustar"
         : marcadas == 1 ? "1 grabación marcada para ajustar"
         : $"{marcadas} grabaciones marcadas para ajustar";

    /// <summary>
    /// Por qué no hay nada que ajustar, para el estado. Distingue lo que pasa de verdad: no es lo
    /// mismo que todo esté a nivel que haberlo desmarcado o excluido todo.
    /// </summary>
    public static string TextoNadaQueAjustar(SeleccionAjuste s, bool porSeleccion, int desviadasNoMp3)
    {
        if (porSeleccion)
        {
            if (s.Excluidas > 0 && s.SinNecesidad == 0)
                return s.Excluidas == 1 ? "La seleccionada está excluida del ajuste." : "Las seleccionadas están excluidas del ajuste.";
            if (s.Excluidas > 0)
                return $"Ninguna de las seleccionadas se puede ajustar: {N(s.Excluidas, "está excluida", "están excluidas")} del ajuste y el resto no lo requiere.";
            return "Ninguna de las seleccionadas requiere ajuste: se desvían "
                 + $"{LoudnessViewModel.UmbralAjuste:0.#} dB o menos, no son MP3 o no tienen margen para subir sin distorsionar.";
        }
        if (s.Desmarcadas > 0 || s.Excluidas > 0)
        {
            var partes = new List<string>();
            if (s.Desmarcadas > 0) partes.Add(N(s.Desmarcadas, "tiene la casilla «Ajustar» quitada", "tienen la casilla «Ajustar» quitada"));
            if (s.Excluidas > 0) partes.Add(N(s.Excluidas, "está excluida del ajuste", "están excluidas del ajuste"));
            return "No hay ninguna grabación marcada para ajustar. De las que lo requieren, " + string.Join(" y ", partes) + ".";
        }
        return desviadasNoMp3 > 0
            ? $"No hay ningún MP3 que ajustar. {N(desviadasNoMp3, "grabación está desviada", "grabaciones están desviadas")}, pero no "
              + (desviadasNoMp3 == 1 ? "es MP3: se mide, no se ajusta." : "son MP3: se miden, no se ajustan.")
            : "No hay ninguna grabación que requiera ajuste.";
    }

    /// <summary>
    /// Textos de la confirmación. Cuenta SOLO lo que se va a tocar, y dice qué queda fuera y por qué.
    /// </summary>
    public static (string Titulo, string Cuerpo, string Nota) TextosConfirmacion(SeleccionAjuste s, bool porSeleccion, int parciales)
    {
        var total = s.Ajustar.Count;
        var suben = s.Ajustar.Count(r => r.Gain > 0);
        var bajan = total - suben;
        var titulo = total == 1
            ? "Se va a ajustar el volumen de 1 grabación"
            : $"Se va a ajustar el volumen de {total} grabaciones";

        var sentido = (suben, bajan) switch
        {
            ( > 0, > 0) => $"Se aumentará el nivel de {suben} y se reducirá el de {bajan}.",
            ( > 0, _) => total == 1 ? "Se aumentará su nivel." : $"Se aumentará el nivel de las {suben}.",
            _ => total == 1 ? "Se reducirá su nivel." : $"Se reducirá el nivel de las {bajan}.",
        };
        var criterio = porSeleccion
            ? $" Se incluyen solo las seleccionadas que se desvían más de {LoudnessViewModel.UmbralAjuste:0.#} dB del nivel de referencia."
            : $" Se incluyen solo las marcadas en la columna «Ajustar», que se desvían más de {LoudnessViewModel.UmbralAjuste:0.#} dB del nivel de referencia.";
        var parcial = parciales switch
        {
            0 => "",
            1 => " En 1 el aumento será parcial, porque no admite más sin distorsionar.",
            _ => $" En {parciales} el aumento será parcial, porque no admiten más sin distorsionar.",
        };

        var fuera = new List<string>();
        if (s.Desmarcadas > 0) fuera.Add(N(s.Desmarcadas, "desmarcada", "desmarcadas"));
        if (s.Excluidas > 0) fuera.Add(N(s.Excluidas, "excluida del ajuste", "excluidas del ajuste"));
        if (s.SinNecesidad > 0) fuera.Add(N(s.SinNecesidad, "seleccionada que no lo requiere", "seleccionadas que no lo requieren"));
        var quedan = fuera.Count == 0 ? ""
            : (s.Desmarcadas + s.Excluidas + s.SinNecesidad == 1 ? " Queda fuera " : " Quedan fuera ")
              + string.Join(", ", fuera.Take(fuera.Count - 1)) + (fuera.Count > 1 ? " y " : "") + fuera[^1] + ".";

        return (titulo, sentido + criterio + parcial + quedan,
                "El audio no se recodifica, por lo que no hay pérdida de calidad y los archivos mantienen su tamaño. "
              + "El cambio queda registrado y se puede revertir desde Enriquecer con «Deshacer última» o, más adelante, con «Historial de cambios…».");
    }

    /// <summary>
    /// Lo que se dice al excluir carpetas. <paramref name="carpetas"/> son las que han entrado en la
    /// lista (<see cref="AnadirCarpetasConNombre"/>), o las pedidas si no ha entrado ninguna.
    /// </summary>
    public static string TextoCarpetasExcluidas(IReadOnlyList<string> carpetas, int anadidas, int canciones)
    {
        if (anadidas == 0)
            return carpetas.Count == 1 ? "Esa carpeta ya está excluida del ajuste." : "Esas carpetas ya están excluidas del ajuste.";
        var que = anadidas == 1
            ? $"Excluida del ajuste la carpeta «{Nombre(carpetas[0])}»"
            : $"Excluidas del ajuste {anadidas} carpetas";
        var cuantas = canciones switch
        {
            0 => "ahora mismo ninguna de sus grabaciones requería ajuste",
            1 => "1 grabación que lo requería deja de ajustarse",
            _ => $"{canciones} grabaciones que lo requerían dejan de ajustarse",
        };
        var quitar = anadidas == 1
            ? "la exclusión se puede quitar en «Exclusiones del ajuste»"
            : "las exclusiones se pueden quitar en «Exclusiones del ajuste»";
        return $"{que}: {cuantas}. Se siguen midiendo; {quitar}.";
    }

    /// <summary>Lo que se dice al excluir canciones.</summary>
    public static string TextoCancionesExcluidas(int pedidas, int anadidas)
    {
        if (anadidas == 0)
            return pedidas == 1 ? "Esa canción ya está excluida del ajuste." : "Esas canciones ya están excluidas del ajuste.";
        return anadidas == 1
            ? "Excluida del ajuste 1 canción. Se sigue midiendo; la exclusión se puede quitar en «Exclusiones del ajuste»."
            : $"Excluidas del ajuste {anadidas} canciones. Se siguen midiendo; las exclusiones se pueden quitar en «Exclusiones del ajuste».";
    }

    private static string Nombre(string carpeta)
    {
        var limpia = carpeta.TrimEnd('\\', '/');
        var n = Path.GetFileName(limpia);
        return n.Length > 0 ? n : limpia;
    }
}
