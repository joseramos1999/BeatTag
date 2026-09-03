# BeatTag en macOS

Estado del port y qué queda. Todo lo que hay aquí está **medido**, no supuesto: sale de ejecutar
las cosas en un Mac real (los runners `macos-latest` de GitHub Actions, gratuitos porque este
repositorio es público).

## Qué funciona ya

En cada commit, un Mac de verdad compila los tres proyectos, pasa **los 547 tests** y genera el
ejecutable para Apple Silicon. Ese trabajo es bloqueante: si se rompe, el commit sale en rojo.

El núcleo entero cruza sin tocarlo — matching, scoring de proveedores, Mp3Gain, comparación de
huellas, EBU R128, listas M3U8, rekordbox, IA local — y la interfaz también, porque Avalonia es
multiplataforma. La publicación cruzada produce un Mach-O arm64 con sus `dylib` de Avalonia y Skia.

Efecto lateral del cambio de `net9.0-windows` a `net9.0`: **el ejecutable de Windows adelgazó de
60,4 a 47,2 MB**, porque arrastraba el runtime de escritorio (WinForms/WPF) que la aplicación nunca
usó.

## Qué falta

Seis piezas. Todas comparten la misma trampa: **compilan sin quejarse y revientan al usarse**, así
que ni la compilación ni los tests las delatan.

| Pieza | Dónde | Salida propuesta |
|---|---|---|
| Cifrado de credenciales (DPAPI) | `Etiquetador.Core/Dpapi.cs` | Llavero de macOS (`security`) |
| Decodificar audio para medir volumen | `Services/LoudnessScanner.cs` | ffmpeg como proceso externo |
| Reproducir el fragmento de prueba | `Services/AudioPreview.cs` | ffmpeg / LibVLC |
| Enviar duplicados a la papelera | `ViewModels/DuplicatesViewModel.cs` | AppleScript o `trash` |
| Instalar Ollama | `Services/OllamaInstaller.cs` | `brew install ollama` |
| Huella acústica | `Etiquetador.Core/Fingerprint.cs` | La build de macOS de Chromaprint |

Las dos de audio son la mitad del trabajo y se resuelven juntas con ffmpeg, que además es el patrón
que la aplicación **ya usa** para fpcalc: proceso externo, salida por tubería. NAudio ahí no tiene
salida: `MediaFoundationReader` es de Windows y punto.

## Lo que la CI no puede comprobar

Un runner no pincha botones. Se queda fuera:

- que la interfaz se vea y responda bien (menús, tablas, selección múltiple),
- que Gatekeeper deje abrir el `.app` sin firmar — dirá *«Apple no puede comprobarlo»* y habrá que
  abrirlo con clic derecho la primera vez,
- firmar y notarizar de verdad, que exige cuenta de desarrollador de Apple y un Mac.

Para eso hace falta un Mac delante **una vez, al final**. No para desarrollar, solo para el repaso.

## Decisiones tomadas por el camino

- **Rutas en las pruebas**: `Etiquetador.Tests/Rutas.cs`. Muchas pruebas usaban `@"C:\m\x.mp3"` a
  pelo; en Unix la barra invertida no separa carpetas, así que fallaban por el sistema operativo y
  no por lo que comprobaban. El producto no tenía ese problema.
- **`[WindowsFact]`** y **`[DpapiFact]`**: para reglas que son de Windows de verdad (que `"A: B.mp3"`
  se rechace por parecer una unidad) y para lo que necesita DPAPI. Se marcan como OMITIDAS fuera de
  Windows, nunca como falsamente verdes.
- **Los fallos de los tests se publican como anotaciones** del run: los logs completos de Actions
  exigen permisos de administrador para leerse, y las anotaciones no.
