using System.Text;
using Etiquetador.Core;
using Etiquetador.Core.Dj;

namespace Etiquetador.Tests;

/// <summary>
/// Un disco en memoria para planear sin archivos: lo que hay, con el nombre con que está. Compara
/// como el disco de Windows y de macOS, sin distinguir mayúsculas ni la forma Unicode.
/// </summary>
internal sealed class DiscoEnMemoria : IAccesoDisco
{
    private readonly HashSet<string> _archivos = new(ComparadorRutas.Instancia);
    private readonly List<string> _carpetas = new();

    /// <summary>La unidad de cada ruta. Por defecto, su raíz.</summary>
    public Func<string, string> Unidad { get; set; } = r => Path.GetPathRoot(r) ?? "";

    public DiscoEnMemoria Archivo(string ruta)
    {
        _archivos.Add(ruta);
        Carpeta(Path.GetDirectoryName(ruta) ?? "");
        return this;
    }

    public DiscoEnMemoria Carpeta(string ruta)
    {
        for (var c = ruta; !string.IsNullOrEmpty(c); c = Path.GetDirectoryName(c))
            if (!_carpetas.Contains(c, ComparadorRutas.Instancia)) _carpetas.Add(c);
        return this;
    }

    public bool ExisteArchivo(string ruta) => _archivos.Contains(ruta) || _carpetas.Contains(ruta, ComparadorRutas.Instancia);

    public bool ExisteCarpeta(string carpeta) => _carpetas.Any(c => Etiquetador.Core.Rutas.MismaCarpeta(c, carpeta));

    public string? NombreRealDeCarpeta(string padre, string nombre)
        => _carpetas.Where(c => ComparadorRutas.Instancia.Equals(Path.GetDirectoryName(c) ?? "", padre.TrimEnd('\\', '/'))
                                && ComparadorRutas.Instancia.Equals(Path.GetFileName(c), nombre))
                    .Select(Path.GetFileName).FirstOrDefault();

    public string UnidadDe(string ruta) => Unidad(ruta);
}

/// <summary>
/// Organizar por género (B1 y B2): el plan que se enseña en la vista previa. Con rutas construidas
/// con Path.Combine y un disco en memoria: no se crea ni se mueve nada.
/// </summary>
public class OrganizarPorGeneroPlanTests
{
    // Una ruta con raíz de verdad («C:\» o «/»), pero que no se toca: el disco es de mentira.
    private static readonly string Raiz = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Musica");
    private static readonly string PorGenero = Path.Combine(Raiz, OrganizarPorGenero.CarpetaPorGenero);

    private static Track T(string carpeta, string archivo, string? genero, string? artista = null, string? raiz = null)
    {
        var r = raiz ?? Raiz;
        return new Track
        {
            FilePath = Path.Combine(r, carpeta, archivo), Folder = r, Genre = genero, Artist = artista,
            Title = Path.GetFileNameWithoutExtension(archivo),
        };
    }

    private static DiscoEnMemoria Disco(IEnumerable<Track> canciones)
    {
        var d = new DiscoEnMemoria();
        foreach (var t in canciones) d.Archivo(t.FilePath);
        return d;
    }

    private static PlanOrganizar Plan(IReadOnlyList<Track> canciones, OpcionesOrganizar? op = null, DiscoEnMemoria? disco = null,
                                      IReadOnlyList<string>? raices = null)
        => OrganizarPorGenero.Planear(canciones, raices ?? new[] { Raiz }, op ?? new OpcionesOrganizar(), disco ?? Disco(canciones));

    private static MovimientoOrganizar Unico(PlanOrganizar plan) => Assert.Single(plan.Movimientos);

    // --- Casos de la medida (línea organizar) ---

    [Fact]
    public void Caso_1_una_cancion_de_pop_va_a_por_genero_pop()
    {
        var t = T("Latino", "Pablo Alboran; Ava Max - Tabú.mp3", "Pop", "Pablo Alboran; Ava Max");

        var m = Unico(Plan(new[] { t }));

        Assert.Equal(Path.Combine(PorGenero, "Pop", "Pablo Alboran; Ava Max - Tabú.mp3"), m.Destino);
        Assert.Equal("Pop", m.Genero);
        Assert.Equal("Pop", m.CarpetaGenero);
    }

