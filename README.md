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
  sobre las dudosas sin salir de la aplicación. Con **«Saltar las que ya están bien»** (activada de
  serie) no se consultan las canciones cuyo nombre y tags ya coinciden y no les falta nada de lo
  que se escribe: en una biblioteca real, la mitad. Las ediciones de DJ conservan su versión
  (también las que van entre corchetes y las acapellas abreviadas: «In Acp», «IOAcp») y un
  mashup nunca se renombra como uno de sus temas. Se puede **analizar solo una carpeta** (o una
  subcarpeta) sin desmarcar las demás. Cuando Deezer identifica una canción, se aprovecha su marca
  de **letra explícita** y, si lo activas en Ajustes, se escribe su **ISRC** (solo si el archivo es
  exactamente esa grabación).
- **Historial de cambios**: todas las ejecuciones que han escrito en tus archivos (propuestas,
  Editor, Volumen, géneros, renombrados, Bandeja, herramientas del Asistente…), con su tipo y su
  estado, y un archivo por fila. Cada una se puede deshacer entera o solo en las canciones que
  elijas, aunque haya otras después; antes de deshacer se revisa qué tocaron las ejecuciones
  posteriores y se explica qué va a pasar. Al deshacer, las ejecuciones posteriores siguen al
  archivo si vuelve a su nombre anterior, y su huella acústica y lo que respondió «Comprobar audio»
  siguen valiendo. Los procesos largos guardan lo hecho por tramos, así que un cierre inesperado no
  pierde la tirada entera.
- **Menú contextual de la Biblioteca** (sobre una o varias canciones): **buscar compatibles** (las
  que entran bien después de una canción por tono Camelot, tempo y, si hay ficha, energía y momento
  de la sesión), **ver sus versiones** (original, extended, remixes, acapella, ediciones de DJ y
  mashups que la usan), ordenar para mezclar la selección y enviar a la papelera, con «Cancelar el
  envío» si son muchas. Todas las tablas con canciones ordenan ese menú de la misma forma.
- **Buscador en cada tabla**: filtra por palabras sueltas y en cualquier orden, sin acentos ni
  mayúsculas. Selección múltiple con Ctrl+clic y Mayús+clic para marcar, aplicar o descartar por
  bloques.
- **Editor**: edición manual de título y tags, con reproducción de un fragmento para comprobar.
- **Duplicados**: agrupa copias (por artista+título, solo título, o +duración) y **marca en verde la
  que conviene conservar**, según el criterio que elijas: carpeta prioritaria, mejor calidad o mayor
  duración. Puedes marcar las sobrantes de todos los grupos a la vez y enviarlas a la papelera de
  una sola pasada, con el avance a la vista y la opción de cancelar, conservando siempre un ejemplar
  de cada canción.
  Con **«Excluir ediciones de DJ»** se dejan fuera del análisis las ediciones hechas para pinchar
  (hype intro, open show, aca out, acapellas, transiciones, mashups): no son copias sobrantes, y
  emparejadas con su original tapan los duplicados de verdad.
  También puede agrupar **por el audio** (huella acústica), lo que encuentra la misma canción
  aunque tenga títulos distintos o no esté etiquetada. En ese modo cada versión conserva su propio
  ejemplar: tus ediciones de DJ no se marcan como sobrantes.
- **Calidad**: clasifica el audio por bitrate/formato y filtra el de baja calidad.
- **Formatos**: MP3, FLAC, WAV, **AIFF** (.aif y .aiff), M4A, AAC, OGG, Opus y WMA. Los AIFF-C que
  exportan algunas aplicaciones de macOS se pueden escuchar, medir y comprobar, y entran en la
  biblioteca con su duración, su calidad y sus etiquetas, pero BeatTag todavía no escribe en ellos:
  para etiquetarlos, conviértelos a AIFF o WAV sin comprimir.
- **Incompletas / No encontradas**: temas a los que les falta algún tag o que ninguna fuente
  identifica.
