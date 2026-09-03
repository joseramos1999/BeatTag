#!/usr/bin/env bash
#
# Monta BeatTag.app para macOS y lo deja empaquetado en la raíz del repositorio.
#
# Se ejecuta EN un Mac (los runners macos-latest de GitHub Actions). Lo usan tanto la CI de cada
# commit como la publicación de una versión, para que lo que se prueba y lo que se entrega sean
# exactamente lo mismo.
#
# Produce un binario UNIVERSAL (arm64 + x86_64). Importa para poder enviárselo a alguien sin
# preguntarle antes qué Mac tiene: una aplicación solo-arm64 no abre en un Intel, y una solo-x64
# funcionaría en Apple Silicon pero emulada bajo Rosetta. Universal va nativa en los dos.
#
# Salida:
#   BeatTag.app                        el paquete montado
#   BeatTag-macos-universal.tar.gz     el paquete comprimido (el .tar.gz conserva permisos; el zip no)

set -euo pipefail

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SLN="$RAIZ/EtiquetadorNet"
PROYECTO="$SLN/Etiquetador.App/Etiquetador.App.csproj"
APP="$RAIZ/BeatTag.app"
MACOS="$APP/Contents/MacOS"

echo "==> Compilando para las dos arquitecturas"
dotnet publish "$PROYECTO" -c Release -r osx-arm64 --self-contained -o "$RAIZ/pub-arm64" --nologo -v q
dotnet publish "$PROYECTO" -c Release -r osx-x64   --self-contained -o "$RAIZ/pub-x64"   --nologo -v q

echo "==> Montando el paquete"
rm -rf "$APP"
mkdir -p "$MACOS" "$APP/Contents/Resources"
cp -R "$RAIZ/pub-arm64/." "$MACOS/"

# Se parte de la compilación arm64 y se le fusiona la de Intel binario a binario. Los .dll
# gestionados son iguales en ambas y se quedan como están; lo que hay que unir son el ejecutable y
# las bibliotecas nativas (Skia, HarfBuzz, las del propio runtime de .NET).
echo "==> Fusionando arquitecturas"
unidos=0
for f in "$MACOS"/*; do
    nombre="$(basename "$f")"
    gemelo="$RAIZ/pub-x64/$nombre"
    [ -f "$f" ] && [ -f "$gemelo" ] || continue
    file "$f" | grep -q "Mach-O" || continue

    if lipo -create "$f" "$gemelo" -output "$f.universal" 2>/dev/null; then
        mv "$f.universal" "$f"
        unidos=$((unidos + 1))
    else
        rm -f "$f.universal"   # ya era universal, o lipo no supo: se deja el de arm64
    fi
done
echo "    $unidos binarios universales"

chmod +x "$MACOS/BeatTag"

VERSION=$(grep -oE 'Version = "[0-9]+\.[0-9]+\.[0-9]+"' "$SLN/Etiquetador.Core/AppInfo.cs" \
          | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' || echo "0.0.0")
echo "==> Versión $VERSION"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>               <string>BeatTag</string>
  <key>CFBundleDisplayName</key>        <string>BeatTag</string>
  <key>CFBundleIdentifier</key>         <string>com.joseramos.beattag</string>
  <key>CFBundleExecutable</key>         <string>BeatTag</string>
  <key>CFBundlePackageType</key>        <string>APPL</string>
  <key>CFBundleVersion</key>            <string>$VERSION</string>
  <key>CFBundleShortVersionString</key> <string>$VERSION</string>
  <key>CFBundleIconFile</key>           <string>beattag</string>
  <key>LSMinimumSystemVersion</key>     <string>12.0</string>
  <key>NSHighResolutionCapable</key>    <true/>
</dict>
</plist>
PLIST

# El icono se convierte del .ico de Windows. Si falla, la aplicación se queda con el icono genérico:
# por un icono no se tumba una compilación.
echo "==> Icono"
if sips -s format png "$SLN/Etiquetador.App/Assets/beattag.ico" --out /tmp/beattag.png >/dev/null 2>&1; then
    rm -rf /tmp/beattag.iconset && mkdir -p /tmp/beattag.iconset
    for t in 16 32 128 256 512; do
        sips -z "$t" "$t" /tmp/beattag.png --out "/tmp/beattag.iconset/icon_${t}x${t}.png" >/dev/null 2>&1 || true
        sips -z "$((t * 2))" "$((t * 2))" /tmp/beattag.png --out "/tmp/beattag.iconset/icon_${t}x${t}@2x.png" >/dev/null 2>&1 || true
    done
    if iconutil -c icns /tmp/beattag.iconset -o "$APP/Contents/Resources/beattag.icns" 2>/dev/null; then
        echo "    icono incrustado"
    else
        echo "    sin icono (iconutil no pudo)"
    fi
else
    echo "    sin icono (sips no pudo leer el .ico)"
fi

echo "==> Comprobaciones"
test -x "$MACOS/BeatTag"
plutil -lint "$APP/Contents/Info.plist"
ARQUITECTURAS=$(lipo -archs "$MACOS/BeatTag")
echo "    arquitecturas del ejecutable: $ARQUITECTURAS"
du -sh "$APP"

# Se comprime en .tar.gz y no en .zip: el zip pierde el permiso de ejecución y, sin él, el paquete
# descargado no arranca de ninguna manera.
echo "==> Comprimiendo"
rm -f "$RAIZ/BeatTag-macos-universal.tar.gz"
tar -czf "$RAIZ/BeatTag-macos-universal.tar.gz" -C "$RAIZ" BeatTag.app
ls -lh "$RAIZ/BeatTag-macos-universal.tar.gz"

rm -rf "$RAIZ/pub-arm64" "$RAIZ/pub-x64"

# Para que se pueda comprobar desde fuera sin permisos de administrador sobre los registros.
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "- **BeatTag.app** $VERSION · arquitecturas: \`$ARQUITECTURAS\` · $unidos binarios fusionados" >> "$GITHUB_STEP_SUMMARY"
fi
echo "::notice title=BeatTag.app::version $VERSION · arquitecturas $ARQUITECTURAS · $unidos binarios universales"
