using Etiquetador.Core.Ai;

namespace Etiquetador.Tests;

/// <summary>
/// Nombres cortados a medias por los record pools. Los casos son reales: salen de medir una
/// biblioteca de 15.173 canciones, donde 1.344 nombres están cortados y 828 se completan con las
/// etiquetas del propio archivo.
/// </summary>
public class NombreCortadoTests
{
    private static string R(string archivo) => Path.Combine(Path.GetTempPath(), "beattag-pruebas", archivo);

    // --- Detección ---

    [Theory]
    [InlineData("50 Cent - In Da Club (WILSO 126-95 Transitio.mp3", "", "")]                  // paréntesis sin cerrar
    [InlineData("Alesso & Katy Perry - When I'm Gone [Alex Pi.mp3", "", "")]                  // corchete sin cerrar
    [InlineData("Ahora Me Llama x Pjanoo (Dion Dobbe x Taron .mp3", "", "")]                  // paréntesis sin cerrar
    [InlineData("Bad Bunny x.mp3", "", "")]                                                   // acaba colgando de una «x»
    [InlineData("Quevedo, Bizarrap - Sesion 52 feat.mp3", "", "")]
    // La última palabra es el principio de una que las etiquetas traen entera.
    [InlineData("SEVILLANAS TRIANA - LA HISTORIA DE UNA AMAPO.mp3", "Sevillanas Triana", "LA HISTORIA DE UNA AMAPOLA")]
    public void Reconoce_un_nombre_cortado(string archivo, string tagA, string tagT)
        => Assert.True(NombreCortado.Parece(R(archivo), tagA, tagT));

    // Falsos positivos que aparecieron al medir con la biblioteca real.
    [Theory]
    [InlineData("Feid - Lady Mi Amor (Extended).mp3", "Feid", "Lady Mi Amor (Extended)")]
    [InlineData("The Wiseguys - Ooh La La.mp3", "The Wiseguys", "Ooh La La")]                  // acaba en «La», no está cortado
    [InlineData("Basstyler - Step Bass.mp3", "Basstyler", "Step Bass")]                        // «Bass» es principio de «Basstyler»
    [InlineData("The Brainkiller - Backstreet's Back.mp3", "The Brainkiller; Pok3r", "Backstreet's Back")]
    public void Un_nombre_entero_no_se_da_por_cortado(string archivo, string tagA, string tagT)
        => Assert.False(NombreCortado.Parece(R(archivo), tagA, tagT));

    // --- Se completa con las etiquetas del propio archivo ---

    [Fact]
    public void Las_etiquetas_completan_el_nombre()
    {
        var p = NombreCortado.DesdeEtiquetas(R("Dubloadz - Night Mode  Shade K Bootleg 130Bp.mp3"),
                                             "Dubloadz", "Night Mode (Shade K Bootleg 130Bpm)");

        Assert.Equal("Dubloadz - Night Mode (Shade K Bootleg 130Bpm)", p);
    }

    // Con los feat largos, la etiqueta de artista lista a todos los invitados y no continúa el
    // nombre; la de título sí. Se conserva entonces el artista que ya trae el nombre.
    [Fact]
    public void Si_solo_continua_el_titulo_se_conserva_el_artista_del_nombre()
    {
        var p = NombreCortado.DesdeEtiquetas(R("Los Grandotes RD - Policia Motores (feat. Le.mp3"),
                                             "Los Grandotes RD; Lirico En La Casa; Leo RD",
                                             "Policia Motores (feat. Leo RD, El Fother)");

        Assert.Equal("Los Grandotes RD - Policia Motores (feat. Leo RD, El Fother)", p);
    }

    // Lo que las etiquetas dicen tiene que CONTINUAR el nombre, no ser otra cosa: en los record pools
    // las etiquetas llevan a veces el pack o el editor.
    [Fact]
    public void Unas_etiquetas_que_no_continuan_el_nombre_no_valen()
    {
        Assert.Null(NombreCortado.DesdeEtiquetas(R("50 Cent - In Da Club (WILSO 126-95 Transitio.mp3"),
                                                  "40 mashups by Alex Gonzalez", "Pack Vol 3"));
    }

    // Visto en la aplicacion: la etiqueta de artista traia el alias del editor y el nombre propuesto
    // empezaba por el.
    [Fact]
    public void El_alias_de_un_editor_no_se_pone_como_artista()
    {
        var p = NombreCortado.DesdeEtiquetas(R("Discoteca - IAmChino ft. Pitbull (Liyo Open.mp3"),
                                             "@liyo dj98", "Discoteca - IAmChino ft. Pitbull (Liyo Open Show 128 Bpm)");

        Assert.Equal("Discoteca - IAmChino ft. Pitbull (Liyo Open Show 128 Bpm)", p);
    }

    [Fact]
    public void Si_las_etiquetas_dicen_lo_mismo_no_hay_nada_que_completar()
    {
        Assert.Null(NombreCortado.DesdeEtiquetas(R("Feid - Lady Mi Amor.mp3"), "Feid", "Lady Mi Amor"));
    }

    // --- Lo que completa la IA ---

    // Respuesta real: «(Intro Privad» → «(Intro Privado)».
    [Fact]
    public void La_ia_puede_alargar_la_palabra_cortada()
    {
        var p = NombreCortado.DesdeIa(R("Tego Calderon - Pa Que Retocen (Intro Privad.mp3"),
                                       "Tego Calderon", "Pa Que Retocen", "Intro Privado");

        Assert.Equal("Tego Calderon - Pa Que Retocen (Intro Privado)", p);
    }

    // Respuestas reales: solo recolocan el trozo cortado entre paréntesis. Eso no completa nada.
    [Theory]
    [InlineData("Karol G, Bad Bunny - Bichota x La Zona (Paul.mp3", "Karol G, Bad Bunny", "Bichota x La Zona", "Paul")]
    [InlineData("Don Omar-Diva Virtual [Christian Maas Remi-1.mp3", "Don Omar", "Diva Virtual", "Christian Maas Remi-1")]
    public void Recolocar_el_trozo_cortado_no_es_completarlo(string archivo, string artista, string titulo, string version)
        => Assert.Null(NombreCortado.DesdeIa(R(archivo), artista, titulo, version));

    // Y nunca puede cambiar la canción por otra.
    [Fact]
    public void La_ia_no_puede_sustituir_el_nombre_por_otro()
    {
        Assert.Null(NombreCortado.DesdeIa(R("Stay Worry (Spice Mash) [128bp.mp3"), "Spice Girls", "Wannabe", "Mash"));
    }
}
