using System.Drawing;
using TradingHub.Indicators.Native.Structure;
using TradingHub.Rendering;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>Un cuadro de valores (título y filas nombre–valor) para dibujar en cualquier panel, y un par de dibujos chicos.</summary>
internal static class PaneCard
{
    public static Rectangle Draw(RenderContext ctx, Rectangle region, string? title, IReadOnlyList<(string Name, string Value, Color Color)> rows, DashCorner corner = DashCorner.TopRight, int fontSize = 9, int top = 22)
    {
        var font = new RenderFont("Arial", fontSize);
        var bold = new RenderFont("Arial", fontSize, FontStyle.Bold);
        var tr = rows.Select(r => (Name: UiText.Tr(r.Name), Value: UiText.Tr(r.Value), r.Color)).ToList();
        string? t = title == null ? null : UiText.Tr(title);
        int nameW = tr.Count == 0 ? 0 : tr.Max(r => ctx.MeasureString(r.Name, font).Width);
        int valW = tr.Count == 0 ? 0 : tr.Max(r => ctx.MeasureString(r.Value, bold).Width);
        int rowH = ctx.MeasureString("Ag", font).Height + 3;
        int w = Math.Max(t == null ? 0 : ctx.MeasureString(t, bold).Width, nameW + valW + 18) + 16;
        int h = tr.Count * rowH + (t != null ? rowH + 2 : 0) + 10;
        int col = (int)corner % 3, row = (int)corner / 3;
        int x = col switch { 0 => region.Left + 8, 1 => region.Left + (region.Width - w) / 2, _ => region.Right - w - 8 };
        int y = row switch { 0 => region.Top + top, 1 => region.Top + (region.Height - h) / 2, _ => region.Bottom - h - 8 };
        var rect = new Rectangle(x, y, w, h);
        ctx.FillRectangle(Color.FromArgb(215, 18, 20, 26), rect, 5);
        ctx.DrawRectangle(new RenderPen(Color.FromArgb(90, 120, 130, 150), 1), rect, 5);
        int cy = y + 5;
        if (t != null)
        {
            ctx.DrawString(t, bold, Color.FromArgb(235, 235, 240), x + 8, cy);
            cy += rowH + 2;
        }

        foreach (var r in tr)
        {
            ctx.DrawString(r.Name, font, Color.FromArgb(160, 165, 175), x + 8, cy);
            var vs = ctx.MeasureString(r.Value, bold);
            ctx.DrawString(r.Value, bold, r.Color.A > 0 ? r.Color : Color.FromArgb(235, 235, 240), x + w - vs.Width - 8, cy);
            cy += rowH;
        }

        return rect;
    }

    /// <summary>Un texto centrado en el panel (para «sin datos»).</summary>
    public static void Message(RenderContext ctx, Rectangle region, string text)
    {
        var font = new RenderFont("Arial", 9);
        var t = UiText.Tr(text);
        var size = ctx.MeasureString(t, font);
        int x = region.Left + Math.Max(8, (region.Width - size.Width) / 2), y = region.Top + Math.Max(18, (region.Height - size.Height) / 2);
        ctx.FillRectangle(Color.FromArgb(200, 18, 20, 26), new Rectangle(x - 8, y - 4, size.Width + 16, size.Height + 8), 4);
        ctx.DrawString(t, font, Color.FromArgb(200, 190, 196, 206), x, y);
    }

    /// <summary>Un anillo de 0 a 1 (fondo apagado y arco del color).</summary>
    public static void Ring(RenderContext ctx, int cx, int cy, int r, double frac, Color color)
    {
        var rect = new Rectangle(cx - r, cy - r, 2 * r, 2 * r);
        ctx.DrawEllipse(new RenderPen(Color.FromArgb(70, 140, 145, 160), 3), rect);
        if (!double.IsFinite(frac)) return;
        ctx.DrawArc(new RenderPen(color, 3), rect, -90, (float)(360 * Math.Clamp(frac, 0, 1)));
    }

    public static string Pct(double v, int dec = 1) => double.IsFinite(v) ? v.ToString("0." + new string('0', dec), System.Globalization.CultureInfo.InvariantCulture) + " %" : "—";

    public static string Num(double v, string fmt = "0.##") => double.IsFinite(v) ? v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) : "—";
}