    [Fact]
    public void Caso_2_un_mashup_se_queda_salvo_que_se_pida_moverlo_y_puede_conservar_su_carpeta()
    {
        var t = T("Mashups Reggaeton2 C-E", "La Reina x Lovumba (Try It Mashup 130Bpm).mp3", "Dance");

        var dejar = Plan(new[] { t });
        Assert.Empty(dejar.Movimientos);
        var omitida = Assert.Single(dejar.Omitidas);
        Assert.Equal(MotivoOmision.HerramientaDj, omitida.Motivo);
        Assert.StartsWith("Herramienta de DJ", omitida.Texto);

        var mover = Unico(Plan(new[] { t }, new OpcionesOrganizar { DejarHerramientas = false }));
        Assert.Equal(Path.Combine(PorGenero, "Dance", t.FileName), mover.Destino);

        var estructura = Unico(Plan(new[] { t }, new OpcionesOrganizar { DejarHerramientas = false, ConservarEstructura = true }));
        Assert.Equal(Path.Combine(PorGenero, "Dance", "Mashups Reggaeton2 C-E", t.FileName), estructura.Destino);
    }

    [Fact]
    public void Caso_3_sin_genero_se_queda_o_va_a_sin_genero_si_se_pide()
    {
        var t = T("WarmUp", "Tema sin etiquetar.mp3", null);

        var queda = Plan(new[] { t });
        Assert.Empty(queda.Movimientos);
        Assert.Equal(MotivoOmision.SinGenero, Assert.Single(queda.Omitidas).Motivo);

        var mueve = Unico(Plan(new[] { t }, new OpcionesOrganizar { MoverSinGenero = true }));
        Assert.Equal(Path.Combine(PorGenero, OrganizarPorGenero.CarpetaSinGenero, t.FileName), mueve.Destino);
        Assert.Equal("", mueve.Genero);
    }

    [Fact]
    public void Caso_4_las_grafias_de_un_genero_acaban_en_una_sola_carpeta()
    {
        var canciones = new[]
        {
            T("A", "uno.mp3", "reggaeton"), T("A", "dos.mp3", "Reggaetón"), T("A", "tres.mp3", "Reguetón"), T("B", "cuatro.mp3", "Reggaeton"),
        };

        var plan = Plan(canciones);

        Assert.Equal(4, plan.Movimientos.Count);
        Assert.All(plan.Movimientos, m => Assert.Equal("Reggaeton", m.CarpetaGenero));
        Assert.Single(plan.PorDestino);
    }

    [Theory]
    [InlineData("Rap; Hip Hop", "Hip Hop")]
    [InlineData("Alternativo; Indie Rock", "Alternativo")]
    public void Caso_5_con_una_lista_cuenta_el_genero_principal(string etiqueta, string carpeta)
        => Assert.Equal(carpeta, Unico(Plan(new[] { T("A", "x.mp3", etiqueta) })).CarpetaGenero);

    [Theory]
    [InlineData("AC/DC: Tribute", "AC-DC- Tribute")]
    [InlineData("CON", "CON (género)")]
    [InlineData("lpt1", "lpt1 (género)")]
    [InlineData("Nul.x", "Nul (género).x")]
    [InlineData(" .Trance. ", "Trance")]
    [InlineData("Drum & Bass", "Drum & Bass")]
    [InlineData("Tab\tHouse", "TabHouse")]
    [InlineData("...", OrganizarPorGenero.GeneroSinNombre)]
    [InlineData("", OrganizarPorGenero.GeneroSinNombre)]
    public void Caso_6_el_nombre_de_la_carpeta_vale_en_cualquier_sistema(string genero, string carpeta)
        => Assert.Equal(carpeta, OrganizarPorGenero.NombreCarpetaGenero(genero));

    [Fact]
    public void Caso_6_el_nombre_sale_en_NFC_y_con_255_bytes_como_mucho()
    {
        var nfd = "Reggaetón".Normalize(NormalizationForm.FormD);
        var nombre = OrganizarPorGenero.NombreCarpetaGenero(nfd);
        Assert.True(nombre.IsNormalized(NormalizationForm.FormC));
        Assert.Equal("Reggaetón".Normalize(NormalizationForm.FormC), nombre);

        var largo = new string('á', 150);   // 300 bytes en UTF-8
        var recortado = OrganizarPorGenero.NombreCarpetaGenero(largo);
        Assert.True(Encoding.UTF8.GetByteCount(recortado) <= OrganizarPorGenero.BytesMaximoTramo);
        Assert.Equal(new string('á', 127), recortado);   // se corta entre caracteres, nunca a mitad de uno
    }

