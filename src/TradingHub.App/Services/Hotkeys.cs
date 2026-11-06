using Avalonia.Controls;
using Avalonia.Input;

namespace TradingHub.App.Services;

/// <summary>Una acción que se puede disparar con un atajo de teclado.</summary>
public sealed record HotkeyDef(string Id, string Group, string Name, string? DefaultGesture = null);

/// <summary>
/// Atajos de teclado configurables (Ajustes → Atajos de teclado). Los de operar vienen sin asignar a
/// propósito: cada uno elige los suyos. En cuentas reales siguen pidiendo confirmación si está activada.
/// </summary>
public static class Hotkeys
{
    public static readonly HotkeyDef[] All =
    {
        new("buy-market", "Operar", "Comprar a mercado"),
        new("sell-market", "Operar", "Vender a mercado"),
        new("buy-bid", "Operar", "Comprar límite en el bid"),
        new("sell-ask", "Operar", "Vender límite en el ask"),
        new("flatten", "Operar", "Cerrar la posición"),
        new("reverse", "Operar", "Dar vuelta la posición"),
        new("cancel-all", "Operar", "Cancelar todas las órdenes"),
        new("close-25", "Operar", "Cerrar el 25 %"),
        new("close-50", "Operar", "Cerrar el 50 %"),
        new("breakeven", "Operar", "Stop a breakeven"),
        new("brackets", "Operar", "Poner stop y objetivos de la plantilla"),
        new("qty-next", "Operar", "Cantidad: siguiente preset"),
        new("qty-prev", "Operar", "Cantidad: preset anterior"),

        new("period-m1", "Gráfico", "Período 1 minuto", "Alt+D1"),
        new("period-m5", "Gráfico", "Período 5 minutos", "Alt+D2"),
        new("period-m15", "Gráfico", "Período 15 minutos", "Alt+D3"),
        new("period-h1", "Gráfico", "Período 1 hora", "Alt+D4"),
        new("period-h4", "Gráfico", "Período 4 horas", "Alt+D5"),
        new("period-d1", "Gráfico", "Período diario", "Alt+D6"),
        new("style-candles", "Gráfico", "Estilo velas"),
        new("style-footprint", "Gráfico", "Estilo footprint"),
        new("style-heikin", "Gráfico", "Estilo Heikin Ashi"),
        new("autoscale", "Gráfico", "Escala automática"),
        new("go-last", "Gráfico", "Ir al último precio"),
        new("tool-hline", "Gráfico", "Dibujar línea horizontal", "Alt+H"),
        // como en TradingView y OpenMarket
        new("tool-trend", "Gráfico", "Dibujar línea de tendencia", "Alt+T"),
        new("tool-hray", "Gráfico", "Dibujar rayo horizontal", "Alt+J"),
        new("tool-vline", "Gráfico", "Dibujar línea vertical", "Alt+V"),
        new("tool-cross", "Gráfico", "Dibujar línea cruzada", "Alt+C"),
        new("tool-fib", "Gráfico", "Dibujar retroceso de Fibonacci", "Alt+F"),
        new("tool-rect", "Gráfico", "Dibujar rectángulo", "Alt+Shift+R"),
        new("tool-brush", "Gráfico", "Dibujar con el pincel", "Alt+B"),
        new("tool-text", "Gráfico", "Escribir un texto", "Alt+N"),
        new("tool-cursor", "Gráfico", "Cursor (soltar herramienta)"),

        new("layout-1", "Ventanas", "Diseño de un gráfico"),
        new("layout-4", "Ventanas", "Diseño de cuatro gráficos"),
        new("next-tab", "Ventanas", "Pestaña siguiente", "Ctrl+Tab"),
        new("prev-tab", "Ventanas", "Pestaña anterior", "Ctrl+Shift+Tab"),
        new("detach", "Ventanas", "Sacar el gráfico a una ventana"),
        new("toggle-dom", "Ventanas", "Mostrar u ocultar el DOM"),
        new("toggle-trader", "Ventanas", "Mostrar u ocultar el Chart Trader del gráfico", "Alt+Shift+T"),
        new("toggle-panel", "Ventanas", "Mostrar u ocultar el panel derecho"),
    };

    /// <summary>El atajo de cada herramienta de dibujo que tiene uno.</summary>
    public static readonly IReadOnlyDictionary<TradingHub.Charting.Drawings.DrawingKind, string> DrawingTools = new Dictionary<TradingHub.Charting.Drawings.DrawingKind, string>
    {
        [TradingHub.Charting.Drawings.DrawingKind.HorizontalLine] = "tool-hline",
        [TradingHub.Charting.Drawings.DrawingKind.TrendLine] = "tool-trend",
        [TradingHub.Charting.Drawings.DrawingKind.HorizontalRay] = "tool-hray",
        [TradingHub.Charting.Drawings.DrawingKind.VerticalLine] = "tool-vline",
        [TradingHub.Charting.Drawings.DrawingKind.CrossLine] = "tool-cross",
        [TradingHub.Charting.Drawings.DrawingKind.FibRetracement] = "tool-fib",
        [TradingHub.Charting.Drawings.DrawingKind.Rectangle] = "tool-rect",
        [TradingHub.Charting.Drawings.DrawingKind.Brush] = "tool-brush",
        [TradingHub.Charting.Drawings.DrawingKind.Text] = "tool-text",
    };

    public static string? GestureOf(AppSettings s, string id) =>
        s.Hotkeys.TryGetValue(id, out var g) ? (string.IsNullOrEmpty(g) ? null : g) : All.FirstOrDefault(h => h.Id == id)?.DefaultGesture;

    /// <summary>Texto lindo para mostrar un atajo (⌥1, ⇧B, F5… en la Mac; Alt+1, Shift+B, F5… en Windows).</summary>
    public static string Pretty(string? gesture) => Pretty(gesture, mac: !Os.IsWindows);

    internal static string Pretty(string? gesture, bool mac)
    {
        if (string.IsNullOrEmpty(gesture)) return "—";
        try
        {
            var g = KeyGesture.Parse(gesture);
            string key = g.Key switch
            {
                >= Key.D0 and <= Key.D9 => ((int)(g.Key - Key.D0)).ToString(),
                >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(g.Key - Key.NumPad0),
                Key.Space => "Espacio",
                Key.Return => mac ? "↩" : "Enter",
                Key.Tab => "Tab",
                Key.Up => "↑",
                Key.Down => "↓",
                Key.Left => "←",
                Key.Right => "→",
                _ => g.Key.ToString(),
            };
            return Keys.Format(g.KeyModifiers, key, mac);
        }
        catch
        {
            return gesture;
        }
    }

    /// <summary>Atajo a partir de una tecla presionada (null si es solo un modificador).</summary>
    public static string? FromKey(Key key, KeyModifiers mods)
    {
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None) return null;
        return new KeyGesture(key, mods).ToString();
    }

    /// <summary>Acción que corresponde a la tecla, o null. No interfiere con lo que se escribe en un campo de texto.</summary>
    public static string? Match(AppSettings s, KeyEventArgs e, TopLevel? top)
    {
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        bool typing = top?.FocusManager?.GetFocusedElement() is TextBox;
        bool strongModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        bool functionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (typing && !strongModifier && !functionKey) return null;
        foreach (var h in All)
        {
            var g = GestureOf(s, h.Id);
            if (g == null) continue;
            try
            {
                if (KeyGesture.Parse(g).Matches(e)) return h.Id;
            }
            catch
            {
                // atajo mal guardado: se ignora
            }
        }

        return null;
    }
}
