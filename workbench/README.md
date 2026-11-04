# Workbench (lo que era VPFM, dentro de KVP)

KVP trae los módulos de VPFM como una sección más (**Workbench**, Ctrl+6): mapa de calor de FlowMap, operaciones
agregadas (aggr), mapa de liquidez (Liquidity Mapper Fast), derivados, opciones GEX, estructura y herramientas. Ya no
hace falta instalar VPFM ni tener otro repositorio: todo se arma desde acá.

| Carpeta | Qué es |
|---|---|
| `web/` | La barra lateral de VPFM y los módulos propios (衍生 / 期权 / 结构 / 工具) |
| `aggr/` | Traducción al chino y fuentes locales de aggr, y el retoque de su compilación |
| `zh-patch/` | Traducción al chino de FlowMap |
| `server/vpfm_wb.py` | Extensión del servidor de FlowMap (proxy y ajustes) para el motor del mapa de calor |
| `scripts/` | `prepare_flowmap.py` (engancha la extensión) y `assemble.sh` (junta las páginas) |
| `upstreams.json` | Las versiones de los originales que se compilan |

## Cómo funciona

- KVP abre un **servidor local** en `http://127.0.0.1:47831` (o el siguiente puerto libre) que sirve las páginas
  (`/wb/`), lo que necesitaba el «外壳» de VPFM (`/api/wb/…`) y la **API de datos de KVP** (`/api/kvp/…`).
- Las **velas** que piden los módulos (las URL de Binance y Bybit de siempre, por `/api/wb/proxy/…`) salen de la caché
  de velas de KVP, la misma de los gráficos: una sola descarga para todo. El resto de las API públicas pasa por el proxy
  de KVP (Ajustes → General).
- El **motor del mapa de calor** de FlowMap (su servidor de Python, con su runtime en `flowmap/pyruntime` junto al
  programa, solo en x64) arranca la primera vez que se abre 热力; KVP le pasa `/api` y `/ws` por el mismo puerto.
- En Windows las páginas se ven dentro de KVP (WebView2); en macOS y Linux, en el navegador.

## API de datos de KVP

| Ruta | Respuesta |
|---|---|
| `GET /api/kvp/info` | versión, puerto, si están los módulos y el motor del mapa de calor |
| `GET /api/kvp/connections` | las conexiones de KVP |
| `GET /api/kvp/open` | los instrumentos abiertos (con operaciones y fotos del libro) |
| `GET /api/kvp/klines?conn=&symbol=&tf=1m\|5m\|15m\|1h\|4h\|1d&from=ms&to=ms&limit=` | `[[ms, o, h, l, c, volumen, comprador], …]` de la caché |
| `GET /api/kvp/trades?feed=&symbol=&limit=` | `[[ms, precio, volumen, "b"\|"s"], …]` |
| `GET /api/kvp/depth?feed=&symbol=&levels=` | `{time, bids:[[precio, tamaño]…], asks:[…]}` |
| `GET /api/kvp/liquidity?feed=&symbol=&minutes=` | las fotos del libro del mapa de calor |
| `WS /api/kvp/stream?conn=&symbol=` | en vivo: `{"type":"trades",…}` y `{"type":"book",…}` cada 250 ms |

Solo responde a `127.0.0.1` / `localhost`; las acciones (abrir ventanas, cambiar el proxy) solo desde sus propias páginas.

## Actualizaciones de los originales

«Sincronizar con el original» mira cada 6 h FlowMap, aggr y Liquidity Mapper Fast; si publicaron algo, anota la versión
nueva en `upstreams.json` y «Empaquetar y publicar» arma KVP con ella (en Windows: compila las tres páginas, el runtime de
Python y prueba que el motor arranque). Si un original cambió de forma que no compila, KVP se publica igual con el
Workbench que ya tenía.

Licencias: FlowMap (Apache-2.0), aggr (GPL-3.0), Liquidity Mapper Fast (MIT); van en `wb/licenses`.
