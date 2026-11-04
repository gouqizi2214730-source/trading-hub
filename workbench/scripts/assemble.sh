#!/usr/bin/env bash
# Junta los módulos web del Workbench en una carpeta lista para el instalador de KVP:
#   assemble.sh <salida> <flowmap> <aggr> <lmap>
# <salida>/wb          páginas (barra lateral de VPFM + FlowMap + aggr + Liquidity Mapper + módulos propios)
# <salida>/flowmap     el runtime de Python con el servidor de FlowMap (si se armó: workbench-python en el workflow)
set -euo pipefail
OUT="$1"; FM="$2"; AG="$3"; LM="$4"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
WB="$OUT/wb"
rm -rf "$WB"; mkdir -p "$WB"
cp -r "$HERE/web/." "$WB/"
cp -r "$FM/client/dist" "$WB/flowmap"
cp -r "$AG/dist" "$WB/aggr"
cp -r "$LM/dist" "$WB/lmap"
if grep -l "Program Files\|Program%20Files" "$WB"/flowmap/index.html "$WB"/aggr/index.html "$WB"/lmap/index.html; then
  echo "las rutas de los recursos quedaron como rutas de Windows" >&2; exit 1
fi
# el nombre del programa que la aloja
sed -i 's#<title>VPFM</title>#<title>KVP Workbench</title>#' "$WB/index.html" || true
mkdir -p "$WB/licenses"
cp "$FM/LICENSE" "$WB/licenses/FlowMap-Apache-2.0.txt"
cp "$AG/LICENSE" "$WB/licenses/aggr-GPL-3.0.txt"
cp "$LM/LICENSE" "$WB/licenses/LiquidityMapperFast-MIT.txt"
d() { git -C "$1" log -1 --format=%cs; }
s() { git -C "$1" rev-parse HEAD; }
PY=$(command -v python3 || command -v python)
"$PY" - "$WB/version.json" "$(s "$FM")" "$(d "$FM")" "$(s "$AG")" "$(d "$AG")" "$(s "$LM")" "$(d "$LM")" <<'PY'
import json, sys, datetime
out, fm, fmd, ag, agd, lm, lmd = sys.argv[1:]
json.dump({"version": "kvp", "built": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
           "upstreams": {"flowmap": {"sha": fm, "date": fmd}, "aggr": {"sha": ag, "date": agd}, "lmap": {"sha": lm, "date": lmd}}},
          open(out, "w", encoding="utf-8"), ensure_ascii=False)
PY
du -sh "$WB"
