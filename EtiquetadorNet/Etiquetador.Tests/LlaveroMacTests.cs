using Etiquetador.Core;

namespace Etiquetador.Tests;

/// <summary>
/// La clave del Llavero de macOS: cuándo se lee, cuándo se crea y cuándo se falla.
///
/// Se prueba con un `security` simulado, así que corre también en Windows. Visto en revisión: la
/// lectura trataba cualquier código distinto de 0 como «no existe» -un Llavero bloqueado tras un
/// reposo, o cancelar el aviso de desbloqueo- y entonces se creaba una clave nueva con «-U», que
/// SUSTITUYE la existente: todas las credenciales cifradas con la buena quedaban ilegibles.
/// </summary>
public class LlaveroMacTests
{
    /// <summary>Un `security` de mentira: responde lo que se le diga y apunta qué se le pidió.</summary>
    private sealed class SecurityFalso
    {
        public readonly List<string> Llamadas = new();
        public readonly Queue<ResultadoSecurity> Lecturas = new();
        public ResultadoSecurity AlGuardar = new(0, "", "");

        public ResultadoSecurity Ejecutar(string argumentos)
        {
            Llamadas.Add(argumentos);
            if (argumentos.StartsWith("find-generic-password", StringComparison.Ordinal))
                return Lecturas.Count > 0 ? Lecturas.Dequeue() : new ResultadoSecurity(LlaveroMac.CodigoNoExiste, "", "");
            if (argumentos.StartsWith("add-generic-password", StringComparison.Ordinal))
            {
                if (AlGuardar.Codigo == 0)
                {
                    // Lo guardado se puede leer después, como en el Llavero de verdad.
                    var b64 = argumentos.Split(' ')[argumentos.Split(' ').ToList().IndexOf("-w") + 1];
                    Lecturas.Enqueue(new ResultadoSecurity(0, b64 + "\n", ""));
                }
                return AlGuardar;
            }
            return new ResultadoSecurity(1, "", "orden inesperada");
        }

        public bool IntentoCrear => Llamadas.Any(l => l.StartsWith("add-generic-password", StringComparison.Ordinal));
    }

    private static string Clave(byte relleno) => Convert.ToBase64String(Enumerable.Repeat(relleno, 32).ToArray());

    // El caso del hallazgo: Llavero bloqueado («User interaction is not allowed», código 36). Nada
    // de crear otra clave: se falla, y AppConfig lo convierte en credenciales ilegibles esta sesión.
    [Theory]
    [InlineData(36, "security: SecKeychainSearchCopyNext: User interaction is not allowed.")]
    [InlineData(128, "security: SecKeychainSearchCopyNext: User canceled the operation.")]
    public void Con_el_llavero_bloqueado_no_se_crea_otra_clave(int codigo, string error)
    {
        var security = new SecurityFalso();
        security.Lecturas.Enqueue(new ResultadoSecurity(codigo, "", error));

        Assert.Throws<InvalidOperationException>(() => LlaveroMac.ObtenerOCrear(security.Ejecutar));
        Assert.False(security.IntentoCrear);
    }

    [Fact]
    public void Si_existe_se_usa_y_no_se_crea()
    {
        var security = new SecurityFalso();
        security.Lecturas.Enqueue(new ResultadoSecurity(0, Clave(7) + "\n", ""));

        var clave = LlaveroMac.ObtenerOCrear(security.Ejecutar);

        Assert.Equal(Enumerable.Repeat((byte)7, 32), clave);
        Assert.False(security.IntentoCrear);
    }

    // Solo con «no existe» (44) se crea, y SIN -U: nunca puede sustituir una clave que ya esté.
    [Fact]
    public void Solo_si_no_existe_se_crea_y_sin_sustituir()
    {
        var security = new SecurityFalso();
        security.Lecturas.Enqueue(new ResultadoSecurity(LlaveroMac.CodigoNoExiste, "", "The specified item could not be found in the keychain."));

        var clave = LlaveroMac.ObtenerOCrear(security.Ejecutar);

        Assert.Equal(32, clave.Length);
        var crear = Assert.Single(security.Llamadas, l => l.StartsWith("add-generic-password", StringComparison.Ordinal));
        Assert.DoesNotContain(" -U", crear);
    }

    // Dos instancias a la vez: la segunda se encuentra la clave ya creada (45) y usa esa.
    [Fact]
    public void Si_otra_instancia_la_creo_a_la_vez_se_usa_la_suya()
    {
        var security = new SecurityFalso { AlGuardar = new ResultadoSecurity(LlaveroMac.CodigoYaExiste, "", "The specified item already exists in the keychain.") };
        security.Lecturas.Enqueue(new ResultadoSecurity(LlaveroMac.CodigoNoExiste, "", ""));
        security.Lecturas.Enqueue(new ResultadoSecurity(0, Clave(9), ""));

        var clave = LlaveroMac.ObtenerOCrear(security.Ejecutar);

        Assert.Equal(Enumerable.Repeat((byte)9, 32), clave);
    }

    // Existe pero no es una clave nuestra: sustituirla dejaría ilegible lo cifrado con ella.
    [Theory]
    [InlineData("esto no es base64")]
    [InlineData("AAAA")]   // base64 válido, pero no de 32 bytes
    public void Una_clave_danada_no_se_sustituye(string salida)
    {
        var security = new SecurityFalso();
        security.Lecturas.Enqueue(new ResultadoSecurity(0, salida, ""));

        Assert.Throws<InvalidOperationException>(() => LlaveroMac.ObtenerOCrear(security.Ejecutar));
        Assert.False(security.IntentoCrear);
    }

    [Fact]
    public void Si_no_se_puede_guardar_se_falla()
    {
        var security = new SecurityFalso { AlGuardar = new ResultadoSecurity(51, "", "Write permissions error.") };

        Assert.Throws<InvalidOperationException>(() => LlaveroMac.ObtenerOCrear(security.Ejecutar));
    }
}
