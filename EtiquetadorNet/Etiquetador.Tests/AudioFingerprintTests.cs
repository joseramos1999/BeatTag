using Etiquetador.Core.Analysis;

namespace Etiquetador.Tests;

/// <summary>
/// Comparacion de huellas acusticas. Los datos son huellas REALES sacadas con fpcalc de dos
/// canciones distintas de una biblioteca de DJ, no valores inventados: lo que se quiere comprobar
/// es como se comporta con la clase de senal que va a recibir.
/// </summary>
public class AudioFingerprintTests
{
    // a-ha - The Sun Always Shines on T.V. (primeros 20 s)
    private static readonly int[] CancionA = unchecked(new[] { (int)1960847874u,(int)1960913666u,(int)1960984322u,(int)350362883u,(int)350356739u,(int)346227713u,(int)346227713u,(int)81998848u,(int)81998864u,(int)98776080u,(int)31917104u,(int)31917073u,(int)66407697u,(int)131980561u,(int)131964177u,(int)265067857u,(int)265067633u,(int)172866801u,(int)172987601u,(int)168727761u,(int)168727761u,(int)169800849u,(int)169833713u,(int)437745123u,(int)450322147u,(int)461708003u,(int)411314787u,(int)411314787u,(int)147074579u,(int)147209731u,(int)138810881u,(int)139331073u,(int)147658241u,(int)147592979u,(int)147601267u,(int)139212659u,(int)139343810u,(int)139335618u,(int)147719106u,(int)143589842u,(int)143463923u,(int)143486451u,(int)143650291u,(int)165121491u,(int)158698963u,(int)154502611u,(int)191201475u,(int)266703057u,(int)514166993u,(int)1592112305u,(int)1583854768u,(int)2116533408u,(int)2116533409u,(int)2116468129u,(int)2116540083u,(int)2116507315u,(int)2133272226u,(int)2099586722u,(int)2086938338u,(int)2095326754u,(int)2090870034u,(int)2090937602u,(int)2091068674u,(int)1554132227u,(int)1558322435u,(int)484576515u,(int)501419281u,(int)492900113u,(int)467746064u,(int)451091760u,(int)451085616u,(int)452003120u,(int)449910032u,(int)448820528u,(int)448851232u,(int)440462625u,(int)448834913u,(int)448834801u,(int)448832755u,(int)440443123u,(int)436314291u,(int)436723858u,(int)436625554u,(int)436626610u,(int)419839458u,(int)415645282u,(int)411441778u,(int)411978259u,(int)411860483u,(int)411594241u,(int)415819265u,(int)134796817u,(int)134795793u,(int)134828625u,(int)134902739u,(int)139088835u,(int)156107154u,(int)1263342002u,(int)1263281586u,(int)1254567314u,(int)1250303362u,(int)1250297986u,(int)1267141842u,(int)1233394914u,(int)1216619618u,(int)1217667123u,(int)1217679633u,(int)1221951761u,(int)1282507057u,(int)1278390576u,(int)1277329664u,(int)1260814784u,(int)1248232832u,(int)1244106160u,(int)438679984u,(int)438679952u,(int)1512151428u,(int)2049154452u,(int)2049439220u,(int)2049401125u,(int)1780949797u,(int)1794581037u,(int)1790255660u,(int)1787830828u,(int)714089021u,(int)713040429u,(int)713057071u,(int)717287726u,(int)734052398u,(int)725729322u,(int)725734458u,(int)725742602u,(int)721484874u,(int)721484874u,(int)704739419u,(int)705132667u,(int)705067115u,(int)709786987u,(int)718175979u,(int)780845819u,(int)780714714u,(int)713614218u,(int)717857162u,(int)692752522u,(int)940233866u,(int)940243162u,(int)940272106u,(int)2026535466u,(int)2022332969u,(int)2022324796u,(int)4169737740u,(int)3633062668u,(int)3637260572u,(int)3645640748u,(int)3654029357u,(int)2345476463u,(int)2341413734u,(int)2307863270u,(int)2307724007u,(int)2290352871u });