- **Volumen**: mide la sonoridad de cada grabación según **EBU R128** y corrige las que se apartan
  del resto. El audio **no se recodifica** —se ajusta la ganancia del propio MP3—, así que no hay
  pérdida de calidad, los archivos mantienen su tamaño y el cambio es reversible. Ese ajuste sin
  pérdida solo existe para MP3: un FLAC, un WAV, un AIFF o un M4A **se miden pero no se tocan**.
- **Tendencias**: el **chart diario de Spotify** de 77 países (y el Global), marcando lo que ya
  está en la biblioteca. Deezer queda como fuente alternativa.
- **Playlists**: igual que Tendencias, pero con la lista que tú elijas: pega el enlace de una
  playlist (o un álbum) de **Spotify o Deezer** para ver qué canciones están en la biblioteca y
  cuáles faltan, con la misma exportación a M3U8 o a una carpeta. De Deezer se lee entera; de
  Spotify, las 100 primeras canciones, que es lo que Spotify deja leer sin iniciar sesión.
  En las dos, **«Ordenar para mezclar»** ordena las disponibles para pincharlas seguidas (y deja
  elegir con cuál empezar), **«Añadir a pendientes»** guarda las que faltan y **«Seguir esta
  lista»** marca, cuando se revise otro día, las canciones que han entrado.
- **Pendientes**: las canciones que faltan en la biblioteca y quieres conseguir. Se tachan solas
  cuando aparecen en ella (al escanear o al pasar canciones desde la Bandeja), y vuelven a pendiente
  si el archivo con el que se tacharon desaparece sin dejar otra copia. Si una se tachó con un archivo que
  no es la canción, «Volver a pendiente» lo corrige y se puede deshacer. Se exportan a texto o CSV,
  y **«Buscar en mi pool»** abre su búsqueda en el pool de descargas que configures. También reúne
  las playlists y charts que sigues: «Actualizar todas» los vuelve a leer en segundo plano y cuenta
  las canciones nuevas y cuántas de ellas faltan.
- **Tonalidad**: muestra la clave de cada tema y su código **Camelot**, para mezclar en armónico.
  Se lee de los tags (la escriben rekordbox y similares); BeatTag no la deduce del audio.
- **Listas M3U8**: exporta lo que hayas filtrado a una lista que abren rekordbox, Engine DJ o Serato.
- **Bandeja de entrada**: una carpeta aparte donde dejas la música recién descargada. Antes de que
  entre en la biblioteca, avisa si ya está en ella (o hay otra versión), si está repetida, si su
  calidad es baja o si le faltan etiquetas. Cada canción pasa por *recibida → analizada → revisada →
  preparada*, y las preparadas se mueven a la biblioteca sin sobrescribir nada y con deshacer.
  Las descargas que sobran se **descartan a la papelera** de una vez, y cuando una descarga es la
  misma grabación que una canción de la biblioteca con mejor calidad, **«Sustituir la copia de la
  biblioteca»** la pone en su lugar (misma carpeta y mismo nombre, con la ficha de DJ de la antigua) y
  manda la antigua a la papelera, también con deshacer. Con **«Sustituir y copiar sus etiquetas»**
  pasan además el comentario, la tonalidad y el BPM de la copia antigua. Al deshacer, la descarga
  vuelve a la bandeja con su propia ficha de DJ y la copia antigua recupera la suya cuando vuelve de
  la papelera (la Ayuda explica los detalles). Una sustitución cortada por un cierre inesperado se
  recoge al volver a arrancar.
- **Ficha DJ**: lo que sabes de cada canción y no cabe en los tags — energía (1-10), momento de la
  sesión, voz, letra limpia o explícita, idioma, ambiente, etiquetas libres y «arma secreta». Se
  guarda en BeatTag sin tocar los archivos; se rellena por lotes (editar cuarenta a la vez no borra lo
  que cada una tenía) y con las teclas 1-0 para la energía. Opcionalmente se vuelca al comentario del
  archivo, conservando lo que ya hubiera.
- **Colecciones**: listas que se rellenan solas a partir de condiciones (género, BPM, años, energía,
  momento, voz, letra, idioma, etiquetas) y se exportan a M3U8.
