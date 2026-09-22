# 🎧 BeatTag

**Etiquetador y organizador de bibliotecas musicales para DJs.** Identifica tus canciones contra
varias fuentes, completa los tags (título, artista, álbum, año, género, BPM, clave), propone un
nombre de archivo limpio y consistente, y te deja aplicarlo por lotes — con **deshacer** para cada
cambio.

Reescritura en **C# / .NET 9 + Avalonia** de la app original en PowerShell. Windows.

> ⚠️ Toda operación que escribe en tus archivos es **reversible**: cada tirada genera un manifiesto
> de deshacer que restaura tags y nombres exactamente como estaban.

---

## ✨ Qué hace

- **Enriquecer**: identifica cada tema contra **Deezer, iTunes, Spotify, MusicBrainz, Discogs y
  AcoustID** (huella acústica), con **IA local opcional** para interpretar nombres muy alterados.
  Propone tags y un nombre de archivo, y puntúa la **confianza** de cada propuesta (incluida la
  coherencia con los tags que ya trae el archivo). Las propuestas dudosas se auto-desmarcan para
  que las revises. Cada propuesta lleva un **índice de cambio de 0 a 10**: lo que apenas cambiaría
  el archivo se aparta de la lista, para que solo tengas delante lo que merece un vistazo. Y la
  columna **«Por qué»** explica en claro qué hizo ganar a cada coincidencia, para poder decidir
  sobre las dudosas sin salir de la aplicación.
- **Buscador en cada tabla**: filtra por palabras sueltas y en cualquier orden, sin acentos ni
  mayúsculas. Selección múltiple con Ctrl+clic y Mayús+clic para marcar, aplicar o descartar por
  bloques.
- **Editor**: edición manual de título y tags, con reproducción de un fragmento para comprobar.
- **Duplicados**: agrupa copias (por artista+título, solo título, o +duración) y **marca en verde la
  que conviene conservar**, según el criterio que elijas: carpeta prioritaria, mejor calidad o mayor
  duración. Puedes marcar las sobrantes de todos los grupos a la vez y enviarlas a la papelera de
  una sola pasada, conservando siempre un ejemplar de cada canción.
  Con **«Excluir ediciones de DJ»** se dejan fuera del análisis las ediciones hechas para pinchar
  (hype intro, open show, aca out, acapellas, transiciones, mashups): no son copias sobrantes, y
  emparejadas con su original tapan los duplicados de verdad.
  También puede agrupar **por el audio** (huella acústica), lo que encuentra la misma canción
  aunque tenga títulos distintos o no esté etiquetada. En ese modo cada versión conserva su propio
  ejemplar: tus ediciones de DJ no se marcan como sobrantes.
- **Calidad**: clasifica el audio por bitrate/formato y filtra el de baja calidad.
- **Incompletas / No encontradas**: temas a los que les falta algún tag o que ninguna fuente
  identifica.
- **Volumen**: mide la sonoridad de cada grabación según **EBU R128** y corrige las que se apartan
  del resto. El audio **no se recodifica** —se ajusta la ganancia del propio MP3—, así que no hay
  pérdida de calidad, los archivos mantienen su tamaño y el cambio es reversible. Ese ajuste sin
  pérdida solo existe para MP3: un FLAC, un WAV o un M4A **se miden pero no se tocan**.
- **Tendencias**: el **chart diario de Spotify** de 77 países (y el Global), marcando lo que ya
  tienes en la biblioteca. Deezer queda como fuente alternativa.
- **Tonalidad**: muestra la clave de cada tema y su código **Camelot**, para mezclar en armónico.
  Se lee de los tags (la escriben rekordbox y similares); BeatTag no la deduce del audio.
- **Listas M3U8**: exporta lo que hayas filtrado a una lista que abren rekordbox, Engine DJ o Serato.
- **Bandeja de entrada**: una carpeta aparte donde dejas la música recién descargada. Antes de que
  entre en la biblioteca, avisa si ya la tienes (o tienes otra versión), si está repetida, si su
  calidad es baja o si le faltan etiquetas. Cada canción pasa por *recibida → analizada → revisada →
  preparada*, y las preparadas se mueven a la biblioteca sin sobrescribir nada y con deshacer.