    [Fact]
    public void Caso_7_dos_archivos_con_el_mismo_nombre_mueve_el_de_ruta_menor_y_lista_el_otro()
    {
        var a = T("Descargas", "Bad Gyal - La Prendo.mp3", "Latin");
        var b = T("Latin Box", "Bad Gyal - La Prendo.mp3", "Latin");

        var plan = Plan(new[] { b, a });   // el orden de entrada no importa

        Assert.Equal(a.FilePath, Unico(plan).Origen);
        var o = Assert.Single(plan.Omitidas);
        Assert.Equal(b.FilePath, o.Ruta);
        Assert.Equal(MotivoOmision.Colision, o.Motivo);
        Assert.Equal("Ya hay un archivo con ese nombre en «Latin»", o.Texto);
    }

    [Fact]
    public void Caso_8_el_mismo_nombre_con_otras_mayusculas_y_en_NFD_es_una_colision()
    {
        var a = T("A", "Tití.mp3", "Reggaeton");
        var b = T("B", "TITÍ.mp3".Normalize(NormalizationForm.FormD), "Reggaeton");

        var plan = Plan(new[] { a, b });

        Assert.Single(plan.Movimientos);
        Assert.Equal(MotivoOmision.Colision, Assert.Single(plan.Omitidas).Motivo);
    }

    [Fact]
    public void Una_cancion_que_iria_a_un_nombre_ocupado_en_el_disco_no_lo_pisa()
    {
        var t = T("A", "Tema.mp3", "Pop");
        var disco = Disco(new[] { t }).Archivo(Path.Combine(PorGenero, "Pop", "tema.MP3"));

        var plan = Plan(new[] { t }, disco: disco);

        Assert.Empty(plan.Movimientos);
        Assert.Equal(MotivoOmision.Colision, Assert.Single(plan.Omitidas).Motivo);
    }

    [Theory]
    [InlineData(260, false)]
    [InlineData(259, true)]
    public void Caso_9_una_ruta_de_destino_de_260_caracteres_se_omite(int largo, bool semueve)
    {
        var carpeta = Path.Combine(PorGenero, "Pop") + Path.DirectorySeparatorChar;
        var archivo = new string('a', largo - carpeta.Length - 4) + ".mp3";
        var t = T("A", archivo, "Pop");

        var plan = Plan(new[] { t });

        Assert.Equal(semueve ? 1 : 0, plan.Movimientos.Count);
        if (semueve) Assert.Equal(largo, plan.Movimientos[0].Destino.Length);
        else Assert.Equal(MotivoOmision.RutaLarga, Assert.Single(plan.Omitidas).Motivo);
    }

    [Fact]
    public void Caso_10_lo_que_esta_en_otra_unidad_no_se_mueve()
    {
        var otraRaiz = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "USB");
        var dentro = T("A", "uno.mp3", "Pop");
        var fuera = T("B", "dos.mp3", "Pop", raiz: otraRaiz);
        var disco = Disco(new[] { dentro, fuera });
        disco.Unidad = r => Etiquetador.Core.Rutas.EstaDentroDe(r, otraRaiz) || Etiquetador.Core.Rutas.MismaCarpeta(r, otraRaiz) ? "usb" : "interna";

        var plan = Plan(new[] { dentro, fuera }, new OpcionesOrganizar { Destino = DestinoOrganizar.Carpeta, CarpetaDestino = PorGenero },
                        disco, new[] { Raiz, otraRaiz });

