// Escena "datos-externos": las conexiones de datos de opciones y de flujos de ETF contra servidores DE MENTIRA: alta,
// prueba, que al guardarlas los indicadores tomen el proveedor solos (y lo suelten al desactivarlas) y la pantalla de
// cada una.
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using TradingHub.App;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Feeds.External;
using TradingHub.Tests;

internal static class ExternalDataScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine($"{(ok ? "OK " : "FALLA")}  {what}");
            if (!ok) failures++;
        }

        void Await(Task t)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!t.IsCompleted && DateTime.UtcNow < until) pump(0.05);
        }

        using var options = new FakeHttpServer();
        options.OnHttp = req => req.Headers.GetValueOrDefault("Authorization") != "Bearer opt-KEY"
            ? (401, "{}")
            : req.Route switch
            {
                "/v1/chain" => (200, """{"time":"2026-11-05T10:00:00Z","spot":70000,"contracts":[{"symbol":"C1","expiry":"2026-11-27T08:00:00Z","strike":72000,"right":"C","mark":925,"iv":0.52,"oi":120,"volume":30},{"symbol":"P1","expiry":"2026-11-27T08:00:00Z","strike":68000,"right":"P","mark":800,"iv":0.58,"oi":200,"volume":50}]}"""),
                "/v1/trades" => (200, "[]"),
                "/v1/daily" => (200, "[]"),
                _ => (404, "{}"),
            };
        using var etf = new FakeHttpServer();
        etf.OnHttp = _ => (200, $$"""[{"date":"{{DateTime.UtcNow.AddDays(-2):yyyy-MM-dd}}","net":-123400000},{"date":"{{DateTime.UtcNow.AddDays(-1):yyyy-MM-dd}}","netMillions":55.5}]""");

        var conns = App.Services.Connections;
        Check(!AppPaths.Root.StartsWith(TradingHub.Common.Product.AppData, StringComparison.Ordinal), "datos en una carpeta temporal (nunca los reales)");
        Check(OptionsData.Provider == null && OptionsData.Unavailable("BTC") != null, "sin conexión los indicadores de opciones dicen que no hay datos");
        conns.Profiles.RemoveAll(p => p.IsExternalData);
        var pOpt = new ConnectionProfile { Id = "options-data", Kind = ConnectionKind.OptionsData, Name = "Datos de opciones (API)", OptionsSource = OptionsSource.CustomJson, ExternalUrl = options.Http + "/v1", ExternalUnderlyings = "BTC" };
        var pEtf = new ConnectionProfile { Id = "etf-flows", Kind = ConnectionKind.EtfFlows, Name = "Flujos de ETF (API)", ExternalUrl = etf.Http };
        conns.Profiles.AddRange(new[] { pOpt, pEtf });
        conns.SetSecret(pOpt, "opt-KEY");
        conns.Save();
        Check(!pOpt.HasMarketData && !pEtf.HasMarketData, "no son mercados para gráficos");
        Check(OptionsData.Provider is JsonOptionsApi && EtfFlows.Provider is JsonEtfFlowsApi, "al guardar, los indicadores toman los proveedores solos");
        Check(OptionsData.Unavailable("BTC") == null && OptionsData.Unavailable("ETH") != null, "el subyacente que no está en la lista dice que no hay");
        foreach (var p in new[] { pOpt, pEtf })
        {
            var t = conns.TestAsync(p, CancellationToken.None);
            Await(t);
            var (ok, msg) = t.IsCompletedSuccessfully ? t.Result : (false, "sin respuesta");
            Check(ok, $"Probar {p.Name}: {msg}");
        }

        window.Navigate("connections");
        pump(1);
        var view = ((Dictionary<string, Control>)window.GetType().GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!)["connections"];
        foreach (var (p, name) in new[] { (pOpt, "opciones"), (pEtf, "etf") })
        {
            view.GetType().GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(view, new object[] { p });
            pump(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "conexion_" + name + ".png"));
        }

        pOpt.Enabled = false;
        conns.Save();
        Check(OptionsData.Provider == null && EtfFlows.Provider != null, "desactivada, los indicadores de opciones vuelven a «sin datos»");
        return failures;
    }
}
