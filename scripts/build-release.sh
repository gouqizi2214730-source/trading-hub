#!/usr/bin/env bash
# Builds the Windows installers of KVP (x64 and ARM64) from Linux, macOS or Git Bash on Windows.
#
#   scripts/build-release.sh [VERSION] [ARCHES]
#
#   VERSION  e.g. 1.2.0.1 (upstream version + fork build number). Default: the <Version> in Directory.Build.props.
#   ARCHES   default "x64 arm64".
#
# Optimizations over the upstream build-windows.sh:
#   - ReadyToRun: the app's code is precompiled, so it opens noticeably faster (no JIT on first use of each screen).
#   - Tiered PGO stays on (default) so hot paths are still re-optimized while running.
#   - SHA-256 of each installer next to it (the in-app updater also checks GitHub's digest).
#
# Needs the .NET 10 SDK and NSIS (apt install nsis / brew install makensis / winget install NSIS.NSIS).
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# makensis reads the script as UTF-8 only with a UTF-8 locale
export LC_ALL=C.UTF-8 LANG=C.UTF-8

BASE=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
VERSION="${1:-$BASE}"
ARCHES="${2:-x64 arm64}"
R2R="${R2R:-true}"

# NSIS VIProductVersion needs exactly 4 numbers
IFS=. read -r -a parts <<<"$VERSION"
VIVERSION="$VERSION"
while [ "${#parts[@]}" -lt 4 ]; do parts+=(0); VIVERSION="$VIVERSION.0"; done

MAKENSIS=$(command -v makensis || true)
[ -z "$MAKENSIS" ] && [ -x "/c/Program Files (x86)/NSIS/makensis.exe" ] && MAKENSIS="/c/Program Files (x86)/NSIS/makensis.exe"
[ -z "$MAKENSIS" ] && { echo "makensis not found (apt install nsis / brew install makensis / winget install NSIS.NSIS)"; exit 1; }

mkdir -p dist
for ARCH in $ARCHES; do
  OUT="dist/win-$ARCH"
  rm -rf "$OUT"
  echo "== publish win-$ARCH $VERSION (ReadyToRun=$R2R)"
  dotnet publish src/TradingHub.App/TradingHub.App.csproj -c Release -r "win-$ARCH" --self-contained true -o "$OUT" -v q \
    -p:Version="$VERSION" -p:PublishReadyToRun="$R2R"
  # Workbench (módulos web de VPFM y el motor del mapa de calor de FlowMap), si se armó (ver workbench/ y release.yml)
  WBD="${WORKBENCH_DIR:-workbench-dist}"
  if [ -f "$WBD/wb/index.html" ]; then
    cp -r "$WBD/wb" "$OUT/wb"
    if [ "$ARCH" = "x64" ] && [ -d "$WBD/flowmap/pyruntime" ]; then cp -r "$WBD/flowmap" "$OUT/flowmap"; fi
    echo "   + Workbench ($(du -sh "$OUT/wb" | cut -f1) de páginas$([ -d "$OUT/flowmap" ] && echo ", motor del mapa de calor $(du -sh "$OUT/flowmap" | cut -f1)"))"
  fi
  SETUP="dist/KVP-Setup-$ARCH.exe"
  # lo que es del programa (para borrar solo eso al actualizar o desinstalar: nunca la carpeta entera, por si el usuario
  # guardó datos adentro)
  FILES="dist/files-$ARCH.nsh"
  {
    echo '!macro RemoveAppFiles DIR'
    (cd "$OUT" && find . -type f | sed 's|^\./||' | sort) | while IFS= read -r f; do printf '  Delete "${DIR}\\%s"\n' "${f//\//\\}"; done
    (cd "$OUT" && find . -mindepth 1 -maxdepth 1 -type d | sed 's|^\./||' | sort) | while IFS= read -r d; do printf '  RMDir /r "${DIR}\\%s"\n' "$d"; done
    echo '!macroend'
  } >"$FILES"
  "$MAKENSIS" -V2 -DVERSION="$VERSION" -DVIVERSION="$VIVERSION" -DARCH="$ARCH" -DSRC="$PWD/$OUT" -DOUT="$PWD/$SETUP" \
    -DFILELIST="$PWD/$FILES" installer/TradingHub.nsi
  (cd dist && sha256sum "$(basename "$SETUP")" >"$(basename "$SETUP").sha256")
  # con el nombre de la edición anterior: las Trading Hub ya instaladas buscan ese archivo para actualizarse a KVP
  cp "$SETUP" "dist/TradingHub-Setup-$ARCH.exe"
  (cd dist && sha256sum "TradingHub-Setup-$ARCH.exe" >"TradingHub-Setup-$ARCH.exe.sha256")
  echo "OK: $SETUP ($(du -h "$SETUP" | cut -f1))"
done
