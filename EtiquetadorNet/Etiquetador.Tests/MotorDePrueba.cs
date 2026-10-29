using System.Reflection;
using System.Runtime.CompilerServices;
using Etiquetador.App.Services;
using Etiquetador.Core;
using Etiquetador.Core.Dj;
using Etiquetador.Core.Pipeline;
using Etiquetador.Core.Providers;

namespace Etiquetador.Tests;

/// <summary>
/// El motor real sin su constructor (que solo trabaja sobre la carpeta de datos de Documentos), con
/// las cachés de audio, la configuración y las colas que usan SeguirArchivo, los guardados y las
/// pestañas que se prueban con él (Volumen, Calidad, Ajustes).
/// </summary>
internal static class MotorDePrueba
{
    /// <param name="datos">Carpeta de datos (temporal) donde viven las cachés.</param>
    /// <param name="log">Registro.</param>
    /// <param name="biblioteca">La biblioteca, si la prueba la necesita.</param>
    public static AppEngine Crear(string datos, Logger log, LibraryStore? biblioteca = null)
    {
        var motor = (AppEngine)RuntimeHelpers.GetUninitializedObject(typeof(AppEngine));
        void Poner(string campo, object valor)
        {
            var f = typeof(AppEngine).GetField(campo, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(f != null, $"AppEngine ya no tiene el campo {campo}: actualiza MotorDePrueba");
            f!.SetValue(motor, valor);
        }
        var paths = new AppPaths(datos);
        var api = new ApiClient(paths);
        var huellas = new Fingerprint(paths, log);
        Poner("<Paths>k__BackingField", paths);
        Poner("<Logger>k__BackingField", log);
        Poner("<Avisos>k__BackingField", new AvisosApp());
        Poner("<Config>k__BackingField", new AppConfig());
        Poner("<Api>k__BackingField", api);
        Poner("<AudioAceptadas>k__BackingField", new IgnoreList(paths.AudioAceptadasPath));
        Poner("<Loudness>k__BackingField", new LoudnessScanner(paths.LoudnessCachePath, log, paths.VolumenAplicadoPath));
        Poner("<Fingerprints>k__BackingField", new FingerprintScanner(paths.FingerprintCachePath, huellas.FpcalcPath, log));
        Poner("<Identificacion>k__BackingField", new IdentificationScanner(paths.IdentificacionCachePath, huellas, new AcoustIdProvider(api),
                                                                          new AuddProvider(new HttpClient(), log), new HttpClient(), log));
        Poner("<Analysis>k__BackingField", new AnalysisCache(paths.AnalysisCachePath));
        Poner("<Marks>k__BackingField", new ApplyMarks(paths.ApplyMarksPath));
        Poner("<Applied>k__BackingField", new IgnoreList(paths.AppliedPath));
        Poner("<Ignored>k__BackingField", new IgnoreList(paths.IgnoredPath));
        Poner("_cerrojoSustituciones", new object());
        Poner("_sustituciones", new AlmacenSustituciones(AlmacenSustituciones.RutaEn(paths.DataDir)));
        Poner("_cerrojoHuellas", new object());
        Poner("_colaHuellas", Task.CompletedTask);
        Poner("_cerrojoEspectro", new object());
        Poner("_cerrojoCargaEspectro", new object());
        Poner("_relojPunto", System.Diagnostics.Stopwatch.StartNew());
        if (biblioteca != null) Poner("<Library>k__BackingField", biblioteca);
        motor.EnLaInterfaz = a => a();   // en la aplicación, Dispatcher.UIThread.Post
        return motor;
    }
}
