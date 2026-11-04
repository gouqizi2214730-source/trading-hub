# Scripts KVS

KVS es el lenguaje simple de indicadores de KVP, pensado como el «wrun» de openmarket: se declara lo que entra, lo que
sale y qué se calcula en cada vela. Se escribe en Indicadores → editor de código → botón de script (o un archivo `.kvs`
en la carpeta de indicadores propios); al guardar se traduce a C#, se compila y aparece en la biblioteca, grupo
**Scripts KVS**. Los errores salen con la línea del `.kvs`.

```js
name "Bandas EMA"
param length = 20 { min: 1, max: 500, description: "Velas de la media" }
param mult = 2
input src = ohlcv.close
input buys = trades.volume { side: "BUY" }
output basis = line { overlay: true, color: "#f2b53a", width: 2 }
output upper = line { overlay: true, color: "#3d8bfd" }
output flow = histogram { color_by: "sign", colors: ["#26c281", "#f0525a"] }
level 0
onBar {
  basis = ema(src, length)
  upper = basis + mult * stdev(src, length)
  flow = buys - (volume - buys)
  if (crossover(src, upper)) { alert("cerró arriba de la banda") }
}
```

| Parte | Qué hace |
|---|---|
| `name "…"` | nombre en la biblioteca |
| `param n = 20 { min, max, description }` | ajuste (número o `true` / `false`) que se ve en la configuración del indicador |
| `input x = …` | `ohlcv.open/high/low/close/volume/hl2/hlc3/ohlc4/time`, `trades.volume {side: "BUY"/"SELL"}`, `trades.delta`, `trades.count`, `book.bid` / `book.ask` / `book.delta {within: ticks}` (las fotos del libro del mapa de calor). `open`, `high`, `low`, `close`, `volume`, `delta`, `buyvol`, `sellvol` se pueden usar sin declararlas. |
| `output x = line \| histogram \| dots { … }` | `overlay: true` = sobre el precio (si no, panel aparte), `color`, `width`, `opacity`, `label`, `color_by: "sign"` con `colors: [positivo, negativo]` |
| `level 70 { color }` | línea horizontal en el panel |
| `onStart { }` / `onBar { }` | se corre al empezar / en cada vela |
| `var x = …` | variable (también es una serie: `x[1]` es su valor de la vela anterior) |
| `x[n]` | valor de hace n velas de cualquier serie |
| `if (…) { } else { }`, `and`, `or`, `not`, `? :` | condiciones |
| `alert("…")` | alerta (una por vela, en la vela actual) |

Funciones: `ema sma rma wma stdev highest lowest sum change cum crossover crossunder` y `abs min max sqrt log log10 exp pow
round floor ceil sign nz na sin cos tan`; `bar_index`, `na`, `PI`.

También entiende la forma de wrun: `param("n", 20, {min: 1})`, `input("c", ohlcv.close)`,
`output("x", line, overlay, {color: "#fff"})`, `function onBar() { … }` y `out_x(valor)`.
