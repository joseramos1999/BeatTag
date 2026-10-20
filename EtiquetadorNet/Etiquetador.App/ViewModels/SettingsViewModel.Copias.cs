using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core.Copias;

namespace Etiquetador.App.ViewModels;

// =================================================================================================
// Ajustes → Copia de seguridad (equipo «volumen_copias», tanda 4, T15)
// =================================================================================================
//
// La lógica está en Core (Copias/CopiaSeguridad, MapaRaices, RutasJson); aquí solo se lleva al hilo
// de fondo lo que toca disco y se dejan los textos para la vista. El diálogo de restaurar
// (RestaurarCopiaDialog) recibe una LecturaCopia y devuelve las ubicaciones que confirma el usuario.

/// <summary>Una copia leída y lista para enseñarla: vista previa de archivos y raíces de la música.</summary>
public sealed record LecturaCopia(ContenidoCopia Contenido, IReadOnlyList<FilaRestauracion> Filas,
                                  IReadOnlyList<RaizCopia> Raices, IReadOnlyList<string> Rutas);

public partial class SettingsViewModel
{
    /// <summary>«Copias automáticas: 5 guardadas; la última, del…».</summary>
    [ObservableProperty] private string _textoCopiasAutomaticas = "";

    /// <summary>El aviso de la restauración preparada que espera al próximo arranque, o "".</summary>
    [ObservableProperty] private string _restauracionPendiente = "";

    /// <summary>Se está haciendo, leyendo o preparando una copia: los botones esperan.</summary>
    [ObservableProperty] private bool _copiaOcupada;

    /// <summary>La carpeta de las copias automáticas y de las de antes de restaurar.</summary>
    public string CarpetaCopias => CopiaSeguridad.CarpetaCopias(_engine.Paths);

    /// <summary>Nombre que se propone al guardar una copia.</summary>
    public static string NombreSugeridoCopia(DateTime ahora) => $"BeatTag-copia_{ahora:yyyyMMdd_HHmm}";

    /// <summary>Recuenta las copias automáticas y mira si hay una restauración preparada (al abrir Ajustes).</summary>
    public async Task ActualizarCopiasAsync()
    {
        var paths = _engine.Paths;
        try
        {
            var (fechas, pendiente) = await Task.Run(() =>
            {
                var zips = CopiaSeguridad.Copias(CopiaSeguridad.CarpetaCopias(paths), CopiaSeguridad.PrefijoAutomatica);
                var f = zips.Select(z => { try { return File.GetLastWriteTime(z); } catch { return default; } })
                            .Where(d => d != default).OrderBy(d => d).ToList();
                return (f, CopiaSeguridad.Pendiente(paths));
            });
            TextoCopiasAutomaticas = TextosCopia.CopiasAutomaticas(fechas);
            RestauracionPendiente = pendiente == null ? "" : TextosCopia.Pendiente(pendiente);
        }
        catch (Exception e) { _engine.Logger.Error("No se pudieron contar las copias de seguridad", e); }
    }

    /// <summary>
    /// «Copia de seguridad…»: guarda el ZIP en <paramref name="destino"/>. Antes pone en disco lo que
    /// entra en la copia y solo vive en memoria (casillas «Aplicar», aplicadas, descartadas, avisos
    /// aceptados e identificaciones): la copia tiene que llevar lo que el usuario ve ahora.
    /// </summary>
    public async Task HacerCopiaAsync(string destino)
    {
        if (CopiaOcupada) return;
        CopiaOcupada = true;
        Status = "Guardando la copia de seguridad…";
        try
        {
            // Las listas se tocan desde el hilo de la interfaz y pesan KB: se guardan aquí mismo.
            var fallos = new List<string>();
            foreach (var (que, guardar) in new (string, Func<string>)[]
                     {
                         ("las casillas «Aplicar»", _engine.Marks.Save), ("las canciones aplicadas", _engine.Applied.Save),
                         ("las canciones descartadas", _engine.Ignored.Save), ("los avisos aceptados", _engine.AudioAceptadas.Save),
                     })
            {
                var error = guardar();
                if (error.Length > 0) fallos.Add($"{que} ({error})");
            }
            var paths = _engine.Paths;
            var r = await Task.Run(() =>
            {
                _engine.Identificacion.Save();   // la guarda también el temporizador, desde otro hilo
                return CopiaSeguridad.Crear(paths, destino, "manual", DateTime.Now);
            });
            Status = TextosCopia.CopiaHecha(r)
                   + (fallos.Count > 0 ? " No se pudieron poner al día antes de copiar: " + string.Join(", ", fallos) + "." : "");
            if (r.Ok) _engine.Logger.Sum($"Copia de seguridad: {destino} · {r.Archivos} archivos · {TextosCopia.Tamano(r.Bytes)}");
            else _engine.Logger.Err("Copia de seguridad: " + r.Error);
        }
        catch (Exception e) { Status = "No se pudo guardar la copia de seguridad: " + e.Message; }
        finally { CopiaOcupada = false; }
    }