        Assert.Equal(dentro.FilePath, Unico(plan).Origen);
        var o = Assert.Single(plan.Omitidas);
        Assert.Equal(MotivoOmision.OtraUnidad, o.Motivo);
        Assert.Equal("Está en otra unidad", o.Texto);
    }

    [Fact]
    public void Caso_11_una_carpeta_con_ajustes_propios_se_queda_con_sus_subcarpetas()
    {
        var acapellas = Path.Combine(Raiz, "Acapellas");
        var dentro = T(Path.Combine("Acapellas", "Pack 1"), "Tema.mp3", "Pop");
        var fuera = T("Otra", "Otro.mp3", "Pop");

        var plan = Plan(new[] { dentro, fuera }, new OpcionesOrganizar { CarpetasConAjustes = new[] { acapellas } });

        Assert.Equal(fuera.FilePath, Unico(plan).Origen);
        Assert.Equal(MotivoOmision.CarpetaConAjustes, Assert.Single(plan.Omitidas).Motivo);
    }

    [Fact]
    public void Caso_12_el_destino_tiene_que_estar_en_la_biblioteca_y_no_en_una_carpeta_desmarcada()
    {
        var desmarcada = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "Viejas");
        var raices = new[] { Raiz };
        var desmarcadas = new[] { desmarcada };

        Assert.Equal("", OrganizarPorGenero.ValidarDestino(Raiz, raices, desmarcadas));
        Assert.Equal("", OrganizarPorGenero.ValidarDestino(Path.Combine(Raiz, "Organizada"), raices, desmarcadas));
        Assert.Contains("tiene que ser una carpeta de la biblioteca",
                        OrganizarPorGenero.ValidarDestino(Path.Combine(Path.GetPathRoot(Raiz)!, "Fuera"), raices, desmarcadas));
        Assert.Contains("«Viejas», que está desmarcada",
                        OrganizarPorGenero.ValidarDestino(Path.Combine(desmarcada, "Sub"), raices, desmarcadas));
        Assert.Equal("Elige la carpeta de destino.", OrganizarPorGenero.ValidarDestino(" ", raices, desmarcadas));
    }

    // Revisión 1: solo se protegía la SALIDA de las carpetas con ajustes, no la entrada. COMPROBADO:
    // con «En la raíz» y «Raíz/House» excluida de Duplicados, las canciones de House entraban en ella
    // y ganaban esa exclusión sin que nadie la pidiera.
    [Theory]
    [InlineData(DestinoOrganizar.EnLaRaiz)]
    [InlineData(DestinoOrganizar.PorGeneroEnCadaRaiz)]
    public void Una_cancion_no_entra_en_una_carpeta_con_ajustes_propios(DestinoOrganizar destino)
    {
        var house = destino == DestinoOrganizar.EnLaRaiz ? Path.Combine(Raiz, "House") : Path.Combine(PorGenero, "House");
        var canciones = new[] { T("Descargas", "a.mp3", "House"), T("Descargas", "b.mp3", "House"), T("Descargas", "c.mp3", "Pop") };
        var disco = Disco(canciones).Carpeta(house);

        var plan = Plan(canciones, new OpcionesOrganizar { Destino = destino, CarpetasConAjustes = new[] { house } }, disco);

        Assert.Equal("c.mp3", Path.GetFileName(Unico(plan).Origen));
        Assert.Equal(2, plan.CuantasOmitidas(MotivoOmision.DestinoConAjustes));
        Assert.All(plan.Omitidas, o => Assert.Contains("ajustes propios en Duplicados o en Volumen", o.Texto));
        Assert.Contains("2 se quedan porque su carpeta de destino tiene ajustes propios", OrganizarPorGenero.Resumen(plan));
    }

    [Fact]
    public void El_destino_elegido_no_puede_estar_en_una_carpeta_con_ajustes_propios()
    {
        var raices = new[] { Raiz };
        var conAjustes = new[] { Path.Combine(Raiz, "Acapellas") };

        Assert.Contains("«Acapellas», que tiene ajustes propios en Duplicados o en Volumen",
                        OrganizarPorGenero.ValidarDestino(Path.Combine(Raiz, "Acapellas"), raices, Array.Empty<string>(), conAjustes));
        Assert.Contains("ajustes propios",
                        OrganizarPorGenero.ValidarDestino(Path.Combine(Raiz, "Acapellas", "Sub"), raices, Array.Empty<string>(), conAjustes));
        Assert.Equal("", OrganizarPorGenero.ValidarDestino(Path.Combine(Raiz, "Organizada"), raices, Array.Empty<string>(), conAjustes));
    }

    [Fact]
    public void Caso_13_en_la_raiz_una_cancion_de_trap_en_la_carpeta_trap_ya_esta_en_su_sitio()
    {
        var t = T("Trap", "Tema.mp3", "Trap");

        var plan = Plan(new[] { t }, new OpcionesOrganizar { Destino = DestinoOrganizar.EnLaRaiz });

        Assert.Empty(plan.Movimientos);
        Assert.Equal(MotivoOmision.YaEnSuSitio, Assert.Single(plan.Omitidas).Motivo);
    }

    // --- Volver a organizar ---

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Volver_a_planificar_sobre_lo_ya_organizado_no_mueve_nada(bool estructura)
    {
        var canciones = new[]
        {
            T("Latino", "a.mp3", "Pop"), T(Path.Combine("Packs", "Enero"), "b.mp3", "Reggaeton"), T("", "c.mp3", "Techno"),
        };
        var op = new OpcionesOrganizar { ConservarEstructura = estructura };
        var primero = Plan(canciones, op);
        Assert.Equal(3, primero.Movimientos.Count);

        var organizadas = primero.Movimientos.Select(m =>
        {
            var t = canciones.Single(c => c.FilePath == m.Origen);
            return new Track { FilePath = m.Destino, Folder = Raiz, Genre = t.Genre };
        }).ToList();
        var segundo = Plan(organizadas, op);

        Assert.Empty(segundo.Movimientos);
        Assert.All(segundo.Omitidas, o => Assert.Equal(MotivoOmision.YaEnSuSitio, o.Motivo));
    }

    [Fact]
    public void Una_cancion_que_cambia_de_genero_pasa_a_su_carpeta_nueva_sin_anidar()
    {
        var t = new Track { FilePath = Path.Combine(PorGenero, "Latin", "Pack", "x.mp3"), Folder = Raiz, Genre = "Reggaeton" };

        Assert.Equal(Path.Combine(PorGenero, "Reggaeton", "x.mp3"), Unico(Plan(new[] { t })).Destino);
        Assert.Equal(Path.Combine(PorGenero, "Reggaeton", "Pack", "x.mp3"),
                     Unico(Plan(new[] { t }, new OpcionesOrganizar { ConservarEstructura = true })).Destino);
    }

    [Fact]
    public void Se_usa_el_nombre_que_ya_tiene_la_carpeta_en_el_disco()
    {
        var t = T("A", "x.mp3", "Hip Hop");
        var disco = Disco(new[] { t }).Carpeta(Path.Combine(Raiz, "POR GÉNERO", "hip hop"));

        var m = Unico(Plan(new[] { t }, disco: disco));

        Assert.Equal(Path.Combine(Raiz, "POR GÉNERO", "hip hop", "x.mp3"), m.Destino);
        Assert.False(Assert.Single(Plan(new[] { t }, disco: disco).PorDestino).Nueva);
    }

    [Fact]
    public void Con_agrupar_los_generos_pequenos_van_a_otros_generos()
    {
        var canciones = new[]
        {
            T("A", "1.mp3", "Reggaeton"), T("A", "2.mp3", "Reggaeton"), T("A", "3.mp3", "Reggaeton"),
            T("A", "4.mp3", "Polka"), T("A", "5.mp3", "Polka"), T("A", "6.mp3", "Fado"),
        };

        var plan = Plan(canciones, new OpcionesOrganizar { AgruparMenosDe = OrganizarPorGenero.AgruparPorDefecto });

        Assert.Equal(3, plan.Movimientos.Count(m => m.CarpetaGenero == "Reggaeton"));
        Assert.Equal(3, plan.Movimientos.Count(m => m.CarpetaGenero == OrganizarPorGenero.CarpetaOtros));
        Assert.Equal(2, plan.PorDestino.Count);
    }

    // Revisión 1: agrupar contaba el total del género en la biblioteca, también lo que se queda.
    [Fact]
    public void Agrupar_cuenta_solo_las_canciones_que_se_organizan()
    {
        var canciones = new[]
        {
            T("Mashups", "1 (Mashup).mp3", "Moombah"), T("Mashups", "2 (Mashup).mp3", "Moombah"),
            T("Mashups", "3 (Mashup).mp3", "Moombah"), T("Mashups", "4 (Mashup).mp3", "Moombah"),
            T("Descargas", "normal.mp3", "Moombah"),
            T("Ajustes", "a.mp3", "Fado"), T("Ajustes", "b.mp3", "Fado"), T("Descargas", "c.mp3", "Fado"),
        };
        var op = new OpcionesOrganizar { AgruparMenosDe = 3, CarpetasConAjustes = new[] { Path.Combine(Raiz, "Ajustes") } };

        var plan = Plan(canciones, op);

        Assert.Equal(2, plan.Movimientos.Count);
        Assert.All(plan.Movimientos, m => Assert.Equal(OrganizarPorGenero.CarpetaOtros, m.CarpetaGenero));
        Assert.Equal(Path.Combine(PorGenero, OrganizarPorGenero.CarpetaOtros, "normal.mp3"),
                     plan.Movimientos.Single(m => m.Genero == "Moombah").Destino);
    }

    [Fact]
    public void Agrupar_cuenta_tambien_las_que_ya_estan_en_su_carpeta_y_volver_a_organizar_no_las_mueve()
    {
        // Tres de Fado: dos ya en «Por género/Fado» y una nueva. No se agrupan, y la nueva va con ellas.
        var canciones = new[]
        {
            new Track { FilePath = Path.Combine(PorGenero, "Fado", "a.mp3"), Folder = Raiz, Genre = "Fado" },
            new Track { FilePath = Path.Combine(PorGenero, "Fado", "b.mp3"), Folder = Raiz, Genre = "Fado" },
            T("Descargas", "c.mp3", "Fado"),
        };

        var plan = Plan(canciones, new OpcionesOrganizar { AgruparMenosDe = 3 });

        Assert.Equal(Path.Combine(PorGenero, "Fado", "c.mp3"), Unico(plan).Destino);
        Assert.Equal(2, plan.CuantasOmitidas(MotivoOmision.YaEnSuSitio));
    }

    [Fact]
    public void Una_carpeta_de_origen_desmarcada_se_queda_entera_y_el_reparto_lo_dice()
    {
        var canciones = new[]
        {
            T("WarmUp", "1.mp3", "Reggaeton"), T("WarmUp", "2.mp3", "Pop"),
            T("Latino", "3.mp3", "Reggaeton"), T("Latino", "4.mp3", "Reggaeton"), T("Latino", "5.mp3", "Latin"),
        };
        var warmUp = Path.Combine(Raiz, "WarmUp");

        var plan = Plan(canciones, new OpcionesOrganizar { OrigenesExcluidos = new[] { warmUp } });

        Assert.Equal(3, plan.Movimientos.Count);
        Assert.Equal(2, plan.CuantasOmitidas(MotivoOmision.OrigenDesmarcado));
        var origen = plan.PorOrigen.Single(o => o.Carpeta == warmUp);
        Assert.True(origen.Excluida);
        Assert.Equal((2, 0, 2), (origen.Total, origen.SeMueven, origen.SeQuedan));
        var latino = plan.PorOrigen.Single(o => o.Carpeta == Path.Combine(Raiz, "Latino"));
        Assert.Equal("2 a Reggaeton · 1 a Latin", latino.Reparto);
        Assert.Equal(2, plan.MediaGenerosPorOrigen);
    }

    // Revisión 1: las canciones de una carpeta de la biblioteca que no está disponible (un disco externo
    // desconectado: la biblioteca las conserva) entraban como traslados. COMPROBADO con una raíz en Q:\
    // inexistente: «Se moverán 2 canciones a 2 carpetas», que después fallaban una a una.
    [Fact]
    public void Las_canciones_de_una_carpeta_que_no_esta_disponible_se_quedan_con_su_motivo()
    {
        var externo = Path.Combine(Path.GetPathRoot(Raiz)!, "Disco externo");
        var dentro = T("A", "uno.mp3", "Pop");
        var fuera = new[] { T("B", "dos.mp3", "Pop", raiz: externo), T("C", "tres.mp3", "Rock", raiz: externo) };
        var disco = Disco(new[] { dentro });   // el disco externo no está

        var plan = Plan(fuera.Append(dentro).ToList(), disco: disco, raices: new[] { Raiz, externo });

        Assert.Equal(dentro.FilePath, Unico(plan).Origen);
        Assert.Equal(2, plan.CuantasOmitidas(MotivoOmision.NoDisponible));
        Assert.All(plan.Omitidas, o => Assert.Equal("Su carpeta no está disponible", o.Texto));
        Assert.Contains("2 se omiten porque su carpeta no está disponible", OrganizarPorGenero.Resumen(plan));
    }

    [Fact]
    public void El_disco_real_dice_que_una_carpeta_que_no_existe_no_esta_disponible()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var musica = Directory.CreateDirectory(Path.Combine(dir, "Musica")).FullName;
            var a = Path.Combine(musica, "A", "uno.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(a)!);
            File.WriteAllText(a, "x");
            var desconectada = Path.Combine(dir, "Desconectada");
            var canciones = new[]
            {
                new Track { FilePath = a, Folder = musica, Genre = "Pop" },
                new Track { FilePath = Path.Combine(desconectada, "B", "dos.mp3"), Folder = desconectada, Genre = "Pop" },
            };

            var plan = OrganizarPorGenero.Planear(canciones, new[] { musica, desconectada }, new OpcionesOrganizar(), new DiscoReal(Array.Empty<string>()));

            Assert.Equal(a, Unico(plan).Origen);
            Assert.Equal(MotivoOmision.NoDisponible, Assert.Single(plan.Omitidas).Motivo);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Lo_que_no_esta_en_las_carpetas_marcadas_no_entra_en_el_plan()
    {
        var fuera = T("A", "x.mp3", "Pop", raiz: Path.Combine(Path.GetPathRoot(Raiz)!, "Otra"));

        var plan = Plan(new[] { fuera });

        Assert.Equal(0, plan.Canciones);
        Assert.Empty(plan.Movimientos);
        Assert.Empty(plan.Omitidas);
    }

    // --- B2: la unidad de una ruta ---

    [Fact]
    public void Un_volumen_montado_dentro_de_otro_es_otra_unidad()
    {
        var raiz = Path.Combine(Path.GetTempPath(), "beattag-unidades");
        var usb = Path.Combine(raiz, "Volumes", "USB");
        var puntos = new[] { raiz, usb };

        Assert.Equal(usb, Etiquetador.Core.Rutas.UnidadDe(Path.Combine(usb, "Musica", "a.mp3"), puntos));
        Assert.Equal(raiz, Etiquetador.Core.Rutas.UnidadDe(Path.Combine(raiz, "Musica", "b.mp3"), puntos));
        Assert.False(Etiquetador.Core.Rutas.MismaUnidad(Path.Combine(usb, "a.mp3"), Path.Combine(raiz, "Musica", "b.mp3"), puntos));
        Assert.True(Etiquetador.Core.Rutas.MismaUnidad(Path.Combine(raiz, "A", "a.mp3"), Path.Combine(raiz, "B", "b.mp3"), puntos));
        Assert.True(Etiquetador.Core.Rutas.MismaUnidad(usb, Path.Combine(usb, "x.mp3"), puntos));
    }

    [Fact]
    public void Sin_puntos_de_montaje_vale_la_raiz_de_la_ruta()
    {
        var ruta = Path.Combine(Path.GetTempPath(), "a.mp3");

        Assert.Equal(Path.GetPathRoot(ruta), Etiquetador.Core.Rutas.UnidadDe(ruta, Array.Empty<string>()));
        Assert.True(Etiquetador.Core.Rutas.MismaUnidad(ruta, Path.Combine(Path.GetTempPath(), "b", "c.mp3"), Array.Empty<string>()));
    }

    [Fact]
    public void El_disco_real_lee_la_carpeta_sin_distinguir_mayusculas_ni_NFC()
    {
        var dir = Mp3Fixture.NewTempDir();
        try
        {
            var genero = Path.Combine(dir, "Reggaetón");
            Directory.CreateDirectory(genero);
            File.WriteAllText(Path.Combine(genero, "Tití.mp3"), "x");
            var disco = new DiscoReal(Array.Empty<string>());

            Assert.Equal("Reggaetón", disco.NombreRealDeCarpeta(dir, "REGGAETÓN".Normalize(NormalizationForm.FormD)));
            Assert.Null(disco.NombreRealDeCarpeta(dir, "Pop"));
            Assert.True(disco.ExisteArchivo(Path.Combine(genero, "TITÍ.mp3".Normalize(NormalizationForm.FormD))));
            Assert.False(disco.ExisteArchivo(Path.Combine(genero, "Otro.mp3")));
            Assert.False(disco.ExisteArchivo(Path.Combine(dir, "No existe", "x.mp3")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
