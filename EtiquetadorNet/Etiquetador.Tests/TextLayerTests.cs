using Etiquetador.Core;

namespace Etiquetador.Tests;

// Espejo en xUnit de las pruebas Pester puras de Etiquetador.Tests.ps1 (contrato de comportamiento).
public class TextLayerTests
{
    // --- NK y To-Ascii (normalizacion) ---
    [Fact] public void Nk_quita_acentos_mayusculas_signos() => Assert.Equal("tildenono", TextUtils.Nk("Tílde, Ñoño!"));
    [Fact] public void ToAscii_mapea_obarrada_y_ntilde() => Assert.Equal("Bjork Nu", TextUtils.ToAscii("Bjørk Ñu"));
    [Fact] public void ToAscii_quita_acentos_conservando_ascii() => Assert.Equal("Rosalia", TextUtils.ToAscii("Rosalía"));

    [Theory]
    [InlineData("Bad Bunny", "badbunny")]
    [InlineData("RVFV", "rvfv")]
    [InlineData("AC/DC", "acdc")]
    [InlineData("404", "404")]
    public void Nk_casos_basicos(string input, string expected) => Assert.Equal(expected, TextUtils.Nk(input));

    // --- JaroWinkler ---
    [Fact] public void Jw_identicos_1() => Assert.Equal(1.0, Matching.JaroWinkler("gasolina", "gasolina"));
    [Fact] public void Jw_typo_alto() => Assert.True(Matching.JaroWinkler("rumbatom", "rumbaton") > 0.9);
    [Fact] public void Jw_transposicion_alta() => Assert.True(Matching.JaroWinkler("emiilo", "emilio") > 0.9);
    [Fact] public void Jw_distintas_bajo() => Assert.True(Matching.JaroWinkler("gasolina", "reggaeton") < 0.7);

    // --- Is-SkipMix ---
    [Fact] public void Skip_mashup() => Assert.True(Matching.IsSkipMix("Don Omar - Taboo Pedro Cabrera Mashup", "Taboo", "Don Omar"));
    [Fact] public void Skip_bootleg() => Assert.True(Matching.IsSkipMix("Sandstorm Freejak Bootleg", "Sandstorm", "Freejak"));
    [Fact] public void Skip_transition() => Assert.True(Matching.IsSkipMix("Esta Noche Bayne 90 Transition", "Esta Noche", "J Quiles"));
    [Fact] public void Skip_vs() => Assert.True(Matching.IsSkipMix("P.I.M.P. Vs Una Noche", "P.I.M.P. Vs Una Noche", ""));
    [Fact] public void NoSkip_extended() => Assert.False(Matching.IsSkipMix("Daddy Yankee - Rumbaton (Extended)", "Rumbaton (Extended)", "Daddy Yankee"));
    [Fact] public void NoSkip_colab_x() => Assert.False(Matching.IsSkipMix("Feid x Karol G - Contigo", "Contigo", "Feid x Karol G"));
    [Fact] public void Skip_edit_de_dj() => Assert.True(Matching.IsSkipMix("Bad Bunny - Moscow Mule (Juan Edit)", "Moscow Mule (Juan Edit)", "Bad Bunny"));
    [Fact] public void NoSkip_radio_edit() => Assert.False(Matching.IsSkipMix("Rihanna - Diamonds (Radio Edit)", "Diamonds (Radio Edit)", "Rihanna"));
    [Fact] public void NoSkip_extended_edit() => Assert.False(Matching.IsSkipMix("Avicii - Levels (Extended Edit)", "Levels (Extended Edit)", "Avicii"));

    // --- RMX -> Remix ---
    [Fact] public void Rmx_se_normaliza_a_remix()
        => Assert.Contains("Remix", Descriptors.ExtractOtros("Gasolina (RMX)", "Gasolina"));

