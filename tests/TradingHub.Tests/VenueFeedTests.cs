using TradingHub.Feeds;
using TradingHub.Feeds.Venues;
using TradingHub.Market;
using Xunit;

namespace TradingHub.Tests;

public class VenueFeedTests
{
    private static Instrument Inst(string sym, decimal tick, string bas = "BTC", string quote = "USDT") =>
        new() { Symbol = sym, FeedId = "x", TickSize = tick, BaseAsset = bas, QuoteAsset = quote };

    [Fact]
    public void Okx_pasa_contratos_a_monedas_y_encadena_el_libro()
    {
        var f = new OkxFeed(false);
        var i = Inst("BTC-USDT-SWAP", 0.1m);
        var s = new PublicVenueFeed.Session(i, new PublicVenueFeed.Spec("BTC-USDT-SWAP", 0.01m));
        var t = f.Process("""{"arg":{"channel":"trades","instId":"BTC-USDT-SWAP"},"data":[{"px":"70000.1","sz":"250","side":"sell","ts":"1730000000000"}]}""", s);
        Assert.Single(t);
        Assert.Equal(2.5m, t[0].Volume);
        Assert.Equal(TradeSide.Sell, t[0].Side);
        f.Process("""{"arg":{"channel":"books"},"action":"snapshot","data":[{"bids":[["70000","100","0","1"]],"asks":[["70000.5","50","0","1"]],"seqId":10,"prevSeqId":-1}]}""", s);
        f.Process("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[["70000","0","0","0"],["69999.9","10","0","1"]],"asks":[],"seqId":11,"prevSeqId":10}]}""", s);
        var snap = s.Book.BuildUpdate(DateTime.UtcNow).Snapshot;
        Assert.Equal(i.ToTicks(69999.9m), snap.Bids[0].PriceTicks);
        Assert.Equal(0.1m, snap.Bids[0].Size);
        Assert.Throws<PublicVenueFeed.VenueResyncException>(() =>
            f.Process("""{"arg":{"channel":"books"},"action":"update","data":[{"bids":[],"asks":[],"seqId":20,"prevSeqId":15}]}""", s));
    }

    [Fact]
    public void Deribit_inverso_divide_los_dolares_por_el_precio()
    {
        var f = new DeribitFeed();
        var i = Inst("BTC-PERPETUAL", 0.5m, quote: "USD");
        var s = new PublicVenueFeed.Session(i, new PublicVenueFeed.Spec("BTC-PERPETUAL", 1, Inverse: true));
        var t = f.Process("""{"params":{"channel":"trades.BTC-PERPETUAL.100ms","data":[{"price":80000,"amount":8000,"timestamp":1730000000000,"direction":"buy"}]}}""", s);
        Assert.Equal(0.1m, t[0].Volume);
        Assert.Equal(TradeSide.Buy, t[0].Side);
        f.Process("""{"params":{"channel":"book.BTC-PERPETUAL.100ms","data":{"type":"snapshot","change_id":5,"bids":[["new",80000,16000]],"asks":[["new",80000.5,8000]]}}}""", s);
        f.Process("""{"params":{"channel":"book.BTC-PERPETUAL.100ms","data":{"type":"change","change_id":6,"prev_change_id":5,"bids":[["delete",80000,0]],"asks":[]}}}""", s);
        var snap = s.Book.BuildUpdate(DateTime.UtcNow).Snapshot;
        Assert.Empty(snap.Bids);
        Assert.Equal(0.1m, snap.Asks[0].Size, 3);
    }

    [Fact]
    public void Coinbase_el_agresor_es_el_lado_contrario_del_match()
    {
        var f = new CoinbaseFeed();
        var s = new PublicVenueFeed.Session(Inst("BTC-USD", 0.01m, quote: "USD"), new PublicVenueFeed.Spec("BTC-USD"));
        var t = f.Process("""{"type":"match","price":"80000.01","size":"0.5","side":"sell","time":"2026-11-05T01:02:03.456Z"}""", s);
        Assert.Equal(TradeSide.Buy, t[0].Side);
        Assert.Equal(new DateTime(2026, 11, 5, 1, 2, 3, 456, DateTimeKind.Utc), t[0].Time);
        f.Process("""{"type":"snapshot","bids":[["80000","1.5"]],"asks":[["80000.02","2"]]}""", s);
        f.Process("""{"type":"l2update","changes":[["buy","80000","0"],["sell","80000.03","1"]]}""", s);
        var snap = s.Book.BuildUpdate(DateTime.UtcNow).Snapshot;
        Assert.Empty(snap.Bids);
        Assert.Equal(2, snap.Asks.Length);
    }

