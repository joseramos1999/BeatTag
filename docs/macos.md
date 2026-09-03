# BeatTag en macOS

Estado del port y qué queda. Todo lo que hay aquí está **medido**, no supuesto: sale de ejecutar
las cosas en un Mac real (los runners `macos-latest` de GitHub Actions, gratuitos porque este
repositorio es público).

## Qué funciona ya

En cada commit, un Mac de verdad compila los tres proyectos, pasa **los 554 tests** y genera el
ejecutable para Apple Silicon. Ese trabajo es bloqueante: si se rompe, el commit sale en rojo.

El núcleo entero cruza sin tocarlo — matching, scoring de proveedores, Mp3Gain, comparación de
huellas, EBU R128, listas M3U8, rekordbox, IA local — y la interfaz también, porque Avalonia es
multiplataforma. La publicación cruzada produce un Mach-O arm64 con sus `dylib` de Avalonia y Skia.

Efecto lateral del cambio de `net9.0-windows` a `net9.0`: **el ejecutable de Windows adelgazó de
60,4 a 47,2 MB**, porque arrastraba el runtime de escritorio (WinForms/WPF) que la aplicación nunca
usó.

**Las dos piezas de audio ya están portadas** (`Services/AudioSamples.cs`, `Services/AudioPreview.cs`):

- **Medir el volumen** (`AudioSamples.Abrir`): en Windows sigue exactamente igual, no se ha tocado
  una línea. Fuera de Windows decodifica con **NLayer**, un MP3 en C# puro, sin binario que instalar
  ni empaquetar — se prefirió a ffmpeg porque la biblioteca del autor es MP3 casi en su totalidad
  (14.653 de 14.674 archivos) y así no hace falta distribuir ~80 MB de ffmpeg por plataforma.
  **Verificado contra 60 MP3 reales de la biblioteca del autor: medidas idénticas, bit a bit, a las
  de antes del cambio.** El pico sin recortar se sale de ±1 con NLayer (el códec de Windows recorta
  a 16 bits por el camino); se recorta a mano para que Windows y macOS midan lo mismo y no se
  desentonen con las 6.629 medidas ya guardadas.
- **Escuchar el fragmento** (`AudioPreview`): en Windows sigue exactamente igual (MediaFoundationReader
  + WaveOutEvent, ni una línea tocada). Fuera de Windows usa **`afplay`**, el reproductor de línea de
  comandos que trae macOS de serie — mismo patrón que ya usa la aplicación para fpcalc: proceso
  externo, nada que instalar. `afplay` no sabe empezar a mitad de archivo (solo cuánto reproducir,
  no desde dónde), así que el trozo desde el 25% se decodifica con `AudioSamples` y se escribe a un
  WAV temporal antes de lanzarlo.

Lo que NO se ha podido verificar, porque hace falta un Mac delante: que `afplay` de verdad reproduzca
el WAV y se oiga algo. El recorte en sí (que el WAV salga bien formado, con el trozo correcto) tiene
pruebas deterministas y pasa; lanzar el proceso y que salga sonido por el altavoz, no.

## Qué falta

Cuatro piezas. Todas comparten la misma trampa: **compilan sin quejarse y revientan al usarse**, así
que ni la compilación ni los tests las delatan.

| Pieza | Dónde | Salida propuesta |
|---|---|---|
| Cifrado de credenciales (DPAPI) | `Etiquetador.Core/Dpapi.cs` | Llavero de macOS (`security`) |
| Enviar duplicados a la papelera | `ViewModels/DuplicatesViewModel.cs` | AppleScript o `trash` |
| Instalar Ollama | `Services/OllamaInstaller.cs` | `brew install ollama` |
| Huella acústica | `Etiquetador.Core/Fingerprint.cs` | La build de macOS de Chromaprint |

## Lo que la CI no puede comprobar

Un runner no pincha botones y normalmente no tiene salida de audio. Se queda fuera:

- que la interfaz se vea y responda bien (menús, tablas, selección múltiple),
- que `afplay` reproduzca de verdad el WAV recortado y se oiga algo por el altavoz,
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
- **`InternalsVisibleTo` de `Etiquetador.App` hacia `Etiquetador.Tests`**: para poder probar en
  directo piezas como el recorte de `AudioPreview` sin tener que hacerlas públicas solo por eso.
