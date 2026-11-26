using TradingHub.Market;

namespace TradingHub.Trading;

/// <summary>
/// Servicio de entrada de órdenes y dimensionamiento de posición desacoplado de la interfaz Avalonia:
/// centraliza el cálculo de tamaño por riesgo monetario/porcentual, la validación de pasos de precio y volumen,
/// la colocación de órdenes en escalera (ladder), el giro de posición (reverse) y la vinculación con <see cref="BracketManager"/>.
/// </summary>
public sealed class OrderEntryService
{
    public OrderEntryService(ITradingAccount account, BracketManager? brackets = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        Brackets = brackets;
    }

    public ITradingAccount Account { get; }
    public BracketManager? Brackets { get; }
    public Instrument Instrument => Account.Instrument;

    /// <summary>Redondea hacia abajo una cantidad al <see cref="Instrument.VolumeStep"/> del instrumento.</summary>
    public decimal NormalizeQuantity(decimal quantity)
    {
        decimal step = Instrument.VolumeStep > 0 ? Instrument.VolumeStep : 1m;
        if (quantity <= 0) return 0m;
        return Math.Floor(quantity / step) * step;
    }

    /// <summary>
    /// Calcula el tamaño de posición exacto (redondeado hacia abajo al paso de volumen) para arriesgar como máximo
    /// <paramref name="riskPercent"/> % del capital <paramref name="accountEquity"/> entre <paramref name="entryPrice"/> y <paramref name="stopPrice"/>.
    /// </summary>
    public decimal CalculateQuantityByRisk(decimal accountEquity, decimal riskPercent, decimal entryPrice, decimal stopPrice)
    {
        if (accountEquity <= 0 || riskPercent <= 0) return 0m;
        decimal dist = Math.Abs(entryPrice - stopPrice);
        if (dist <= 0) return 0m;
        decimal pv = Instrument.PointValue > 0 ? Instrument.PointValue : 1m;
        decimal riskPerUnit = dist * pv;
        if (riskPerUnit <= 0) return 0m;
        decimal budget = accountEquity * (riskPercent / 100m);
        return NormalizeQuantity(budget / riskPerUnit);
    }

    /// <summary>Envía una orden a mercado validada y, si corresponde, aplica la plantilla de brackets.</summary>
    public Order SubmitMarket(OrderDirections direction, decimal quantity, string comment = "")
    {
        decimal qty = NormalizeQuantity(quantity);
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad normalizada debe ser mayor que cero.");
        return Account.Market(direction, qty, comment);
    }

    /// <summary>Envía una orden límite o stop con precio redondeado al tick y adjunta brackets pendientes si la plantilla está activa.</summary>
    public Order SubmitEntryOrder(OrderTypes type, OrderDirections direction, decimal quantity, decimal price, bool attachBracketTemplate = true, string comment = "")
    {
        decimal qty = NormalizeQuantity(quantity);
        if (qty <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad normalizada debe ser mayor que cero.");
        decimal roundedPrice = Instrument.RoundPrice(price);
        Order order = type == OrderTypes.Stop
            ? Account.Stop(direction, qty, roundedPrice, comment)
            : Account.Limit(direction, qty, roundedPrice, comment);

        if (attachBracketTemplate && Brackets is { Template.Enabled: true } && order.State != OrderStates.Failed)
        {
            Brackets.AttachTemplate(order);
        }

        return order;
    }

    /// <summary>Invierte la posición abierta actual (cierra y abre del mismo tamaño en dirección contraria).</summary>
    public Order? ReversePosition(string comment = "Reverse")
    {
        decimal vol = Account.Position?.Volume ?? 0m;
        if (vol == 0) return null;
        var dir = vol > 0 ? OrderDirections.Sell : OrderDirections.Buy;
        decimal doubleQty = NormalizeQuantity(Math.Abs(vol) * 2m);
        if (doubleQty <= 0) return null;
        return Account.Market(dir, doubleQty, comment);
    }

    /// <summary>
    /// Coloca una escalera (ladder) de <paramref name="rungs"/> órdenes límite distribuyendo <paramref name="totalQuantity"/>
    /// uniformemente entre <paramref name="startPrice"/> y <paramref name="endPrice"/>.
    /// </summary>
    public IReadOnlyList<Order> PlaceLimitLadder(OrderDirections direction, decimal totalQuantity, decimal startPrice, decimal endPrice, int rungs, bool attachBrackets = false)
    {
        if (rungs <= 0) throw new ArgumentOutOfRangeException(nameof(rungs));
        decimal total = NormalizeQuantity(totalQuantity);
        if (total <= 0) return Array.Empty<Order>();

        decimal step = Instrument.VolumeStep > 0 ? Instrument.VolumeStep : 1m;
        int actualRungs = (int)Math.Min(rungs, Math.Max(1, Math.Floor(total / step)));
        decimal perRung = NormalizeQuantity(total / actualRungCount(actualRungs));
        decimal remainder = total - perRung * actualRungs;

        var orders = new List<Order>(actualRungs);
        for (int i = 0; i < actualRungs; i++)
        {
            decimal t = actualRungs == 1 ? 0m : (decimal)i / (actualRungs - 1);
            decimal px = Instrument.RoundPrice(startPrice + (endPrice - startPrice) * t);
            decimal qty = i == actualRungs - 1 ? perRung + remainder : perRung;
            qty = NormalizeQuantity(qty);
            if (qty <= 0) continue;
            orders.Add(SubmitEntryOrder(OrderTypes.Limit, direction, qty, px, attachBrackets, $"Ladder {i + 1}/{actualRungs}"));
        }

        return orders;
    }

    private static int actualRungCount(int rungs) => Math.Max(1, rungs);
}