    // --- UnScream ---
    [Fact] public void Unscream_multipalabra() => Assert.Equal("Pa' Romperla", TextUtils.UnScream("PA' ROMPERLA"));
    [Fact] public void Unscream_respeta_minusculas() => Assert.Equal("TiK ToK", TextUtils.UnScream("TiK ToK"));
    [Fact] public void Unscream_respeta_siglas() => Assert.Equal("RVFV", TextUtils.UnScream("RVFV"));
    [Fact] public void Unscream_nombre_largo() => Assert.Equal("El Rey De La Tarima", TextUtils.UnScream("EL REY DE LA TARIMA"));

    // --- Normalize-Artists ---
    private static readonly ArtistExceptions Exc = new();
    [Fact] public void Na_protege_multipalabra() => Assert.Equal("DJ Snake", Exc.NormalizeArtists("DJ SNAKE"));
    [Fact] public void Na_canoniza_casing() => Assert.Equal("deadmau5", Exc.NormalizeArtists("DEADMAU5"));
    [Fact] public void Na_canoniza_jayz() => Assert.Equal("JAY-Z", Exc.NormalizeArtists("JAY Z"));
    [Fact] public void Na_por_componente() => Assert.Equal("Bad Bunny, DJ Snake", Exc.NormalizeArtists("Bad Bunny, DJ SNAKE"));
    [Fact] public void Na_degrita_no_excepcion() => Assert.Equal("Los Del Rio", Exc.NormalizeArtists("LOS DEL RIO"));
    [Fact] public void Na_protege_sigla_aa() => Assert.Equal("Anuel AA", Exc.NormalizeArtists("ANUEL AA"));
    [Fact] public void Na_restaura_enye() => Assert.Equal("Ñengo Flow", Exc.NormalizeArtists("NENGO FLOW"));
    [Fact] public void Na_restaura_acento() => Assert.Equal("Tego Calderón", Exc.NormalizeArtists("tego calderon"));
    [Fact] public void Na_excepcion_en_colab() => Assert.Equal("Bad Bunny, Anuel AA", Exc.NormalizeArtists("Bad Bunny, ANUEL AA"));

    // --- Complete-Truncated ---
    [Fact] public void Ct_completa() => Assert.Equal("Blasco Intro Extended", Descriptors.CompleteTruncated("Blasco Intro Extend"));
    [Fact] public void Ct_no_toca() => Assert.Equal("Kapulo", Descriptors.CompleteTruncated("Kapulo"));

    // --- Build-Kw ---
    [Fact] public void Bk_ft_no_borra_titulo() => Assert.Matches("(?i)big soto", Descriptors.BuildKw("Big Soto ft. De La Ghetto", "Climaxx"));
    [Fact] public void Bk_conserva_titulo() => Assert.Matches("(?i)climaxx", Descriptors.BuildKw("Big Soto ft. De La Ghetto", "Climaxx"));

    // --- Extract-Otros ---
    [Fact] public void Eo_recupera_extended()
        => Assert.Matches("(?i)extended", string.Join(",", Descriptors.ExtractOtros(Descriptors.CompleteTruncated("Song (YANISS Extended Mi"), "Song")));
    [Fact] public void Eo_no_duplica() => Assert.Equal("", string.Join(",", Descriptors.ExtractOtros("Song (Remix)", "Song Remix")));

    // --- Clean-Title / Clean-DbTitle ---
    [Fact] public void Clt_quita_descriptores_bpm() => Assert.Equal("Gasolina", Descriptors.CleanTitle("Gasolina (Extended) 128bpm").Trim());
    [Fact] public void Cdt_separa_pegados() => Assert.Matches("(?i)Original Mix", Descriptors.CleanDbTitle("Cola (Robin Schulz OriginalMix)", ""));
    [Fact] public void Cdt_quita_version() => Assert.Equal("Maneater", Descriptors.CleanDbTitle("Maneater (Radio Version)", "").Trim());

    // --- Clean-Keywords / Build-Kw numericos ---
    [Fact] public void Ck_no_vacia_numerico() => Assert.Equal("404", Descriptors.CleanKeywords("404"));
    [Fact] public void Ck_quita_numero_suelto() => Assert.Equal("Randy", Descriptors.CleanKeywords("Randy 128"));

