using System.Diagnostics;

namespace Etiquetador.Tests;

/// <summary>
/// Un programa de mentira que NO termina por su cuenta: espera a que la prueba lo suelte y solo
/// entonces deja una marca y sale. Sirve para comprobar que algo lo paró (un plazo, una cancelación)
/// sin depender de cuánto tarda en pararse.
///
/// Antes se usaba uno que terminaba solo a los ~2 s dejando la marca, y «no hay marca» quería decir
/// «se le paró a tiempo». Con la batería en marcha eso fallaba a ratos: los temporizadores del plazo
/// y de la cancelación corren en hilos del pool, y con el pool ocupado llegan tarde. MEDIDO en
/// octubre de 2026 en 4 núcleos (como el equipo Windows de la CI): un plazo de 500 ms se disparó a
/// los 1.369 ms y una cancelación pedida para los 400 ms llegó a los 1.329; lanzar el programa costó
/// hasta 700 ms con la CPU ocupada. Si todo eso sumaba más de lo que el programa tardaba en acabar,
/// «matar» lo encontraba ya terminado y la prueba lo daba por vivo. Matar el árbol en sí tardaba
/// 28-157 ms.
///
/// Este no acaba hasta que se le suelta: si lo han matado, la marca no aparece nunca; si sigue vivo,
/// al soltarlo la deja en un segundo. Por si una prueba se rompe antes de soltarlo, se rinde solo a
/// los dos minutos sin dejar marca: más que el minuto de tope de las pruebas y que el plazo de
/// fpcalc, y no tanto como para que la batería espere mucho a un programa vivo al terminar.
/// </summary>
internal sealed class ProgramaQueEspera
{
    /// <summary>Lo que se lanza: un .cmd en Windows y un guion con permiso de ejecución fuera.</summary>
    public string Ruta { get; }

    private readonly string _suelta;
    private readonly string _marca;

    public ProgramaQueEspera(string dir, string nombre)
    {
        _suelta = Path.Combine(dir, "suelta");
        _marca = Path.Combine(dir, "marca");
        // Rutas relativas a la carpeta del propio guion: nada que entrecomillar.
        if (OperatingSystem.IsWindows())
        {
            Ruta = Path.Combine(dir, nombre + ".cmd");
            File.WriteAllText(Ruta,
                "@echo off\r\n" +
                "cd /d \"%~dp0\"\r\n" +
                "for /l %%i in (1,1,120) do (\r\n" +
                "  if exist suelta goto suelto\r\n" +
                "  ping -n 2 127.0.0.1 >nul\r\n" +
                ")\r\n" +
                "exit /b 1\r\n" +
                ":suelto\r\n" +
                "echo listo>> marca\r\n");
        }
        else
        {
            Ruta = Path.Combine(dir, nombre);
            File.WriteAllText(Ruta,
                "#!/bin/sh\n" +
                "cd \"$(dirname \"$0\")\"\n" +
                "i=0\n" +
                "while [ ! -e suelta ] && [ $i -lt 600 ]; do sleep 0.2; i=$((i+1)); done\n" +
                "[ -e suelta ] && echo listo >> marca\n");
#pragma warning disable CA1416   // solo se llega aquí fuera de Windows
            File.SetUnixFileMode(Ruta, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        }
    }

    public ProcessStartInfo Arranque() => new(Ruta);

    /// <summary>
    /// Lo suelta y espera: si quedaba vivo, en este rato deja la marca. Los 3,5 s solo pueden dejar
    /// pasar un programa vivo con el equipo muy cargado, nunca suspender una prueba buena: un programa
    /// muerto no deja la marca por mucho que se espere.
    /// </summary>
    public async Task<bool> SiguioVivoAsync()
    {
        File.WriteAllText(_suelta, "");
        await Task.Delay(3500);
        return File.Exists(_marca);
    }
}
