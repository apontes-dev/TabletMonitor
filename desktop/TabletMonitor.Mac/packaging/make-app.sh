#!/bin/sh
# Gera TabletMonitor.app para Apple Silicon (osx-arm64) e Intel (osx-x64).
#   ./make-app.sh [saida]   → saida/TabletMonitor-macOS-AppleSilicon.zip e -Intel.zip
# Pode rodar no Linux/Windows/macOS (só precisa do .NET 8 SDK e zip).
set -e
cd "$(dirname "$0")/.."
OUT="${1:-$(pwd)/packaging/out}"
mkdir -p "$OUT"
for pair in "osx-arm64:AppleSilicon" "osx-x64:Intel"; do
  RID="${pair%%:*}"; NAME="${pair##*:}"
  PUB="$OUT/publish-$RID"
  APP="$OUT/$NAME/TabletMonitor.app"
  rm -rf "${PUB:?}" "${OUT:?}/${NAME:?}"
  dotnet publish -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false -o "$PUB" >/dev/null
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$PUB"/. "$APP/Contents/MacOS/"
  cp packaging/Info.plist "$APP/Contents/Info.plist"
  cp packaging/TabletMonitor.icns "$APP/Contents/Resources/TabletMonitor.icns"
  chmod +x "$APP/Contents/MacOS/TabletMonitor"
  cp packaging/LEIA-ME-INSTALAR.txt "$OUT/$NAME/"
  ( cd "$OUT/$NAME" && rm -f "../TabletMonitor-macOS-$NAME.zip" && zip -qry "../TabletMonitor-macOS-$NAME.zip" TabletMonitor.app LEIA-ME-INSTALAR.txt )
  rm -rf "${PUB:?}"
  echo "ok: $OUT/TabletMonitor-macOS-$NAME.zip"
done
