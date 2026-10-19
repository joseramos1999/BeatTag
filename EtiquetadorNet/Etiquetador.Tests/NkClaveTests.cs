using Etiquetador.Core;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// Comparar títulos en otros alfabetos. Nk se queda solo con a-z/0-9, y al unir las comprobaciones
/// de título de MusicBrainz con los nombres en cirílico, «KARTASHOW - Золото» dejaba de
/// identificarse: «Золото» quedaba en "" y no casaba ni consigo mismo.
/// </summary>
public class NkClaveTests
{
    [Theory]
    [InlineData("Золото", "золото")]
    [InlineData("Ε. Signaler", "εsignaler")]   // mezcla: Nk ya daría "signaler"
    public void Conserva_las_letras_de_otros_alfabetos_cuando_Nk_no_deja_nada(string texto, string esperado)
    {
        var clave = TextUtils.NkClave(texto);
        if (TextUtils.Nk(texto).Length > 0) Assert.Equal(TextUtils.Nk(texto), clave);
        else Assert.Equal(esperado, clave);
    }

    // El hangul se descompone en jamo al quitar diacríticos: el resultado no es el texto original,
    // pero es el mismo para los dos lados de una comparación, que es lo que importa.
    [Fact]
    public void El_hangul_no_queda_vacio_y_casa_consigo_mismo()
    {
        Assert.NotEqual("", TextUtils.NkClave("강남스타일"));
        Assert.Equal(TextUtils.NkClave("강남스타일"), TextUtils.NkClave("강남스타일"));
        Assert.True(ReglasCatalogo.TituloVerificado("강남스타일", "강남스타일", out _));
    }

    [Theory]
    [InlineData("Rosalía - Malamente")]
    [InlineData("Bad Bunny")]
    public void Con_letras_latinas_es_igual_que_Nk(string texto)
        => Assert.Equal(TextUtils.Nk(texto), TextUtils.NkClave(texto));

    [Fact]
    public void Un_titulo_en_cirilico_casa_consigo_mismo_y_no_con_otro()
    {
        Assert.True(ReglasCatalogo.TituloVerificado("Золото", "Золото", out var exacto));
        Assert.True(exacto);
        Assert.False(ReglasCatalogo.TituloVerificado("Золото", "Группа крови", out _));
        Assert.False(ReglasCatalogo.TituloVerificado("Золото", "Gold", out _));
    }
}
