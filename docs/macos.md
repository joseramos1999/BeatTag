# BeatTag en macOS

Estado del port y qué queda. Todo lo que hay aquí está **medido**, no supuesto: sale de ejecutar
las cosas en un Mac real (los runners `macos-latest` de GitHub Actions, gratuitos porque este
repositorio es público).

## Qué funciona ya

En cada commit, un Mac de verdad compila los tres proyectos, pasa **los 566 tests** y genera el
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

**Las otras cuatro piezas también están portadas:**

- **Credenciales** (`Etiquetador.Core/Secretos.cs`, `LlaveroMac.cs`). En Windows sigue siendo DPAPI,
  sin tocar: los `config.net.json` existentes se leen exactamente igual. En macOS se cifra con
  AES-GCM usando una clave de 32 bytes guardada en el **Llavero del usuario**. Se eligió el Llavero
  y no un archivo de clave con permisos restringidos porque solo el Llavero da la misma garantía que
  DPAPI: el secreto va ligado a la sesión, no a un archivo que se pueda copiar junto con la
  configuración. Se guarda una sola clave y con ella se cifra todo, así el Llavero se consulta una
  vez por sesión en lugar de una vez por credencial.
  Si el cifrado no está disponible, **se lanza excepción y el guardado falla con un mensaje visible**;
  nunca se escriben las claves en claro «para que al menos funcione».
  Una configuración cifrada en el otro sistema se marca como ilegible (`CryptoError`), no como texto
  en claro: devolver el churro hexadecimal como si fuera la credencial sería lo peor de todo.
- **Papelera** (`ViewModels/DuplicatesViewModel.cs`). Se intenta por Finder vía `osascript`, que es
  quien lo hace bien: respeta el disco donde vive el archivo —importante, porque una biblioteca de DJ
  suele estar en un disco externo— y deja el «Volver a poner». La primera vez macOS pedirá permiso
  para controlar Finder; si se deniega, se mueve a `~/.Trash` a mano, con nombre libre para no pisar
  lo que ya hubiera allí. **Nunca se borra de verdad**: si ambos caminos fallan, el archivo se queda
  donde está y se informa del error.
- **Instalar Ollama** (`Services/OllamaInstaller.cs`). `brew install ollama` en macOS, winget en
  Windows. `brew` se busca también en `/opt/homebrew/bin` y `/usr/local/bin`, porque no siempre está
  en el PATH que hereda una aplicación de escritorio.
- **Huella acústica** (`Etiquetador.Core/Fingerprint.cs`). Descarga la build **universal** de
  Chromaprint 1.5.1 (vale para Apple Silicon e Intel), con su SHA-256 comprobado igual que el de
  Windows, extrae el `.tar.gz`, valida la cabecera Mach-O y le pone permiso de ejecución — sin eso
  el binario no arranca en Unix. El ejecutable pasa a llamarse `fpcalc`, sin extensión.

## Qué falta

Nada de código conocido. Lo que queda es **comprobarlo en un Mac de verdad**.

## Cómo conseguir el .app para probarlo

Cada commit de `main` deja el paquete listo como artefacto de la ejecución de CI:

1. Entrar en **[Actions](https://github.com/joseramos1999/BeatTag/actions)** → la última ejecución
   de *CI* en verde.
2. Abajo del todo, en **Artifacts**, descargar **`BeatTag-macos-arm64`**.
3. Descomprimir dos veces: GitHub envuelve el artefacto en un `.zip`, y dentro está el
   `BeatTag-macos-arm64.tar.gz` que contiene `BeatTag.app`.

Hace falta haber iniciado sesión en GitHub: los artefactos no se descargan de forma anónima aunque
el repositorio sea público.

**Por qué va en `.tar.gz` y no como carpeta suelta:** el `.zip` que genera Actions pierde los
permisos de Unix, y sin el bit de ejecución el `.app` no arranca de ninguna manera. El `.tar.gz` los
conserva.

**La primera vez, Gatekeeper lo bloqueará.** La aplicación no está firmada ni notarizada (eso exige
cuenta de desarrollador de Apple), y además todo lo descargado del navegador llega en cuarentena.
macOS dirá *«no se puede abrir porque Apple no puede comprobar que no contenga malware»*. Para
abrirlo igualmente:

```bash
xattr -dr com.apple.quarantine BeatTag.app   # quita la cuarentena
open BeatTag.app
```

O, sin tocar el Terminal: clic derecho sobre `BeatTag.app` → **Abrir** → *Abrir* en el aviso.

Es solo arm64 (Apple Silicon). Para un Mac Intel habría que añadir `osx-x64` a la compilación.

## Lo que la CI no puede comprobar

Un runner no pincha botones, no tiene salida de audio y su sesión no es la de un usuario de verdad.
Se queda fuera:

- que la interfaz se vea y responda bien (menús, tablas, selección múltiple),
- que `afplay` reproduzca de verdad el WAV recortado y se oiga algo por el altavoz,
- que el **Llavero** guarde y devuelva la clave (hace falta una sesión con llavero desbloqueado),
- que **Finder** mueva el archivo a la papelera, incluido el permiso que macOS pide la primera vez,
- que `brew install ollama` termine bien,
- que la descarga de fpcalc se extraiga y **arranque** con el permiso de ejecución que se le pone,
- que Gatekeeper deje abrir el `.app` sin firmar — dirá *«Apple no puede comprobarlo»* y habrá que
  abrirlo con clic derecho la primera vez,
- firmar y notarizar de verdad, que exige cuenta de desarrollador de Apple y un Mac.

Para eso hace falta un Mac delante **una vez, al final**. No para desarrollar, solo para el repaso.
Esa lista es exactamente el guion de esa sesión.

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
