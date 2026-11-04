#!/usr/bin/env python3
"""Prepara el servidor de FlowMap (motor del mapa de calor del Workbench de KVP).

Uso: python workbench/scripts/prepare_flowmap.py <fuente de FlowMap>

KVP sirve el Workbench (páginas, /api/wb/…, /api/kvp/…) con su propio servidor local; del servidor de Python de FlowMap
solo usa el motor del mapa de calor (/api y /ws), al que le pasa esas rutas. Acá se le agrega vpfm_wb.py (el mismo de
VPFM) para que sus conexiones a los exchanges usen el proxy (trust_env) y entienda los ajustes. Si el original cambió
el lugar donde se engancha, se corta con error y no se publica nada roto.
"""
import pathlib
import re
import shutil
import sys

sys.stdout.reconfigure(encoding='utf-8')
HERE = pathlib.Path(__file__).resolve().parent.parent
root = pathlib.Path(sys.argv[1]).resolve()

shutil.copy2(HERE / 'server/vpfm_wb.py', root / 'server/src/flowmap_server/api/vpfm_wb.py')
app = root / 'server/src/flowmap_server/api/app.py'
s = app.read_text(encoding='utf-8')
new, n = re.subn(r'(\n(\s*)app\.include_router\(ws\.router\)\n)',
                 r'\1\2from flowmap_server.api import vpfm_wb  # KVP: proxy y ajustes\n\2vpfm_wb.install(app)\n', s, count=1)
if n != 1:
    sys.exit('[kvp] no encontré «app.include_router(ws.router)» en server/.../api/app.py (el original cambió)')
app.write_text(new, encoding='utf-8')
print('[kvp] servidor de FlowMap preparado')