- **Ficha DJ**: lo que sabes de cada canción y no cabe en los tags — energía (1-10), momento de la
  sesión, voz, letra limpia o explícita, idioma, ambiente, etiquetas libres y «arma secreta». Se
  guarda en BeatTag sin tocar los archivos; se rellena por lotes (editar cuarenta a la vez no borra lo
  que cada una tenía) y con las teclas 1-0 para la energía. Opcionalmente se vuelca al comentario del
  archivo, conservando lo que ya hubiera.
- **Colecciones**: listas que se rellenan solas a partir de condiciones (género, BPM, años, energía,
  momento, voz, letra, idioma, etiquetas) y se exportan a M3U8.
- **Asistente IA**: herramientas con la IA local — buscar con una frase («bachata romántica de los
  2000 para cerrar»), proponer idioma y voz para las fichas, unificar géneros escritos de formas
  distintas, renombrar archivos con nombre sucio, completar nombres cortados a medias y ordenar una
  colección para mezclar (tono Camelot, tempo y energía). Ver [IA local](#-ia-local-opcional).
- **Estadísticas**: reparto de la biblioteca por BPM, género, calidad, década y Clean/Explícito.
- **Importar rekordbox**: trae BPM y clave musical desde un XML de rekordbox.
- **Caché persistente** en tres niveles (respuestas de red, escaneo y análisis): no se reprocesa lo
  ya hecho salvo que lo pidas.

### Mezclas y mashups

No se identifican: no existen como lanzamiento, así que buscarlos en el catálogo solo puede dar una
identificación equivocada. Se detectan por el nombre del archivo y **por la carpeta**: si el nombre
de una carpeta contiene «mashup», se salta todo lo que hay dentro, incluidas sus subcarpetas.

Si pides reanalizar una canción concreta, se busca igualmente — cuando lo pides expresamente, mandas
tú.

## 📥 Descargar

La versión compilada (`BeatTag.exe`, ejecutable único y autocontenido) se publica en la página de
**[Releases](../../releases)**. Descárgala y ejecútala — no requiere instalar .NET.

Requisitos: **Windows 10/11 (x64)**.

## 🔑 Claves de API (opcional)

BeatTag funciona con Deezer e iTunes sin configuración. Para usar **Spotify, Discogs o AcoustID**,
introduce tus claves en la pestaña **Ajustes**. Se guardan **cifradas** en tu equipo con DPAPI
(Windows Data Protection, por usuario) — **nunca** salen del equipo ni se incluyen en el repo.

Los datos de la app (config, cachés, logs) se guardan en
`Documentos\Etiquetador de Musica\`.

## 🤖 IA local (opcional)

Cuando ninguna fuente identifica un tema, un modelo de lenguaje puede interpretar nombres de archivo
muy alterados (etiquetas de record pool, BPM, tonalidad, nombres de editor) y convertirlos en una
búsqueda aprovechable.

Se ejecuta **en tu propio equipo** mediante [Ollama](https://ollama.com): **no necesita clave** y no
envía información a ningún servicio externo. Desde **Ajustes** puedes instalarlo, descargar un modelo
y elegir cuál usar; si Ollama está instalado pero parado, BeatTag lo arranca solo.

Su propuesta **siempre se contrasta contra el catálogo** antes de escribir nada, de modo que una
invención del modelo no llega a tus archivos. Es opcional: sin ella, el resto del análisis funciona
igual.

También decide los casos que ninguna regla resuelve: cuando el nombre une varias canciones o varios
artistas con una «x», la IA distingue el mashup de la colaboración y aparta el primero.

Y cuando entiende el nombre pero **ningún catálogo lo confirma**, la propuesta no se tira: aparece en
**No encontradas**, en la columna «Sugerencia de la IA», para que la revises y la apliques de un clic
si es correcta. Antes se filtra lo que el modelo se inventa, así que solo se sugiere lo que reordena
o completa datos que ya estaban en el nombre o en los tags.

La pestaña **Asistente IA** reúne las herramientas que usan el modelo sin pasar por un catálogo.
Todas siguen la misma norma: **la IA propone, las reglas y tu biblioteca comprueban, y tú decides**.
Cada una se midió con bibliotecas reales antes de construirla, y la ayuda de la aplicación dice
cuánto acierta:

- **Buscar con una frase**: lo seguro (géneros y artistas que tienes, BPM, décadas, «sin
  palabrotas») se lee con reglas; la IA solo añade condiciones que justifica con tus palabras, y
  salen desmarcadas.
- **Proponer fichas**: solo idioma y voz (acierta el idioma en unas 7 de cada 10). No propone la
  energía, porque no sabe estimarla.
- **Unificar géneros**: las grafías conocidas se resuelven con reglas; la IA solo puede elegir entre
  géneros que ya usas, nunca inventar ni vaciar uno, y sus propuestas salen desmarcadas.
- **Renombrar con IA**: solo para nombres sucios. Cada propuesta pasa por comprobaciones (el record
  pool o el editor como artista, mashups deformados, palabras inventadas) y lo que no se puede
  comprobar se avisa. Unas 3 de cada 4 propuestas son buenas: todo sale desmarcado.
- **Completar nombres cortados**: los record pools recortan muchos nombres a una longitud fija
  («… (Rodri Gomez & Adr»). La mayoría se completan con las **etiquetas del propio archivo**, sin
  IA; la IA solo entra en los que no las traen, y su propuesta se contrasta con el catálogo. Solo se
  **añade lo que falta al final**: lo que ya estaba escrito se conserva tal cual, acentos incluidos,
  y nunca se antepone el artista ni se mete la publicidad del pack. Si el resultado seguiría
  cortado, no se propone nada.

Lo que escriben en tus archivos (géneros y nombres) se puede deshacer. Nada se aplica sin que lo
marques.

## 🎛️ rekordbox y los cue points

rekordbox guarda los cue points y el beatgrid en su base de datos, **ligados a la ruta del archivo**.
Al renombrar, los da por perdidos — y renombrar es justo lo que hace BeatTag.

Por eso cada renombrado queda anotado, y desde **Ajustes → Reparar colección de rekordbox** puedes
devolvérselos: exporta tu colección desde rekordbox, repárala y vuelve a importarla. El archivo
original no se modifica.

## 👤 Nombres de artista

Dos listas ampliables desde **Ajustes**, en `Documentos\Etiquetador de Musica\`:

- **Alias** (`ArtistAliases.json`): nombres distintos de un mismo artista, para que una coincidencia
  correcta no se descarte. Un artista puede figurar en el catálogo con un nombre anterior.
- **Grafías** (`ArtistsExceptions.json`): nombres cuya escritura no debe alterarse (`deadmau5`,
  `AC/DC`, `Tiësto`).

## 🛠️ Compilar desde el código

Necesitas el [SDK de .NET 9](https://dotnet.microsoft.com/download).

```bash
cd EtiquetadorNet

# Ejecutar en desarrollo
dotnet run --project Etiquetador.App

# Tests
dotnet test

# Publicar el ejecutable unico (self-contained, win-x64)
dotnet publish Etiquetador.App/Etiquetador.App.csproj -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true -o publicado
```

## 🧱 Estructura

| Proyecto | Rol |
|---|---|
| `Etiquetador.Core` | Lógica: proveedores, matching/scoring, pipeline, cachés, deshacer. Sin UI. |
| `Etiquetador.App`  | Interfaz Avalonia (MVVM con CommunityToolkit.Mvvm). |
| `Etiquetador.Tests`| Tests unitarios (xUnit) del Core. |

**Stack:** .NET 9 · Avalonia 12 · CommunityToolkit.Mvvm · TagLibSharp · NAudio · xUnit.

## 🤝 Contribuir

Los *issues* y *pull requests* son bienvenidos. Antes de enviar un PR, asegúrate de que
`dotnet test` pasa en verde.

## 📄 Licencia

[MIT](LICENSE).
