#!/bin/sh
# Monta os pacotes do TWRP a partir de src/ + o APK compilado.
#   ./build.sh            → dist/TabletMonitorROM-SM-T520.zip, -semgoogle.zip, TabletMonitorROM-restaurar.zip
set -e
cd "$(dirname "$0")"
APK=../android/app/build/outputs/apk/release/app-release.apk
[ -f "$APK" ] || { echo "compile o app Android antes (android/: ./gradlew assembleRelease)"; exit 1; }
mkdir -p dist work
rm -rf work/pkg && cp -r src work/pkg
cp "$APK" work/pkg/tm/TabletMonitor.apk
python3 make_bootanimation.py work/pkg/tm/bootanimation.zip
( cd work/pkg && rm -f ../rom.zip && zip -qr -X ../rom.zip META-INF tm )
cp work/rom.zip dist/TabletMonitorROM-SM-T520.zip
cp work/rom.zip dist/TabletMonitorROM-SM-T520-semgoogle.zip
cp work/rom.zip dist/TabletMonitorROM-restaurar.zip
ls -la dist
