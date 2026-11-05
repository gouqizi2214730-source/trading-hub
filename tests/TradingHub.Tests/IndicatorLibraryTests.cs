using TradingHub.App.Services;
using TradingHub.Indicators;
using Xunit;

namespace TradingHub.Tests;

public class IndicatorLibraryTests
{
    private static List<IndicatorInfo> Items()
    {
        var cat = new IndicatorCatalog();
        cat.Scan(typeof(TradingHub.Indicators.Native.BigTrades).Assembly, "Trading Hub");
        return cat.Items.ToList();
    }

    [Fact]
    public void De_fabrica_va_por_grupos_en_orden()
    {
        var items = Items();
        var groups = IndicatorLibrary.Build(items, new LibraryLayout());
        Assert.Equal(items.Count, groups.Sum(g => g.Items.Count));
        var ids = groups.Select(g => g.Id).ToList();
        // en el orden de 01 … 15, sin grupos vacíos
        var known = ids.Where(id => IndicatorCategories.Order.Contains(id)).ToList();
        Assert.Equal(known.OrderBy(id => Array.IndexOf(IndicatorCategories.Order, id)).ToList(), known);
        Assert.Contains(IndicatorCategories.OrderFlow, ids);
        Assert.DoesNotContain(IndicatorCategories.VolumeOrderFlow, ids); // las categorías viejas caen en las nuevas
        Assert.All(groups, g => Assert.NotEmpty(g.Items));
    }

    [Fact]
    public void Mover_ordenar_renombrar_y_volver_a_fabrica()
    {
        var layout = new LibraryLayout();
        try
        {
            Run(layout);
        }
        finally
        {
            IndicatorLibrary.Reset(layout);
        }
    }

    private static void Run(LibraryLayout layout)
    {
        var items = Items();
        var groups = IndicatorLibrary.Build(items, layout);
        var flow = groups.First(g => g.Id == IndicatorCategories.OrderFlow);
        var a = flow.Items[0];
        var b = flow.Items[1];

        // un grupo nuevo debajo de order flow con el primero
        string id = IndicatorLibrary.CreateGroup(layout, items, "Mis favoritos", flow.Id);
        IndicatorLibrary.Move(layout, items, a.Type.FullName!, id, null);
        groups = IndicatorLibrary.Build(items, layout);
        int fi = groups.FindIndex(g => g.Id == flow.Id);
        Assert.Equal(id, groups[fi + 1].Id);
        Assert.Equal("Mis favoritos", groups[fi + 1].Name);
        Assert.Equal(new[] { a }, groups[fi + 1].Items);
        Assert.DoesNotContain(a, groups[fi].Items);

        // b antes que a en el grupo nuevo, después sube y baja
        IndicatorLibrary.Move(layout, items, b.Type.FullName!, id, a.Type.FullName);
        Assert.Equal(new[] { b, a }, IndicatorLibrary.Build(items, layout).First(g => g.Id == id).Items);
        IndicatorLibrary.Shift(layout, items, a.Type.FullName!, -1);
        Assert.Equal(new[] { a, b }, IndicatorLibrary.Build(items, layout).First(g => g.Id == id).Items);

        // el grupo sube
        IndicatorLibrary.ShiftGroup(layout, items, id, -1);
        groups = IndicatorLibrary.Build(items, layout);
        Assert.Equal(id, groups[groups.FindIndex(g => g.Id == flow.Id) - 1].Id);

        // nombres: en la biblioteca y en los gráficos
        IndicatorLibrary.Rename(layout, a.Type.FullName!, "Mi Big Trades");
        Assert.Equal("Mi Big Trades", Indicator.DefaultName(a.Type));
        var inst = (Indicator)Activator.CreateInstance(a.Type)!;
        Assert.Equal("Mi Big Trades", inst.Name);

        // borrar el grupo: vuelven a su grupo de fábrica
        IndicatorLibrary.DeleteGroup(layout, id);
        groups = IndicatorLibrary.Build(items, layout);
        Assert.DoesNotContain(groups, g => g.Id == id);
        Assert.Contains(a, groups.First(g => g.Id == flow.Id).Items);
        Assert.Equal(items.Count, groups.Sum(g => g.Items.Count));

        // los ajustes se guardan en JSON
        var json = System.Text.Json.JsonSerializer.Serialize(layout);
        var back = System.Text.Json.JsonSerializer.Deserialize<LibraryLayout>(json)!;
        Assert.Equal("Mi Big Trades", back.Names[a.Type.FullName!]);

        IndicatorLibrary.Reset(layout);
        Assert.Equal(Indicator.FactoryName(a.Type), Indicator.DefaultName(a.Type));
        Assert.Empty(layout.Groups);
    }
}