    // El "ft" solo cuenta como "featuring" si va suelto: dentro de una palabra NO debe cortar.
    [Theory]
    [InlineData("Daft Punk", "Daft Punk")]
    [InlineData("Kraftwerk", "Kraftwerk")]
    [InlineData("Soft Cell", "Soft Cell")]
    [InlineData("Taylor Swift", "Taylor Swift")]
    [InlineData("After Dark", "After Dark")]
    [InlineData("Defeated", "Defeated")]
    public void Ck_no_corta_por_ft_dentro_de_palabra(string input, string expected)
        => Assert.Equal(expected, Descriptors.CleanKeywords(input));

    [Theory]
    [InlineData("Bad Bunny feat. Drake", "Bad Bunny")]
    [InlineData("Bad Bunny ft. Drake", "Bad Bunny")]
    [InlineData("Bad Bunny ft Drake", "Bad Bunny")]
    [InlineData("Bad Bunny featuring Drake", "Bad Bunny")]
    public void Ck_sigue_quitando_el_featuring_de_verdad(string input, string expected)
        => Assert.Equal(expected, Descriptors.CleanKeywords(input));
    [Fact] public void Bk_conserva_numerico() => Assert.Equal("23 Randy", Descriptors.BuildKw("23", "Randy feat Ape Drums"));

    // --- Remove-EditorTags ---
    [Fact] public void Ret_tras_parentesis() => Assert.Equal("Sandstorm (Freejak x Thomas Anthony Remix) Mashup", Descriptors.RemoveEditorTags("Sandstorm (Freejak x Thomas Anthony Remix) Jose Ramos Mashup"));
    [Fact] public void Ret_tras_doble_espacio() => Assert.Equal("Don Omar - Taboo Mashup", Descriptors.RemoveEditorTags("Don Omar - Taboo  Pedro Cabrera Mashup"));
    [Fact] public void Ret_no_toca_dentro_parentesis() => Assert.Equal("Envidia x Monaco (Juanjo Garcia Mashup)", Descriptors.RemoveEditorTags("Envidia x Monaco (Juanjo Garcia Mashup)"));
    [Fact] public void Ret_no_toca_normal() => Assert.Equal("Darude - Sandstorm", Descriptors.RemoveEditorTags("Darude - Sandstorm"));

    // --- ArtistMatch y Remove-RedundantFeat ---
    [Fact] public void Am_casa_inclusion() => Assert.True(Matching.ArtistMatch(TextUtils.Nk("Bad Bunny"), TextUtils.Nk("Bad Bunny, Jhay Cortez")));
    [Fact] public void Am_no_casa() => Assert.False(Matching.ArtistMatch(TextUtils.Nk("Shakira"), TextUtils.Nk("Rosalia")));
    [Fact] public void Rrf_quita_feat_redundante() => Assert.Equal("Callaita", Feat.RemoveRedundantFeat("Callaita (feat. Bad Bunny)", "Bad Bunny").Trim());
    [Fact] public void Rrf_quita_feat_suelto() => Assert.Equal("Aroma", Feat.RemoveRedundantFeat("Aroma feat. Brytiago & Beele", "Brytiago, Lenny Tavarez, Beele").Trim());
    [Fact] public void Rrf_conserva_invitado_real() => Assert.Equal("Cancion feat. Guest Star", Feat.RemoveRedundantFeat("Cancion feat. Guest Star", "Main Artist").Trim());
    [Fact] public void Rrf_deja_invitado_nuevo() => Assert.Equal("Song feat. New Guy", Feat.RemoveRedundantFeat("Song feat. Bad Bunny, New Guy", "Bad Bunny").Trim());

    // --- Sanitize y Format-ETA ---
    [Fact] public void San_quita_ilegales() => Assert.Equal("A B C", TextUtils.Sanitize("A/B: C?"));
    [Fact] public void Eta_minutos() => Assert.Equal("2m 5s", TextUtils.FormatEta(125));
}