    // Fuego - Una Vaina Loca (acapella). Otra cancion completamente distinta.
    private static readonly int[] CancionB = unchecked(new[] { (int)1647164092u,(int)1647164092u,(int)1647164092u,(int)3794714188u,(int)3861839620u,(int)3861851428u,(int)2771133732u,(int)2758155300u,(int)3031305216u,(int)3031043089u,(int)3031108643u,(int)2628262946u,(int)2628271139u,(int)2628262931u,(int)2624523523u,(int)2285821185u,(int)2287922497u,(int)2363468995u,(int)3751782546u,(int)3738618290u,(int)3730165138u,(int)3725971090u,(int)3726100146u,(int)3725058722u,(int)3728957858u,(int)3728745618u,(int)3729861698u,(int)3746774034u,(int)3581282610u,(int)3577023014u,(int)3581688374u,(int)3581418014u,(int)3585616447u,(int)3333959227u,(int)3331860234u,(int)2258117386u,(int)3348829035u,(int)3281539499u,(int)3246965163u,(int)3251143099u,(int)3243082171u,(int)3276636345u,(int)3276641432u,(int)3271923848u,(int)3271932040u,(int)3272063112u,(int)3273042168u,(int)3273058312u,(int)3241568520u,(int)3292425736u,(int)3300873784u,(int)3299545660u,(int)3299480092u,(int)3299480068u,(int)3299344928u,(int)3299353377u,(int)3303547939u,(int)3295545378u,(int)3291875346u,(int)3289794562u,(int)3255191874u,(int)3254966594u,(int)3254954307u,(int)3267535937u,(int)3267731568u,(int)2193920224u,(int)2190246368u,(int)2189984484u,(int)2189984460u,(int)2190185193u,(int)2462691049u,(int)2450041771u,(int)2450295210u,(int)4062152890u,(int)4060514458u,(int)1913059466u,(int)1918266506u,(int)1645486218u,(int)571744410u,(int)588710058u,(int)557188074u,(int)538841834u,(int)551428810u,(int)547120718u,(int)545566286u,(int)595892743u,(int)583305477u,(int)642029621u,(int)638433332u,(int)638497796u,(int)772710404u,(int)772710404u,(int)2031017996u,(int)2014244908u,(int)3893251432u,(int)3906882152u,(int)3902753384u,(int)3904719464u,(int)3971304040u,(int)3971242617u,(int)2912184026u,(int)3209978554u,(int)4281598634u,(int)4281598890u,(int)4146921642u,(int)3845066938u,(int)4096135354u,(int)4100340874u,(int)4100135050u,(int)4108523722u,(int)3568457803u,(int)3568498745u,(int)3568412936u,(int)2492509960u,(int)2626724360u,(int)2626720264u,(int)3633418808u,(int)3633771052u,(int)2631295533u,(int)2625438247u,(int)2352742946u,(int)2352742754u,(int)2218003554u,(int)3291744482u,(int)3423869106u,(int)3422890113u,(int)3423021184u,(int)3485870272u,(int)3330730176u,(int)3331257408u,(int)3330966560u,(int)2189984801u,(int)2189983777u,(int)2189984033u,(int)2458616339u,(int)2475405842u,(int)2471011938u,(int)2451090086u,(int)2451081894u,(int)3523906238u,(int)3523889806u,(int)4060756622u,(int)1913269134u,(int)1913269406u,(int)1914184874u,(int)1660682410u,(int)548274330u,(int)816722058u,(int)816735434u,(int)812508234u,(int)808231967u,(int)808234029u,(int)808233260u,(int)590121772u,(int)585861676u,(int)581615124u,(int)648716548u,(int)652976140u,(int)908845068u,(int)1059741708u });

    [Fact]
    public void La_misma_huella_es_identica()
        => Assert.Equal(1.0, AudioFingerprint.Similarity(CancionA, CancionA), 3);

    // Dos canciones sin relacion rondan el 0,5: los bits coinciden por azar la mitad de las veces.
    // Es la razon de que el umbral este en 0,85 y no en algo como 0,6.
    [Fact]
    public void Dos_canciones_distintas_rondan_el_azar()
    {
        var s = AudioFingerprint.Similarity(CancionA, CancionB);
        Assert.InRange(s, 0.35, 0.70);
        Assert.False(AudioFingerprint.SameRecording(CancionA, CancionB));
    }

    // El caso que de verdad importa: la misma cancion guardada con la intro recortada. Sin buscar
    // el desplazamiento, esto se veria como dos canciones distintas.
    [Theory]
    [InlineData(15)]
    [InlineData(40)]
    [InlineData(70)]
    public void La_misma_cancion_desplazada_se_reconoce(int recorte)
    {
        var recortada = CancionA[recorte..];
        Assert.True(AudioFingerprint.SameRecording(CancionA, recortada),
            $"desplazada {recorte} elementos, deberia seguir siendo la misma");
    }

    // Una recodificacion cambia bits sueltos. Debe seguir reconociendose.
    [Fact]
    public void Cambiar_bits_sueltos_no_rompe_el_reconocimiento()
    {
        var ruido = (int[])CancionA.Clone();
        var r = new Random(7);
        for (var i = 0; i < ruido.Length; i++)
            ruido[i] ^= 1 << r.Next(32);     // un bit distinto por elemento
        var s = AudioFingerprint.Similarity(CancionA, ruido);
        Assert.True(s >= AudioFingerprint.UmbralIgual, $"un bit por elemento no deberia romperlo, y sale {s:F3}");
    }

    [Fact]
    public void Huellas_vacias_no_rompen()
    {
        Assert.Equal(0.0, AudioFingerprint.Similarity(null, CancionA));
        Assert.Equal(0.0, AudioFingerprint.Similarity(System.Array.Empty<int>(), CancionA));
    }

    // Guarda: un fragmento minusculo no basta para afirmar que son la misma grabacion.
    [Fact]
    public void Un_solape_minusculo_no_cuenta()
        => Assert.Equal(0.0, AudioFingerprint.Similarity(CancionA[..5], CancionB));
}
