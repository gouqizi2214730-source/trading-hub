using TradingHub.App.Services;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;

namespace TradingHub.Tests;

public class LineWidthTests
{
    [Fact]
    public void Grosor_y_estilo_de_niveles_y_lineas_se_guardan_y_se_restauran()
    {
        var rsi = new Rsi();
        var level = rsi.LineSeries.First(l => l.Id == "70");
        level.Width = 3;
        level.LineDashStyle = LineDashStyle.Dot;
        level.Value = 75;
        var props = Workspace.SaveIndicator(rsi, out var series);

        var copy = new Rsi();
        Workspace.LoadIndicator(copy, props, series);
        var l2 = copy.LineSeries.First(l => l.Id == "70");
        Assert.Equal(3, l2.Width);
        Assert.Equal(LineDashStyle.Dot, l2.LineDashStyle);
        Assert.Equal(75m, l2.Value);

        var vp = new SessionVolumeProfile { PocWidth = 4, ValueAreaLineWidth = 2, ValueAreaLineStyle = LineDashStyle.Solid };
        var vpProps = Workspace.SaveIndicator(vp, out var vpSeries);
        var vp2 = new SessionVolumeProfile();
        Workspace.LoadIndicator(vp2, vpProps, vpSeries);
        Assert.Equal(4, vp2.PocWidth);
        Assert.Equal(2, vp2.ValueAreaLineWidth);
        Assert.Equal(LineDashStyle.Solid, vp2.ValueAreaLineStyle);

        var bt = new BigTrades { BorderWidth = 3 };
        var btProps = Workspace.SaveIndicator(bt, out var btSeries);
        var bt2 = new BigTrades();
        Workspace.LoadIndicator(bt2, btProps, btSeries);
        Assert.Equal(3, bt2.BorderWidth);
    }
}