    [Fact]
    public void Hyperliquid_mexc_y_bitget_leen_libro_y_operaciones()
    {
        var hl = new HyperliquidFeed();
        var s = new PublicVenueFeed.Session(Inst("BTC", 1m, quote: "USD"), new PublicVenueFeed.Spec("BTC"));
        var t = hl.Process("""{"channel":"trades","data":[{"coin":"BTC","side":"A","px":"80001","sz":"0.3","time":1730000000000,"tid":1}]}""", s);
        Assert.Equal(TradeSide.Sell, t[0].Side);
        hl.Process("""{"channel":"l2Book","data":{"coin":"BTC","levels":[[{"px":"80000","sz":"2","n":3}],[{"px":"80010","sz":"1","n":1}]]}}""", s);
        Assert.Equal(2m, s.Book.BuildUpdate(DateTime.UtcNow).Snapshot.Bids[0].Size);
        Assert.Equal(1m, HyperliquidFeed.TickFor(80000, 5));
        Assert.Equal(0.00001m, HyperliquidFeed.TickFor(0.5m, 0));

        var mx = new MexcFeed();
        var sm = new PublicVenueFeed.Session(Inst("BTC_USDT", 0.1m), new PublicVenueFeed.Spec("BTC_USDT", 0.0001m));
        var tm = mx.Process("""{"channel":"push.deal","symbol":"BTC_USDT","data":{"p":80000.1,"v":1000,"T":1,"t":1730000000000,"i":5}}""", sm);
        Assert.Equal(0.1m, tm[0].Volume);
        Assert.Equal(TradeSide.Buy, tm[0].Side);

        var bg = new BitgetFeed(false);
        var sb = new PublicVenueFeed.Session(Inst("BTCUSDT", 0.1m), new PublicVenueFeed.Spec("BTCUSDT"));
        Assert.Empty(bg.Process("""{"action":"snapshot","arg":{"channel":"trade"},"data":[{"price":"1","size":"1","side":"buy","ts":"1"}]}""", sb));
        bg.Process("""{"action":"snapshot","arg":{"channel":"books"},"data":[{"bids":[["80000","1"]],"asks":[["80000.1","2"]],"seq":100,"pseq":99}]}""", sb);
        bg.Process("""{"action":"update","arg":{"channel":"books"},"data":[{"bids":[["80000","3"]],"asks":[],"seq":101,"pseq":100}]}""", sb);
        Assert.Equal(3m, sb.Book.BuildUpdate(DateTime.UtcNow).Snapshot.Bids[0].Size);
    }

    [Fact]
    public void El_agregado_empareja_por_moneda_y_prefiere_usdt()
    {
        var okx = new Instrument { Symbol = "BTC-USDT-SWAP", FeedId = "okx", TickSize = 0.1m, BaseAsset = "BTC", QuoteAsset = "USDT" };
        var cb = new Instrument { Symbol = "BTC-USD", FeedId = "cb", TickSize = 0.01m, BaseAsset = "BTC", QuoteAsset = "USD" };
        var eur = new Instrument { Symbol = "BTC-EUR", FeedId = "cb", TickSize = 0.01m, BaseAsset = "BTC", QuoteAsset = "EUR" };
        Assert.Equal("BTCUSDT", AggregateFeed.CanonicalSymbol(okx));
        Assert.Equal("BTCUSDT", AggregateFeed.CanonicalSymbol(cb));
        Assert.Null(AggregateFeed.CanonicalSymbol(eur));
        long slot = 0;
        var id = AggregateFeed.TaggedId(DateTime.UtcNow, 11, ref slot);
        Assert.Equal(11, AggregateFeed.SourceOf(id));
        Assert.Equal(12, CryptoVenues.All.Count);
        Assert.True(CryptoVenues.All.Count <= AggregateFeed.SourceSlots);
    }
}
