using TradingHub.App.Services;

namespace TradingHub.Tests;

public class NetworkProxyTests
{
    [Theory]
    [InlineData("127.0.0.1:7890", "http://127.0.0.1:7890")]
    [InlineData("socks5://127.0.0.1:7891", "socks5://127.0.0.1:7891")]
    [InlineData(" http://127.0.0.1:10809/ ", "http://127.0.0.1:10809")]
    [InlineData("", null)]
    [InlineData("hola", null)]
    public void Normaliza_la_direccion(string input, string? expected) => Assert.Equal(expected, NetworkProxy.Normalize(input));

    [Fact]
    public void Un_corte_de_red_sugiere_el_proxy()
    {
        var ex = new HttpRequestException("The SSL connection could not be established, see inner exception.", new IOException("Received an unexpected EOF"));
        var msg = NetworkProxy.Explain(ex);
        Assert.Contains("Proxy de red", msg);
        Assert.Contains("unexpected EOF", msg);
    }
}
