using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Pipeline;
using Etiquetador.App.Views;

namespace Etiquetador.App.ViewModels;

/// <summary>Pestaña Editor: elige una canción de la biblioteca y edita a mano su título y tags.</summary>
public partial class EditorViewModel : ViewModelBase, IEstadoPagina
{
    private readonly AppEngine _engine;
    private LibraryStore Store => _engine.Library;

    public DataGridCollectionView TracksView { get; }

    [ObservableProperty] private Track? _selectedTrack;
    [ObservableProperty] private string _search = "";

    /// <summary>Nombre del archivo, editable: al guardar se renombra si ha cambiado.</summary>
    [ObservableProperty] private string _fileName = "";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _album = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _year = "";
    [ObservableProperty] private string _bpm = "";
    [ObservableProperty] private string _comment = "";

    [ObservableProperty] private bool _canEdit;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Selecciona una canción de la lista para editar sus tags.";

    /// <summary>
    /// Lo que se leyó del archivo al abrir la canción. Guardar compara el formulario con esto para
    /// saber qué campos ha TOCADO el usuario, y solo escribe esos.
    /// </summary>
    private TagValues _cargado = new("", "", "", "", 0, 0, "");

    public EditorViewModel(AppEngine engine)
    {
        _engine = engine;
        TracksView = new DataGridCollectionView(Store.Tracks);
        TracksView.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(Track.Folder)));
        Store.Changed += () => TracksView.Refresh();
        _filtroDiferido = new BusquedaDiferida(AplicarFiltro);
    }

    private readonly BusquedaDiferida _filtroDiferido;

    // La lista es la biblioteca entera (15.000-20.000 canciones agrupadas por carpeta): se filtra al
    // dejar de teclear, no en cada pulsación.
    partial void OnSearchChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) _filtroDiferido.Ahora();
        else _filtroDiferido.Pedir();
    }

    private void AplicarFiltro()
    {
        var q = Search?.Trim() ?? "";
        // Asignar el filtro ya rehace la vista; el Refresh() que venía detrás la recorría otra vez.
        TracksView.Filter = q.Length == 0
            ? null
            : o => o is Track t &&
                   ((t.FileName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (t.Artist?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>Selecciona en la lista el track con esa ruta (para "editar esta canción" desde otra pestaña).</summary>
    public void SelectByPath(string filePath)
    {
        var t = System.Linq.Enumerable.FirstOrDefault(Store.Tracks,
            x => string.Equals(x.FilePath, filePath, System.StringComparison.OrdinalIgnoreCase));
        if (t != null) SelectedTrack = t;
    }

    partial void OnSelectedTrackChanged(Track? value)
    {
        if (value == null) { CanEdit = false; Status = "Selecciona una canción de la lista."; return; }
        LoadFrom(value);
    }

    private void LoadFrom(Track t)
    {
        var v = TagEditor.Read(t.FilePath);   // lee siempre lo real del archivo
        _cargado = v;
        FileName = Path.GetFileName(t.FilePath);
        Title = v.Title; Artist = v.Artist; Album = v.Album; Genre = v.Genre;
        Year = v.Year == 0 ? "" : v.Year.ToString();
        Bpm = v.Bpm == 0 ? "" : v.Bpm.ToString();
        Comment = v.Comment;
        CanEdit = true;
        Status = "Editando: " + t.FileName;
    }

    /// <summary>Lo que hay ahora mismo en el formulario.</summary>
    private FormularioEditor Formulario()
        => new(FileName ?? "", Title ?? "", Artist ?? "", Album ?? "", Genre ?? "", Year ?? "", Bpm ?? "", Comment ?? "");

    private void Volcar(FormularioEditor f)
    {
        FileName = f.FileName; Title = f.Title; Artist = f.Artist; Album = f.Album;
        Genre = f.Genre; Year = f.Year; Bpm = f.Bpm; Comment = f.Comment;
    }

    [RelayCommand]
    private void Reload() { if (SelectedTrack != null) LoadFrom(SelectedTrack); }

    [RelayCommand]
    private void PlayPreview()
    {
        if (SelectedTrack == null) return;
        try { _engine.Preview.Toggle(SelectedTrack.FilePath); Status = "Reproduciendo (≈25% del tema)…"; }
        catch (Exception e) { Status = "No se pudo reproducir (¿formato sin soporte?): " + e.Message; }
    }

    [RelayCommand]
    private void StopPreview() => _engine.Preview.Stop();

    [RelayCommand]
    private async Task SaveAsync()
    {
        var t = SelectedTrack;
        if (t == null || IsBusy) return;

        // Todo lo que se guarda se toma AHORA, antes de esperar a nada. Antes la escritura leía las
        // propiedades del formulario desde otro hilo y, al volver, las copiaba a la canción y
        // renombraba con el nombre del formulario: si en ese rato se pinchaba otra canción, la
        // primera se quedaba con los datos y el nombre de la segunda.
        var form = Formulario();
        var cargado = _cargado;
        if (!EdicionManual.LeerAnio(form.Year, out var anio, out var errAnio)) { Status = errAnio; return; }
        if (!EdicionManual.LeerBpm(form.Bpm, out var bpm, out var avisoBpm, out var errBpm)) { Status = errBpm; return; }
        var manifiesto = _engine.Paths.NuevoManifiesto();

        IsBusy = true;
        try
        {
            _engine.ReleaseAudio();   // el reproductor mantiene el archivo abierto y la escritura fallaría
            var origPath = t.FilePath;
            var sello = SelloArchivo.De(origPath);   // para que las cachés de audio sigan al archivo
            var res = await Task.Run(() => EdicionManual.Guardar(t.FilePath, cargado, form, anio, bpm, manifiesto));

            // Refleja los cambios en la biblioteca en memoria (para las demás pestañas): solo lo tocado.
            EdicionManual.Reflejar(t, cargado, form, res, anio, bpm);
            if (res.Renombrado) t.FilePath = res.RutaFinal;   // FileName se deriva de la ruta
            if (ReferenceEquals(SelectedTrack, t)) LoadFrom(t);   // el formulario enseña lo que quedó escrito
            _engine.SeguirArchivo(origPath, t.FilePath, sello);

            TracksView.Refresh();
            Status = EdicionManual.MensajeGuardado(t.FileName, res, avisoBpm);
            if (res.Renombrado) _engine.Logger.Ok($"Editor: renombrado a '{t.FileName}'");
            if (res.ErrorAnotar.Length > 0)
                _engine.Logger.Err($"Editor: '{t.FileName}' se guardó pero NO se pudo anotar para deshacer: {res.ErrorAnotar}");
        }
        catch (Exception e) { Status = "Error al guardar: " + e.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Servicios del diálogo "Reanalizar…" (los mismos que en Enriquecer).</summary>
    public SearchServices SearchServicesFor(string filePath)
        => new(_engine.FindCandidatesAsync, _engine.ResolveLinkAsync,
               () => _engine.IdentifyByFingerprintAsync(filePath));

    /// <summary>
    /// Reanaliza la canción y VUELCA la propuesta en el formulario (no escribe nada): aquí mandas tú,
    /// así que revisas lo propuesto y decides si guardar.
    /// </summary>
    public async Task ReanalyzeIntoFormAsync(Track? t, string searchArtist = "", string searchTitle = "",
        string searchSource = "")
    {
        t ??= SelectedTrack;
        if (t == null) { Status = "Selecciona antes una canción."; return; }
        if (IsBusy) { Status = "Hay un proceso en curso; espera a que termine."; return; }
        IsBusy = true;   // la lista y Guardar quedan bloqueados mientras se busca
        var nombre = Path.GetFileName(t.FilePath);
        Status = $"Buscando «{nombre}»…";
        try
        {
            var opts = _engine.BuildOptions();
            opts.SearchArtist = searchArtist;
            opts.SearchTitle = searchTitle;
            opts.SearchSource = searchSource;
            var r = await Task.Run(() => _engine.Processor.ProcessAsync(t.FilePath, isAcapella: false, opts));

            // La propuesta es de la canción por la que se preguntó. Si para entonces el formulario
            // es de otra, se descarta: antes se volcaba encima (título, artista, año, BPM y el NOMBRE
            // DE ARCHIVO) y Guardar escribía los datos de A en B y la renombraba como A.
            if (EdicionManual.PropuestaDeOtraCancion(t, SelectedTrack))
            {
                Status = $"La búsqueda de «{nombre}» terminó cuando ya estabas en otra canción; se ha descartado y el formulario no se ha tocado.";
                return;
            }

            if (!r.Found && !r.CleanOnly) { Status = $"Sin resultado para «{nombre}». Prueba otra grafía, un enlace o la huella."; return; }

            // Solo se rellena lo que trae la propuesta: no se borra lo que ya tenías.
            Volcar(EdicionManual.ConPropuesta(Formulario(), r));

            // Aquí no se escribe nada hasta Guardar, pero el motivo para revisar (fuente de rescate,
            // nombre con huellas de una regla fallida) tiene que verse antes de hacerlo.
            Status = r.Revisar.Length > 0
                ? $"Propuesta para «{nombre}» de {r.Source} (confianza {r.Score}), pendiente de revisión: {r.Revisar}. Revisa y pulsa Guardar."
                : $"Propuesta para «{nombre}» de {r.Source} (confianza {r.Score}). Revisa y pulsa Guardar.";
        }
        catch (Exception e) { Status = "Error al reanalizar: " + e.Message; }
        finally { IsBusy = false; }
    }
}

/// <summary>Lo que hay en el formulario del Editor, tal cual se ve.</summary>
public sealed record FormularioEditor(string FileName, string Title, string Artist, string Album,
                                      string Genre, string Year, string Bpm, string Comment);

/// <summary>Lo que hizo un guardado del Editor.</summary>
/// <param name="Cambios">Cada campo escrito, con su valor anterior y el que quedó en el archivo.</param>
/// <param name="MensajeRenombrado">"" si no se pidió renombrar; si no, cómo fue.</param>
/// <param name="Anotado">El cambio quedó en el manifiesto y «Deshacer última» lo puede revertir.</param>
public sealed record GuardadoEditor(IReadOnlyDictionary<string, FieldChange> Cambios, bool Renombrado, string RutaFinal,
                                    string MensajeRenombrado, bool Anotado, string ErrorAnotar)
{
    public bool HuboCambios => Cambios.Count > 0 || Renombrado;
}

/// <summary>
/// Guardado del Editor, aparte de la pantalla para poder probarlo con archivos de verdad.
///
/// Escribe SOLO los campos que el usuario ha tocado. Antes se reescribían todos en cada Guardar, y
/// eso tenía dos efectos que nadie había pedido: los artistas se partían por comas («Earth, Wind &amp;
/// Fire» pasaba a ser dos intérpretes, «Earth» y «Wind &amp; Fire», con solo corregir el año) y los
/// géneros se juntaban en un único valor. Y como el manifiesto comparaba el texto ya unido, el
/// deshacer no lo registraba. El comentario tampoco se anotaba, aunque la pantalla prometía que se
/// podía deshacer.
/// </summary>
public static class EdicionManual
{
    /// <summary>
    /// Nombres que llevan coma y son UN solo artista. Se suman a los que ya figuran así en el archivo:
    /// escribir «Tyler, The Creator» a mano no debe dar dos intérpretes.
    /// </summary>
    public static readonly string[] ArtistasConComa =
    {
        "Earth, Wind & Fire", "Tyler, The Creator", "Crosby, Stills & Nash", "Crosby, Stills, Nash & Young",
        "Emerson, Lake & Palmer", "Peter, Paul and Mary", "Blood, Sweat & Tears",
    };

    /// <summary>Año del formulario: vacío es 0 (sin año). false, con el motivo, si no se puede leer.</summary>
    public static bool LeerAnio(string? texto, out uint anio, out string error)
    {
        var t = (texto ?? "").Trim();
        anio = 0; error = "";
        if (t.Length == 0) return true;
        if (uint.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out anio) && anio <= 9999) return true;
        anio = 0;
        error = $"No se ha guardado nada: el año «{t}» no es un número válido. Escribe uno como 2004, o déjalo vacío.";
        return false;
    }

    /// <summary>
    /// BPM del formulario. La etiqueta solo guarda enteros, así que un BPM con decimales («128.5»,
    /// «127,99») se redondea y se avisa. Antes cualquier cosa que no fuera un entero se guardaba
    /// como 0, es decir, BORRABA el BPM sin decir nada.
    /// </summary>
    public static bool LeerBpm(string? texto, out uint bpm, out string aviso, out string error)
    {
        var t = (texto ?? "").Trim();
        bpm = 0; aviso = ""; error = "";
        if (t.Length == 0) return true;
        if (!double.TryParse(t.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v > 999)
        {
            error = $"No se ha guardado nada: el BPM «{t}» no es un número válido. Escribe uno como 128 o 127,5, o déjalo vacío.";
            return false;
        }
        bpm = (uint)Math.Round(v, MidpointRounding.AwayFromZero);
        if (bpm != v) aviso = $"BPM redondeado a {bpm}: la etiqueta solo admite números enteros";
        return true;
    }

    /// <summary>
    /// Separa el campo Artista por comas, salvo los nombres que son uno solo aunque lleven coma: los
    /// que el archivo ya tenía como un intérprete y los de <see cref="ArtistasConComa"/>.
    /// </summary>
    public static string[] SepararArtistas(string? texto, IEnumerable<string> delArchivo)
    {
        if (string.IsNullOrWhiteSpace(texto)) return Array.Empty<string>();
        var piezas = texto.Split(',').Select(p => p.Trim()).ToList();

        // Cada nombre protegido, en trozos; el del archivo se devuelve con su grafía exacta.
        var protegidos = delArchivo.Where(n => n.Contains(',')).Select(n => (Trozos: Trozos(n), Original: (string?)n))
            .Concat(ArtistasConComa.Select(n => (Trozos: Trozos(n), Original: (string?)null)))
            .OrderByDescending(p => p.Trozos.Length)
            .ToList();

        var res = new List<string>();
        for (var i = 0; i < piezas.Count; i++)
        {
            var tomar = 1;
            string? original = null;
            foreach (var (trozos, orig) in protegidos)
            {
                if (i + trozos.Length > piezas.Count) continue;
                if (!trozos.Select((x, k) => string.Equals(x, piezas[i + k], StringComparison.OrdinalIgnoreCase)).All(b => b)) continue;
                tomar = trozos.Length;
                original = orig;
                break;
            }
            var nombre = original ?? string.Join(", ", piezas.GetRange(i, tomar));
            if (nombre.Length > 0) res.Add(nombre);
            i += tomar - 1;
        }
        return res.ToArray();

        static string[] Trozos(string n) => n.Split(',').Select(p => p.Trim()).ToArray();
    }

    /// <summary>
    /// Separa el campo Género. El formulario enseña los géneros unidos por «; » (así los devuelve
    /// TagLib), así que se separan por el mismo signo: editar un archivo con varios géneros y
    /// guardarlo los conserva separados. Una coma no separa: «Dance, Pop» puede ser un único valor.
    /// </summary>
    public static string[] SepararGeneros(string? texto)
        => string.IsNullOrWhiteSpace(texto)
            ? Array.Empty<string>()
            : texto.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Escribe en el archivo los campos que el usuario ha tocado (distintos de <paramref name="cargado"/>)
    /// y que además difieren de lo que tiene ahora el archivo. Devuelve cada cambio con su valor
    /// anterior y el escrito. Las listas (artistas, géneros) se anotan RELEÍDAS del archivo: el
    /// deshacer compara lo que hay en el archivo con lo anotado, y antes se anotaba la lista partida
    /// por comas mientras se escribía una sola cadena, así que nunca coincidían.
    /// </summary>
    public static Dictionary<string, FieldChange> EscribirCambios(string ruta, TagValues cargado, FormularioEditor form,
                                                                  uint anio, uint bpm)
    {
        var cambios = new Dictionary<string, FieldChange>();
        using (var tf = Tagging.AbrirParaEscribir(ruta))   // TagLib no escribe AIFF-C: así el motivo sale en español
        {
            var tag = tf.Tag;
            Texto(cambios, "Title", form.Title, cargado.Title, tag.Title, v => tag.Title = v);
            Texto(cambios, "Album", form.Album, cargado.Album, tag.Album, v => tag.Album = v);
            Texto(cambios, "Comment", form.Comment, cargado.Comment, tag.Comment, v => tag.Comment = v);

            if (anio != cargado.Year && anio != tag.Year) { cambios["Year"] = FieldChange.Num(tag.Year, anio); tag.Year = anio; }
            if (bpm != cargado.Bpm && bpm != tag.BeatsPerMinute) { cambios["Bpm"] = FieldChange.Num(tag.BeatsPerMinute, bpm); tag.BeatsPerMinute = bpm; }

            var artistas = tag.Performers ?? Array.Empty<string>();
            if (form.Artist != cargado.Artist)
            {
                var nuevos = SepararArtistas(form.Artist, artistas);
                if (!nuevos.SequenceEqual(artistas)) { tag.Performers = nuevos; cambios["Artist"] = FieldChange.Arr(artistas, nuevos); }
            }

            var generos = tag.Genres ?? Array.Empty<string>();
            if (form.Genre != cargado.Genre)
            {
                var nuevos = SepararGeneros(form.Genre);
                if (!nuevos.SequenceEqual(generos)) { tag.Genres = nuevos; cambios["Genre"] = FieldChange.Arr(generos, nuevos); }
            }

            if (cambios.Count == 0) return cambios;
            tf.Save();
        }

        if (cambios.ContainsKey("Artist") || cambios.ContainsKey("Genre"))
        {
            using var releido = TagLib.File.Create(ruta);
            if (cambios.TryGetValue("Artist", out var a))
                cambios["Artist"] = FieldChange.Arr(a.OldArr ?? new List<string>(), releido.Tag.Performers ?? Array.Empty<string>());
            if (cambios.TryGetValue("Genre", out var g))
                cambios["Genre"] = FieldChange.Arr(g.OldArr ?? new List<string>(), releido.Tag.Genres ?? Array.Empty<string>());
        }
        return cambios;
    }

    private static void Texto(Dictionary<string, FieldChange> cambios, string campo, string nuevo, string cargado,
                              string? actual, Action<string?> poner)
    {
        if (nuevo == cargado) return;          // no lo has tocado
        var antes = actual ?? "";
        if (nuevo == antes) return;            // el archivo ya lo tiene
        cambios[campo] = FieldChange.Str(antes, nuevo);
        poner(nuevo.Length == 0 ? null : nuevo);
    }

    /// <summary>
    /// Renombra si el nombre del formulario es otro. Pasa por la misma validación que Enriquecer
    /// (RenameSafety): no admite rutas ni separadores, fuerza la extensión original y evita pisar.
    /// </summary>
    public static bool Renombrar(string ruta, string propuesto, out string rutaFinal, out string mensaje)
    {
        rutaFinal = ruta; mensaje = "";
        var nombre = (propuesto ?? "").Trim();
        var actual = Path.GetFileName(ruta);
        if (nombre.Length == 0 || string.Equals(nombre, actual, StringComparison.Ordinal)) return false;

        var dir = Path.GetDirectoryName(ruta) ?? "";
        if (!RenameSafety.TryResolveTarget(nombre, actual, dir, out var target, out var error))
        {
            mensaje = "NO se renombró: " + error;
            return false;
        }
        try
        {
            var destino = Path.Combine(dir, target);
            File.Move(ruta, destino);
            rutaFinal = destino;
            mensaje = $"renombrado a «{target}»";
            return true;
        }
        catch (Exception e)
        {
            mensaje = "NO se pudo renombrar: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Deja constancia en el manifiesto, con el MISMO formato que Enriquecer, para que «Deshacer
    /// última» revierta también lo editado a mano. Devuelve "" si quedó anotado, o el motivo.
    /// </summary>
    public static string Anotar(string manifiesto, string origen, string final, bool renombrado,
                                IReadOnlyDictionary<string, FieldChange> cambios)
    {
        try
        {
            var dir = Path.GetDirectoryName(manifiesto);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var rec = new UndoRecord
            {
                OrigPath = origen, FinalPath = final, Renamed = renombrado,
                Fields = new Dictionary<string, FieldChange>(cambios),
                // Sin esto, una edición que solo cambia el género salía en el historial como
                // «Géneros», igual que Unificar géneros: los campos no las distinguen.
                Origen = nameof(TipoEjecucion.EdicionEtiquetas),
            };
            File.AppendAllText(manifiesto, JsonSerializer.Serialize(rec) + Environment.NewLine, Encoding.UTF8);
            return "";
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>
    /// Guarda: escribe lo tocado, renombra si el nombre cambió y lo anota para deshacer. Si las
    /// etiquetas no se pueden escribir, lanza y no se renombra (como en Enriquecer: media
    /// aplicación es peor que ninguna).
    /// </summary>
    public static GuardadoEditor Guardar(string ruta, TagValues cargado, FormularioEditor form, uint anio, uint bpm,
                                         string manifiesto)
    {
        var cambios = EscribirCambios(ruta, cargado, form, anio, bpm);
        var renombrado = Renombrar(ruta, form.FileName, out var rutaFinal, out var mensaje);
        var hubo = cambios.Count > 0 || renombrado;
        var error = hubo ? Anotar(manifiesto, ruta, rutaFinal, renombrado, cambios) : "";
        return new GuardadoEditor(cambios, renombrado, rutaFinal, mensaje, hubo && error.Length == 0, error);
    }

    /// <summary>
    /// Pone en la canción de la biblioteca (en memoria, para las demás pestañas) los campos que has
    /// tocado, tal como los leería el escaneo: un campo vacío es null, y los artistas y los géneros van
    /// unidos por «; », como los une TagLib (los escritos, releídos del archivo). Lo que no has tocado se
    /// queda como estaba.
    ///
    /// POR QUÉ (revisión 3). Antes se ponían todos los campos del formulario, también los no tocados, y
    /// el formulario enseña "" donde el escaneo deja null: una canción sin género pasaba de null a "" con
    /// solo corregirle el título, y Organizar por género daba la vista previa por desfasada («alguna
    /// canción ha cambiado de género») sin que nada hubiera cambiado.
    /// </summary>
    public static void Reflejar(Track t, TagValues cargado, FormularioEditor form, GuardadoEditor res, uint anio, uint bpm)
    {
        static string? Nada(string s) => s.Length == 0 ? null : s;
        string? Lista(string campo, IEnumerable<string> delFormulario)
            => Nada(string.Join("; ", res.Cambios.TryGetValue(campo, out var c) && c.NewArr != null ? c.NewArr : delFormulario));

        if (form.Title != cargado.Title) t.Title = Nada(form.Title);
        if (form.Album != cargado.Album) t.Album = Nada(form.Album);
        if (form.Artist != cargado.Artist) t.Artist = Lista("Artist", SepararArtistas(form.Artist, Array.Empty<string>()));
        if (form.Genre != cargado.Genre) t.Genre = Lista("Genre", SepararGeneros(form.Genre));
        if (anio != cargado.Year) t.Year = anio;
        if (bpm != cargado.Bpm) t.Bpm = bpm;
    }

    /// <summary>
    /// Lo que se dice tras guardar. «Se puede deshacer» solo si de verdad quedó anotado: antes se
    /// decía siempre, también cuando no se anotó nada (solo había cambiado el comentario) o cuando
    /// el manifiesto no se pudo escribir.
    /// </summary>
    public static string MensajeGuardado(string archivo, GuardadoEditor g, string avisoBpm)
    {
        if (!g.HuboCambios)
            return g.MensajeRenombrado.Length > 0
                ? $"No se ha cambiado nada en «{archivo}»: {g.MensajeRenombrado}"
                : $"No había nada que guardar: «{archivo}» ya tiene esos datos.";

        var s = $"Guardado: {archivo}";
        if (g.MensajeRenombrado.Length > 0) s += " · " + g.MensajeRenombrado;
        if (avisoBpm.Length > 0) s += " · " + avisoBpm;
        s += g.Anotado ? " · se puede deshacer"
           : $" · ⚠ NO quedó anotado, no se podrá deshacer ({g.ErrorAnotar})";
        return s;
    }

    /// <summary>
    /// ¿La propuesta que acaba de llegar es de otra canción que la del formulario? Pasa si se cambia
    /// de canción mientras «Reanalizar…» busca (1-5 s con varias fuentes e IA).
    /// </summary>
    public static bool PropuestaDeOtraCancion(Track pedida, Track? enPantalla) => !ReferenceEquals(pedida, enPantalla);

    /// <summary>El formulario con la propuesta encima: solo lo que trae; lo demás se queda como estaba.</summary>
    public static FormularioEditor ConPropuesta(FormularioEditor f, ProcessResult r) => f with
    {
        Title = r.Title.Length > 0 ? r.Title : f.Title,
        Artist = r.Artist.Length > 0 ? r.Artist : f.Artist,
        Album = r.Album.Length > 0 ? r.Album : f.Album,
        Genre = r.Genre.Length > 0 ? r.Genre : f.Genre,
        Year = r.Year.Length > 0 ? r.Year : f.Year,
        Bpm = r.Bpm.Length > 0 ? r.Bpm : f.Bpm,
        FileName = r.New.Length > 0 ? r.New : f.FileName,
    };
}