- **Asistente IA**: herramientas con la IA local — buscar con una frase («bachata romántica de los
  2000 para cerrar»), proponer idioma y voz para las fichas, unificar géneros escritos de formas
  distintas, renombrar archivos con nombre sucio y completar nombres cortados a medias — y otras
  solo con reglas: ordenar una colección para mezclar (tono Camelot, tempo, energía y momento de la
  sesión), **limpiar la publicidad de los packs** (el record pool o el «@usuario» en el Álbum o el
  Artista) y **poner género por carpeta y por artista** a las canciones que no lo tienen. Ver
  [IA local](#-ia-local-opcional).
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

## 📁 Dónde se guardan tus datos

En `Documentos\Etiquetador de Musica\`: los ajustes, las cachés, los manifiestos para deshacer y los
registros. Las cachés se regeneran solas; estos archivos, en cambio, son trabajo tuyo y conviene
incluirlos en tus copias de seguridad:

| Archivo | Contenido |
|---|---|
| `fichas-dj.json` | Fichas de DJ (energía, momento, ambiente…) |
| `colecciones.json` | Colecciones |
| `bandeja.json` | Estado de cada canción de la Bandeja de entrada |
| `pendientes.json` | Lista de pendientes |
| `listas-seguidas.json` | Playlists y charts que sigues |
| `generos-por-carpeta.json` | Reglas de «Género por carpeta y artista» |
| `bandeja-sustituidas.json` | Sustituciones de la Bandeja que aún se pueden deshacer, con la ficha de DJ de la copia antigua |
| `ArtistAliases.json`, `ArtistsExceptions.json` | Nombres de artista (ver más abajo) |

La carpeta `bandeja-sustituciones` solo tiene contenido mientras hay una sustitución de la Bandeja en
curso: si la aplicación se cierra a medias, la descarga vuelve a la bandeja al arrancar de nuevo o al
analizarla.

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
Sin IA, las que la usan funcionan solo con reglas, salvo «Renombrar con IA», que la necesita.
Cada una se midió con bibliotecas reales antes de construirla, y la ayuda de la aplicación dice
cuánto acierta:

- **Buscar con una frase**: lo seguro (géneros y artistas de la biblioteca, BPM, décadas, «sin
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
  cortado, no se propone nada. Los que no tienen propuesta se listan igualmente, para escribir tú
  el final.

El Asistente reúne también herramientas **solo con reglas, sin IA**: ordenar para mezclar (que
respeta el momento de la sesión de las fichas), **limpiar la publicidad de los packs** (vacía un
Álbum que es el nombre del pool y propone el artista del nombre del archivo, escrito como en el resto
de la biblioteca, cuando el Artista es un «@usuario»; lo dudoso sale desmarcado y lo retirado puede
guardarse en el comentario) y **género por carpeta y artista** (reglas por carpeta que se guardan,
con subcarpetas que se pueden excluir de la regla de su carpeta madre, y, para el resto, el género
dominante del artista; nunca cambia un género existente).

Todas las tablas del Asistente tienen el **menú contextual** del resto de la aplicación: escuchar,
editar etiquetas, marcar o desmarcar las seleccionadas, abrir la carpeta y enviar a la papelera
(se puede parar con «Cancelar el proceso»).

Lo que escriben en tus archivos (géneros, nombres y etiquetas de los packs) se puede deshacer. Nada
se aplica sin que lo marques.

## 🎛️ rekordbox y los cue points

rekordbox guarda los cue points y el beatgrid en su base de datos, **ligados a la ruta del archivo**.
Al renombrar, los da por perdidos — y renombrar es justo lo que hace BeatTag.

Por eso cada renombrado queda anotado, y desde **Ajustes → Reparar colección de rekordbox** puedes
devolvérselos: exporta tu colección desde rekordbox, repárala y vuelve a importarla. El archivo
original no se modifica.

Al **sustituir** una copia desde la Bandeja, la descarga toma el nombre y la carpeta de la antigua:
rekordbox y Traktor conservan la pista si la extensión no cambia (conviene volver a analizarla); si
cambia, hay que reubicarla. Serato guarda los cue points dentro del propio archivo, así que se quedan
en la copia antigua.

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
