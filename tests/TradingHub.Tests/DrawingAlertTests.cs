using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Data;
using Xunit;

namespace TradingHub.Tests;

/// <summary>Alertas sobre lo dibujado (líneas, canales, rectángulos) con cruza, toca, entra y sale, como en TradingView.</summary>
public class DrawingAlertTests
{
    private static ChartModel Model()
    {
        var data = VpUltimateTests.Market(4);
        var model = new ChartModel(data, ChartPeriod.Parse("M15"), new ChartSettings { TimeZoneId = "UTC" });
        model.Update();
        return model;
    }

    [Fact]
    public void Un_canal_paralelo_da_sus_tres_lineas_en_cada_vela()
    {
        var m = Model();
        var s = m.Series;
        var d = new ChartDrawing { Kind = DrawingKind.ParallelChannel, ExtendRight = true };
        d.Points.Add(new ChartPoint(s[10].Time, 100));
        d.Points.Add(new ChartPoint(s[20].Time, 110));
        d.InitChannel(5);
        Assert.Equal(105, d.ValueAt(s, 10, "upper"));
        Assert.Equal(100, d.ValueAt(s, 10, "lower"));
        Assert.Equal(107.5, d.ValueAt(s, 15, "middle"));
        Assert.Equal(120, d.ValueAt(s, 30, "lower")); // extendido a la derecha
        Assert.Null(d.ValueAt(s, 5, "lower")); // no extendido a la izquierda

        // mover el primer punto conserva el ancho; el tercer manejador lo cambia
        d.MoveHandle(0, new ChartPoint(s[10].Time, 90));
        Assert.Equal(5, d.ChannelOffset, 6);
        d.MoveHandle(2, new ChartPoint(s[15].Time, 120)); // la base a mitad de camino está en 100
        Assert.Equal(20, d.ChannelOffset, 6);
    }

    [Fact]
    public void El_precio_entra_y_sale_de_un_canal_dibujado_y_toca_una_linea()
    {
        var m = Model();
        var s = m.Series;
        int n = s.Count;
        decimal Px(int b) => m.Instrument.FromTicks(s[b].CloseTicks);
        decimal lo = Enumerable.Range(0, n).Min(Px), hi = Enumerable.Range(0, n).Max(Px);
        decimal mid = (lo + hi) / 2, band = (hi - lo) / 6;
        var rect = new ChartDrawing { Kind = DrawingKind.Rectangle, ExtendLeft = true, ExtendRight = true };
        rect.Points.Add(new ChartPoint(s[0].Time, (double)(mid + band)));
        rect.Points.Add(new ChartPoint(s[n - 1].Time, (double)(mid - band)));
        m.Drawings.Add(rect);
        var lines = ChartAlertEngine.DrawingLines(m, rect);
        Assert.Equal(new[] { "top", "bottom" }, lines.Select(l => l.Series));
        var enter = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.EnteringChannel, Right = lines[0], Lower = lines[1] };
        var exit = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.ExitingChannel, Right = lines[0], Lower = lines[1] };
        int enters = 0, exits = 0;
        for (int b = 1; b < n; b++)
        {
            if (ChartAlertEngine.Check(m, enter, b) == true) enters++;
            if (ChartAlertEngine.Check(m, exit, b) == true) exits++;
        }

        Assert.True(enters > 0 && exits > 0);

        // una horizontal en el máximo de una vela: «toca» con el rango de la vela
        int k = n / 2;
        double high = (double)m.Instrument.FromTicks(s[k].HighTicks);
        var h = new ChartDrawing { Kind = DrawingKind.HorizontalLine };
        h.Points.Add(new ChartPoint(s[k].Time, high));
        m.Drawings.Add(h);
        var touch = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.Touching, Right = ChartAlertEngine.DrawingLines(m, h)[0] };
        Assert.True(ChartAlertEngine.Check(m, touch, k));
        Assert.True(touch.Uses(h.Id.ToString("N")));

        // sube más de 0 % en 1 vela: se cumple en alguna
        var up = new ChartAlert { Left = AlertOperand.Price(), Condition = AlertCondition.MovingUpPercent, Right = AlertOperand.Fixed(0), Bars = 1 };
        Assert.Contains(Enumerable.Range(1, n - 1), b => ChartAlertEngine.Check(m, up, b) == true);
    }
}
