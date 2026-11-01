**Español** · [English](README.en.md) · [中文](README.zh-CN.md)

> **这是 [ralphtradesz/trading-hub](https://github.com/ralphtradesz/trading-hub) 的一个分支（fork）**，在原版基础上：
> - **自动更新**：程序启动时检查本仓库的新安装包（设置 → 更新），也会提示原项目发布了新版本；下载后校验 SHA-256 再打开安装程序，不会强制关闭正在交易的程序。
> - **自动跟进上游**：GitHub Actions 每 6 小时检查一次原项目，有更新就自动合并、跑测试、打包 Windows 安装程序并发布 Release（有冲突时不推送，只开一个 issue）。
> - **足迹图历史更完整**：可在 设置 → 数据 →「历史足迹范围」选 2 小时到 30 天；Binance / Bybit 已收盘的日子直接从交易所公开文件下载，远处的 K 线也有足迹。
> - **成交量分布可调行数**：「交易时段成交量分布」指标新增「每行 Tick 数」和「行数」两种调法，POC、VAH、VAL 线可调粗细、线型和颜色。
> - **用 Bybit 账户交易**：USDT 永续合约（统一账户 UTA），支持模拟盘 Demo Trading、测试网和实盘；市价、限价、止损单，改单不撤单，图表上一键平仓/反手。在 连接 → Bybit 里填 API Key。
> - **安装时可选安装位置**（例如 D 盘），以后更新会自动装回同一个位置。
> - **行情数据可以放到其他磁盘**：设置 → 数据 →「行情数据文件夹」→ 更改，会自动迁移已有数据。
> - **更快启动**：ReadyToRun 预编译；修复 Rithmic 改单后价格短暂跳回旧价的问题。
>
> **下载 Windows 安装程序：[TradingHub-Setup-x64.exe](https://github.com/gouqizi2214730-source/trading-hub/releases/latest/download/TradingHub-Setup-x64.exe)**（ARM 电脑用 [arm64 版](https://github.com/gouqizi2214730-source/trading-hub/releases/latest/download/TradingHub-Setup-arm64.exe)）· [全部版本](https://github.com/gouqizi2214730-source/trading-hub/releases)
>
> 许可证与原项目相同：MIT + Commons Clause（可免费使用、修改、分享，但不能出售）。

<div align="center">

# Trading Hub

**La plataforma de trading gratis y de código abierto que corre 100 % en tu computadora: Mac y Windows.**
Gráficos con footprint, operación con DOM, backtesting profesional anti-sobreoptimización y estrategias con IA,
con tus datos y tus claves siempre en tu computadora.

Creada por [**@ralphtradesz**](https://x.com/ralphtradesz)

¿Usás Windows? **[Descargá el instalador](https://github.com/ralphtradesz/trading-hub/releases/latest/download/TradingHub-Setup-x64.exe)** ([cómo instalarla](#usás-windows)).

[![Licencia MIT + Commons Clause](https://img.shields.io/badge/licencia-MIT%20%2B%20Commons%20Clause-amber.svg)](LICENSE)
![macOS Apple Silicon](https://img.shields.io/badge/macOS-Apple%20Silicon-black.svg)
![Windows 10 y 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)
![Avalonia](https://img.shields.io/badge/UI-Avalonia-8B44AC.svg)

![Trading Hub](docs/img/trading.png)

</div>

---

## Qué es

Trading Hub es una plataforma de trading de escritorio, gratuita y de código abierto. Todo corre localmente: la app, los
datos que grabás o bajás, tus estrategias, los backtests y las claves de tus brokers (que van al Llavero de macOS, nunca
a archivos). No hay servidores propios, cuentas que crear ni telemetría.

Sirve para **futuros de CME** (Rithmic, Tradovate y la historia de Databento), **cripto** (Binance y Bybit, con datos
gratis sin claves), **forex y CFDs** (OANDA, cTrader o archivos CSV) y **acciones de EE. UU.** (Alpaca).

La IA viene de **Claude Code con tu propia suscripción** (sin claves de API): un asistente que escribe estrategias en C#,
las compila, las prueba y arma reportes, y un agente que reparte el riesgo de tus estrategias en vivo.

## Funciones

### Gráficos
- Velas de tiempo, ticks, volumen o rango; velas, barras, línea o Heikin Ashi.
- **Footprint** configurable columna por columna (bid/ask, delta, volumen, POC, perfil por vela) con su propio editor.
- DOM, cinta de operaciones, dibujos y plantillas.
- Indicadores incluidos: medias móviles, VWAP con desvíos, Bollinger, RSI, MACD, volumen, delta, delta acumulado (CVD),
  perfil de volumen por sesión y grandes operaciones. Sumá los tuyos en C# (se recompilan al guardar), compatibles con
  la API de indicadores de ATAS, y los de ATAS u otras fuentes de código público: ver
  [Más indicadores](#más-indicadores).
- **Replay** de cualquier día guardado, a la velocidad que quieras.

### Operar
- **Chart Trader** y DOM con órdenes de mercado, límite y stop, brackets (stop y objetivos) y OCO.
- Cuentas simuladas incluidas y cuentas reales de tus conexiones; historial y estadísticas por cuenta.
- En la simulada nada te frena el clic; en cuentas reales podés pedir una confirmación antes de cada orden (se apaga
  en Ajustes). La seguridad va por límites de riesgo.

### Datos
- Todo lo que mirás se graba solo, tick a tick, con el lado agresor.
- Descargas: archivos públicos de Binance y Bybit, historia de Rithmic y de tus conexiones.
- **Databento**: años de futuros de CME tick a tick (pago por uso, te muestra el costo antes). Se guarda aparte y en los
  backtests elegís **solo Rithmic, solo Databento o las dos combinadas** (Rithmic primero y Databento completa lo que falta).
- Importación de CSV de Dukascopy, HistData, MetaTrader o TradingView.

### Estrategias y backtesting
- Estrategias en **C#** con un editor integrado que compila en milisegundos (Roslyn, con los errores marcados en el
  código) y una API simple: `EnterLong()`,
  `StopLossTicks`, indicadores, filtros de horario y de contexto de mercado.
- Backtests sobre cada operación real guardada, con comisiones, deslizamiento y llenado realista de órdenes límite.
- **Optimización bayesiana** (TPE) y **walk-forward inteligente**: elige mesetas estables en vez de picos, comprime ticks
  para ir más rápido y mide la sobreoptimización con **PBO (CSCV)** y **CPCV con embargo**. La librería trae además el
  **Sharpe deflactado** y una validación completa para usar desde código.
- El **timeframe** se optimiza como un parámetro más (M1 a D1), y tus estrategias **favoritas** quedan arriba de la lista.
- **Monte Carlo**, resultados **según el mercado** (rango, mixto o tendencia; volatilidad baja, normal o alta) y métricas de
  robustez para no confundir suerte con ventaja.

### En vivo
- Corré estrategias en tus cuentas desde el backtester o desde un gráfico, con tamaño, pérdida diaria y drawdown máximos.
  En el gráfico ves cómo le fue en su historia: el resultado real con costos, una mini curva y todas las estadísticas.
- Portafolios, un **agente de riesgo con Claude** que propone el tamaño de cada una (en cuentas reales aprobás vos),
  la condición de cada mercado en tiempo real y un panel en la barra de menú de la Mac.
- Reanudación automática al abrir y modo sin dormir (también con la tapa cerrada, si querés).

### Claude
- Un chat que escribe y corrige estrategias, y un **laboratorio** (servidor MCP de la misma app) donde Claude corre
  backtests, optimizaciones y walk-forwards y te deja un reporte profesional con todas las métricas.

## Capturas

| | |
|---|---|
| ![Footprint](docs/img/footprint.png) | ![Operar desde el gráfico](docs/img/operar.png) |
| Footprint con delta, POC y grandes operaciones | Chart Trader, DOM y cinta de operaciones |
| ![Estrategias con Claude](docs/img/claude.png) | ![Backtesting](docs/img/backtest.png) |
| Estrategias en C# con el asistente de Claude | Backtesting con resultados según el mercado |
| ![Datos y Databento](docs/img/datos.png) | ![Tema claro](docs/img/claro.png) |
| Historia tick a tick y Databento | Tema claro u oscuro |

## Instalación

### Lo más fácil: con Claude Code
Si tenés [Claude Code](https://claude.com/claude-code), abrí una Terminal en esta carpeta, corré `claude` y pedile:

```
Instalá Trading Hub en mi Mac siguiendo CLAUDE.md.
```

Revisa tu Mac, instala lo que falte (sin contraseña de administrador), compila la app y la deja en Aplicaciones.
La guía paso a paso está en [INSTALAR_CON_CLAUDE_CODE.md](INSTALAR_CON_CLAUDE_CODE.md).

### A mano
Necesitás una Mac con Apple Silicon y macOS 12 o más nuevo (para Windows, mirá [¿Usás Windows?](#usás-windows)).

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0
git clone https://github.com/ralphtradesz/trading-hub.git
cd trading-hub
./build-app.sh --install
```

La primera compilación tarda unos minutos. La app queda en `/Applications/Trading Hub.app` y arranca con una cuenta
simulada: nada opera dinero real hasta que lo habilites en Conexiones. Más detalles en
[docs/instalacion.md](docs/instalacion.md).

## ¿Usás Windows?

Trading Hub corre en Windows 10 y 11 con un instalador de un clic: no hace falta compilar nada ni instalar .NET.

1. Descargá el instalador:
   - **[PC con Intel o AMD (la mayoría)](https://github.com/ralphtradesz/trading-hub/releases/latest/download/TradingHub-Setup-x64.exe)**
   - [PC con Windows en ARM (Snapdragon, Surface Pro X)](https://github.com/ralphtradesz/trading-hub/releases/latest/download/TradingHub-Setup-arm64.exe)
2. Abrilo. Como el instalador todavía no tiene firma de un certificado pago, Windows puede mostrar «Windows protegió
   tu PC»: tocá **Más información** y después **Ejecutar de todas formas**.
3. Tocá **Instalar**. No pide permisos de administrador: queda en tu usuario, con accesos en el menú Inicio y en el
   escritorio. Al terminar, **Abrir Trading Hub**.

Arranca con una cuenta simulada, igual que en Mac. Tus datos quedan en `%APPDATA%\Trading Hub`, tus estrategias e
indicadores en `Documentos\Trading Hub` y tus claves en el **Administrador de credenciales de Windows**. Para
actualizar, instalá la versión nueva encima (tus datos no se tocan); para desinstalar, **Configuración → Aplicaciones →
Trading Hub → Desinstalar**.

Para compilarla vos en Windows: [Git](https://git-scm.com), el [SDK de .NET 10](https://dotnet.microsoft.com/download)
y [NSIS](https://nsis.sourceforge.io) (`winget install NSIS.NSIS`), y en la carpeta del repositorio
`.\build-windows.ps1` (deja los instaladores en `dist`). Desde una Mac: `brew install makensis` y `./build-windows.sh`.
Qué depende de cada sistema está en [PORTAR_A_WINDOWS.md](PORTAR_A_WINDOWS.md).

## Más indicadores

Trading Hub trae sus propios indicadores (medias, VWAP, Bollinger, RSI, MACD, volumen, delta, CVD, perfil de volumen y
grandes operaciones). Los de otras plataformas no vienen en el repositorio porque su código no tiene una licencia que
permita redistribuirlo, pero **los podés sumar vos** desde su código público:

**Los de ATAS, todos de una vez** (unos 250, desde su repositorio público
[AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators)):

```bash
git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators
python3 tools/port_atas.py
./build-app.sh --install
```

El script adapta los namespaces de ATAS a los de Trading Hub sin tocar la lógica (y corrige un par de detalles para que
compilen), y aparecen en la biblioteca de indicadores con los demás. Para actualizarlos cuando ATAS publique cambios:
`git -C third_party/ATAS-Indicators pull` y repetí los dos últimos pasos. Las dos carpetas que se crean están en el
`.gitignore`, así que no se suben si hacés un fork.

**Uno suelto, de ATAS o de cualquier otra fuente de código público en C#:** copiá el `.cs` en
`~/Documents/Trading Hub/Indicadores`. Trading Hub lo compila solo, adapta los namespaces de ATAS y lo suma a la
biblioteca. Si usa otro indicador (por ejemplo, el CCI de ATAS usa su SMA), copiá también ese.

Esos indicadores son de sus autores: usalos respetando la licencia de cada fuente. Más detalles en
[docs/indicadores.md](docs/indicadores.md).

## Documentación

| | |
|---|---|
| [Instalación](docs/instalacion.md) | Requisitos, compilar, actualizar y desinstalar |
| [Primeros pasos](docs/primeros-pasos.md) | Recorrido por la app |
| [Conexiones](docs/conexiones.md) | Brokers, exchanges y datos; cuentas reales y simuladas |
| [Gráficos](docs/graficos.md) | Períodos, footprint, DOM, dibujos, indicadores y replay |
| [Operar](docs/operar.md) | Chart Trader, DOM, órdenes, brackets y cuentas |
| [Datos](docs/datos.md) | Grabación, descargas, Databento e importación de CSV |
| [Estrategias](docs/estrategias.md) | La API en C# con ejemplos |
| [Backtesting](docs/backtesting.md) | Backtest, optimización, walk-forward, PBO, CPCV y Monte Carlo |
| [En vivo](docs/en-vivo.md) | Estrategias en tus cuentas, portafolios y agente de riesgo |
| [Claude](docs/asistente-claude.md) | El asistente de estrategias y el laboratorio MCP |
| [Indicadores](docs/indicadores.md) | Incluidos, propios y compatibles con la API de ATAS |
| [Ajustes](docs/ajustes.md) | Todas las opciones |
| [Privacidad](docs/privacidad.md) | Qué queda en tu Mac y qué se conecta a internet |
| [Desarrollo](docs/desarrollo.md) | Estructura del código, pruebas y escenas |
| [Windows](PORTAR_A_WINDOWS.md) | Qué depende de cada sistema operativo y cómo se armó la versión de Windows |

## Apoyá el proyecto

Trading Hub es gratis y lo va a seguir siendo. Si te sirve y querés apoyar su desarrollo, podés donar acá:

| Moneda | Red | Dirección |
|---|---|---|
| USDT | BEP20 (BNB Smart Chain) | `0xebedab9a1f6820914b0869e4126d5c54aa028a2a` |
| USDT | TRC20 (Tron) | `THAULimtnWpbQ8opZWxK8863nZEkyek4E5` |
| BTC | BEP20 (BNB Smart Chain) | `0xebedab9a1f6820914b0869e4126d5c54aa028a2a` |

Mandá cada moneda **solo por la red indicada**: lo que se envía por otra red se pierde. ¡Gracias!

## Aviso

Operar en los mercados implica riesgo de perder dinero. Trading Hub es una herramienta: no da consejos de inversión y
los resultados de un backtest no garantizan resultados futuros. Probá siempre primero con la cuenta simulada. El
software se entrega "tal cual", sin garantías (ver la [licencia](LICENSE)).

## Licencia

[MIT con la cláusula Commons Clause](LICENSE): podés usar Trading Hub gratis (también para operar tu propia plata),
modificarlo y compartirlo, siempre con el aviso de la licencia. Lo que no podés es **venderlo**: ni la app, ni una
versión modificada, ni un servicio pago cuyo valor venga de Trading Hub (por ejemplo, cobrar por instalarlo, hostearlo
o darle soporte). Por esa condición no es una licencia «open source» en el sentido estricto de la OSI: el código es
público y gratis, pero no se puede comercializar.

Los indicadores portados de otras plataformas que no tienen licencia abierta no forman parte de este repositorio, pero
los podés sumar vos desde su código público (ver [Más indicadores](#más-indicadores)).

---

<div align="center">

Hecho con dedicación por [**@ralphtradesz**](https://x.com/ralphtradesz)

</div>
