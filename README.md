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
  AcoustID** (huella acústica), con IA opcional de rescate. Propone tags y un nombre de archivo, y
  puntúa la **confianza** de cada propuesta (incluida la coherencia con los tags que ya trae el
  archivo). Las propuestas dudosas se auto-desmarcan para que las revises.
- **Editor**: edición manual de título y tags, con reproducción de un fragmento para comprobar.
- **Duplicados**: agrupa copias (por artista+título, solo título, o +duración) y permite enviarlas a
  la papelera.
- **Calidad**: clasifica el audio por bitrate/formato y filtra el de baja calidad.
- **Incompletas / No encontradas**: temas a los que les falta algún tag o que ninguna fuente
  identifica.
- **Estadísticas**: reparto de la biblioteca por BPM, género, calidad, década y Clean/Explícito.
- **Importar rekordbox**: trae BPM y clave musical desde un XML de rekordbox.
- **Caché persistente** en tres niveles (respuestas de red, escaneo y análisis): no se reprocesa lo
  ya hecho salvo que lo pidas.

## 📥 Descargar

La versión compilada (`BeatTag.exe`, ejecutable único y autocontenido) se publica en la página de
**[Releases](../../releases)**. Descárgala y ejecútala — no requiere instalar .NET.

Requisitos: **Windows 10/11 (x64)**.

## 🔑 Claves de API (opcional)

BeatTag funciona con Deezer e iTunes sin configuración. Para usar **Spotify, Discogs, AcoustID** o la
**IA**, introduce tus claves en la pestaña **Ajustes**. Se guardan **cifradas** en tu equipo con
DPAPI (Windows Data Protection, por usuario) — **nunca** salen del equipo ni se incluyen en el repo.

Los datos de la app (config, cachés, logs) se guardan en
`Documentos\Etiquetador de Musica\`.

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