    /// <summary>
    /// Lee una copia para «Restaurar copia…»: qué se sustituye y dónde parece estar ahora la música de
    /// cada raíz. Todo en segundo plano (mira discos). Devuelve null y deja el motivo en el estado si
    /// no es una copia que se pueda usar.
    /// </summary>
    public async Task<LecturaCopia?> LeerCopiaAsync(string zip)
    {
        if (CopiaOcupada) return null;
        CopiaOcupada = true;
        Status = "Leyendo la copia de seguridad…";
        var paths = _engine.Paths;
        var cfg = _engine.Config;
        // Las carpetas de la biblioteca de ESTE equipo: ayudan a encontrar dónde está ahora la música.
        var actuales = cfg.Folders.Concat(cfg.DisabledFolders)
                          .Append(cfg.BandejaCarpeta).Append(cfg.BandejaDestino)
                          .Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        try
        {
            var (lectura, error) = await Task.Run(() =>
            {
                var c = CopiaSeguridad.Leer(zip, paths, out var err);
                if (c == null) return ((LecturaCopia?)null, err);
                var filas = CopiaSeguridad.Vista(c, paths);
                var rutas = CopiaSeguridad.Rutas(c).Values.SelectMany(x => x).ToList();
                var raices = CopiaSeguridad.ProponerRaices(CopiaSeguridad.CarpetasDeLaCopia(c, paths), rutas,
                                                           Directory.Exists, actuales, CopiaSeguridad.VolumenesDelEquipo());
                return (new LecturaCopia(c, filas, raices, rutas), "");
            });
            Status = lectura == null ? error : "";
            return lectura;
        }
        catch (Exception e) { Status = "No se pudo leer la copia de seguridad: " + e.Message; return null; }
        finally { CopiaOcupada = false; }
    }

    /// <summary>Cuántas rutas de la copia cambiarían con estas ubicaciones (para la confirmación).</summary>
    public static Task<int> ContarRutasQueCambianAsync(LecturaCopia l, MapaRaices mapa)
        => mapa.Vacio ? Task.FromResult(0) : Task.Run(() => l.Rutas.Count(r => mapa.Traducir(r) != null));

    /// <summary>
    /// Deja la restauración preparada para el próximo arranque, con las rutas ya en su sitio. Al
    /// reescribir se mira en disco la forma de los acentos de cada archivo (NFC o NFD) y su fecha, por
    /// si el disco nuevo la redondea (ver RutasJson.Resellar).
    /// </summary>
    public async Task PrepararRestauracionAsync(LecturaCopia l, IReadOnlyList<CambioRaiz> cambios)
    {
        if (CopiaOcupada) return;
        CopiaOcupada = true;
        Status = "Preparando la restauración…";
        var paths = _engine.Paths;
        try
        {
            var mapa = new MapaRaices(cambios);
            var r = await Task.Run(() => CopiaSeguridad.PrepararRestauracion(
                l.Contenido, paths, mapa, existe: p => File.Exists(p) || Directory.Exists(p), DateTime.Now, sello: SelloDe));
            Status = TextosCopia.Preparada(r);
            if (r.Ok)
                _engine.Logger.Sum($"Restauración preparada: {Path.GetFileName(l.Contenido.Origen)} · {r.Archivos} archivos · "
                                 + $"{r.RutasReescritas} rutas reescritas · {r.Resellados} fechas puestas al día · "
                                 + string.Join(" · ", cambios.Where(c => c.Destino.Length > 0).Select(c => $"{c.Origen} → {c.Destino}")));
            else _engine.Logger.Err(r.Error);
        }
        catch (Exception e) { Status = "No se pudo preparar la restauración: " + e.Message; }
        finally { CopiaOcupada = false; }
        await ActualizarCopiasAsync();
    }

    /// <summary>Fecha (ticks UTC) y tamaño de un archivo de música en este equipo, o null si no está.</summary>
    private static (long Ticks, long Bytes)? SelloDe(string ruta)
    {
        try
        {
            var fi = new FileInfo(ruta);
            return fi.Exists ? (fi.LastWriteTimeUtc.Ticks, fi.Length) : null;
        }
        catch { return null; }
    }

    /// <summary>«Cancelar la restauración»: descarta la que estaba preparada. No toca los datos actuales.</summary>
    /// <remarks>
    /// No mientras se prepara otra restauración: preparar borra y vuelve a escribir la misma carpeta,
    /// y cancelar a la vez la borraba a medias (la preparación fallaba, o el estado decía «preparada»
    /// sin que existiera). El botón sale desactivado con <see cref="CopiaOcupada"/>, y la cancelación
    /// la ocupa mientras dura.
    /// </remarks>
    [RelayCommand]
    private async Task CancelarRestauracionAsync()
    {
        if (CopiaOcupada) return;
        CopiaOcupada = true;
        try
        {
            var habia = await Task.Run(() => CopiaSeguridad.CancelarPendiente(_engine.Paths));
            Status = habia ? "Restauración cancelada: los datos actuales se quedan como están." : "No había ninguna restauración preparada.";
            if (habia) _engine.Logger.Log("Restauración de copia de seguridad cancelada.");
        }
        catch (Exception e) { Status = "No se pudo cancelar la restauración: " + e.Message; }
        finally { CopiaOcupada = false; }
        await ActualizarCopiasAsync();
    }

    /// <summary>Abre la carpeta de las copias automáticas.</summary>
    [RelayCommand]
    private async Task AbrirCarpetaCopiasAsync()
    {
        try { Directory.CreateDirectory(CarpetaCopias); } catch { }
        await Shell.OpenFolderAsync(CarpetaCopias);
        Status = "Carpeta de copias: " + CarpetaCopias;
    }
}
