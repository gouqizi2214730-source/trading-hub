using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingHub.Attributes;

namespace TradingHub.Charting;

/// <summary>Tipo de pieza (columna) dentro de cada vela del footprint.</summary>
public enum FootprintColumnKind
{
    [Display(Name = "Vela finita")] Candle,
    [Display(Name = "Celdas coloreadas")] Cells,
    [Display(Name = "Escalera (números)")] Ladder,
    [Display(Name = "Perfil (histograma)")] Profile,
    [Display(Name = "Espacio")] Spacer,
}

/// <summary>Dato que muestra una columna.</summary>
public enum FootprintValue
{
    [Display(Name = "Volumen")] Volume,
    [Display(Name = "Delta")] Delta,
    [Display(Name = "Bid (ventas agresivas)")] Bid,
    [Display(Name = "Ask (compras agresivas)")] Ask,
    [Display(Name = "Bid x Ask")] BidAsk,
    [Display(Name = "Operaciones")] Trades,
}

/// <summary>Hacia dónde crecen las barras de un perfil.</summary>
public enum ProfileDirection
{
    [Display(Name = "De izquierda a derecha")] LeftToRight,
    [Display(Name = "De derecha a izquierda")] RightToLeft,
    [Display(Name = "Desde el centro (delta / bid-ask)")] Centered,
}

/// <summary>Qué niveles se resaltan.</summary>
public enum HighlightRule
{
    [Display(Name = "Ninguno")] None,
    [Display(Name = "Delta fuerte en el nivel")] StrongDelta,
    [Display(Name = "Desequilibrios diagonales")] Imbalance,
    [Display(Name = "POC de la vela")] Poc,
    [Display(Name = "Volumen alto (sobre el % del POC)")] HighVolume,
}

public enum ColumnTextAlign
{
    [Display(Name = "Izquierda")] Left,
    [Display(Name = "Centro")] Center,
    [Display(Name = "Derecha")] Right,
}

/// <summary>Una columna (pieza) del footprint.</summary>
public sealed class FootprintColumn
{
    [Display(Name = "Tipo")]
    public FootprintColumnKind Kind { get; set; } = FootprintColumnKind.Cells;

    [Display(Name = "Dato")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public FootprintValue Value { get; set; } = FootprintValue.BidAsk;

    /// <summary>Ancho relativo (las velas finitas tienen ancho fijo en píxeles).</summary>
    [Display(Name = "Ancho", Description = "Relativo a las demás columnas de la vela")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile, FootprintColumnKind.Spacer)]
    public double Width { get; set; } = 1;

    [Display(Name = "Dirección de las barras")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Profile)]
    public ProfileDirection Direction { get; set; } = ProfileDirection.LeftToRight;

    [Display(Name = "Resaltar")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public HighlightRule Highlight { get; set; } = HighlightRule.None;

    [Display(Name = "Colores de las celdas")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells)]
    public ClusterColorScheme ColorScheme { get; set; } = ClusterColorScheme.VolumeProportion;

    [Display(Name = "Alineación del texto")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder)]
    public ColumnTextAlign Align { get; set; } = ColumnTextAlign.Right;

    /// <summary>En los perfiles, escribir el valor sobre la barra.</summary>
    [Display(Name = "Números sobre las barras")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Profile)]
    public bool ShowNumbers { get; set; }

    /// <summary>Colores propios (ARGB). Null = los del tema.</summary>
    [Display(Name = "Color positivo", GroupName = "Colores de fondo", Description = "Fondo o barra del ask, del delta positivo y de lo resaltado a favor. Automático = el del tema.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? PositiveColor { get; set; }

    [Display(Name = "Color negativo", GroupName = "Colores de fondo", Description = "Fondo o barra del bid, del delta negativo y de lo resaltado en contra. Automático = el del tema.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? NegativeColor { get; set; }

    [Display(Name = "Color neutro", GroupName = "Colores de fondo", Description = "Fondo o barra del volumen y de lo resaltado sin lado. Automático = el del tema.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? NeutralColor { get; set; }

    /// <summary>Color de los números (null = el del tema). Los de abajo, si están, mandan sobre este.</summary>
    [Display(Name = "Color del texto", GroupName = "Colores del texto", Description = "Todos los números de la columna, salvo los que tengan su propio color (delta, bid, ask). Automático = el del tema.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? TextColor { get; set; }

    [Display(Name = "Texto del delta positivo", GroupName = "Colores del texto")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Delta)]
    public uint? PositiveTextColor { get; set; }

    [Display(Name = "Texto del delta negativo", GroupName = "Colores del texto")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Delta)]
    public uint? NegativeTextColor { get; set; }

    [Display(Name = "Texto del bid", GroupName = "Colores del texto", Description = "Los desequilibrios marcados mantienen su color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Bid, FootprintValue.BidAsk)]
    public uint? BidTextColor { get; set; }

    [Display(Name = "Texto del ask", GroupName = "Colores del texto", Description = "Los desequilibrios marcados mantienen su color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Ask, FootprintValue.BidAsk)]
    public uint? AskTextColor { get; set; }

    [Display(Name = "Texto en niveles resaltados", GroupName = "Colores del texto", Description = "Automático = blanco.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder)]
    [VisibleWhen(nameof(Highlight), HighlightRule.StrongDelta, HighlightRule.Imbalance, HighlightRule.Poc, HighlightRule.HighVolume)]
    public uint? HighlightTextColor { get; set; }

    public FootprintColumn Clone() => (FootprintColumn)MemberwiseClone();

    public override string ToString() => Kind switch
    {
        FootprintColumnKind.Candle => "Vela",
        FootprintColumnKind.Spacer => "Espacio",
        _ => $"{Kind} · {Value}",
    };
}

/// <summary>
/// Cómo se arma cada vela del footprint: una lista de columnas de izquierda a derecha.
/// Se guarda como plantilla JSON.
/// </summary>
public sealed class FootprintLayout
{
    public string Name { get; set; } = "Personalizado";
    public List<FootprintColumn> Columns { get; set; } = new();

    /// <summary>Delta de la vela arriba y volumen total abajo.</summary>
    [Display(Name = "Totales de cada vela", Description = "Delta de la vela arriba y volumen total abajo")]
    public bool ShowTotals { get; set; }

    /// <summary>Recuadro en el POC de cada vela (en las columnas de celdas).</summary>
    [Display(Name = "Recuadro en el POC", Description = "En las columnas de celdas")]
    public bool ShowPoc { get; set; } = true;

    /// <summary>Grosor del recuadro del POC en píxeles (0 = automático: 1,5 con filas altas, 1 con filas bajas).</summary>
    [Display(Name = "Grosor del recuadro del POC", Description = "En píxeles; 0 = automático")]
    [Range(0, 6)]
    public decimal PocBorderWidth { get; set; }

    /// <summary>Resaltar desequilibrios en el texto de bid x ask.</summary>
    [Display(Name = "Marcar desequilibrios", Description = "En el texto de bid x ask (diagonal)")]
    public bool ShowImbalances { get; set; } = true;

    [Display(Name = "Desequilibrio: proporción", Description = "Cuántas veces más grande tiene que ser un lado que el otro (en diagonal)")]
    public decimal ImbalanceRatio { get; set; } = 3m;

    [Display(Name = "Desequilibrio: volumen mínimo")]
    public decimal ImbalanceMinVolume { get; set; }

    /// <summary>Para "delta fuerte": |delta| / volumen del nivel (0.45 ≈ 72/28).</summary>
    [Display(Name = "Delta fuerte desde", Description = "|delta| / volumen del nivel (0,45 ≈ 72% de un lado)")]
    public decimal StrongDeltaRatio { get; set; } = 0.45m;

    /// <summary>Volumen mínimo del nivel (fracción del POC) para resaltar.</summary>
    [Display(Name = "Volumen mínimo para resaltar", Description = "Fracción del volumen del POC (0,3 = 30%)")]
    public decimal HighlightMinVolumeFraction { get; set; } = 0.3m;

    /// <summary>Colores proporcionales al máximo de la vela (true) o de todo lo visible.</summary>
    [Display(Name = "Colores proporcionales a cada vela", Description = "Apagado: al máximo de todo lo visible")]
    public bool ProportionPerCandle { get; set; } = true;

    public FootprintLayout Clone()
    {
        var c = (FootprintLayout)MemberwiseClone();
        c.Columns = Columns.Select(x => x.Clone()).ToList();
        return c;
    }

    // ------------------------------------------------------------------ plantillas

    private static FootprintColumn Candle() => new() { Kind = FootprintColumnKind.Candle };

    public static IReadOnlyList<FootprintLayout> Presets { get; } = new[]
    {
        new FootprintLayout
        {
            Name = "Bid x Ask (clásico)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.BidAsk, ColorScheme = ClusterColorScheme.VolumeProportion } },
        },
        new FootprintLayout
        {
            Name = "Escalera de volumen + perfil",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Highlight = HighlightRule.StrongDelta, Direction = ProfileDirection.LeftToRight, Width = 1.3 },
            },
        },
        new FootprintLayout
        {
            Name = "Escalera de delta + perfil de volumen",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Direction = ProfileDirection.LeftToRight, Width = 1.3 },
            },
        },
        new FootprintLayout
        {
            Name = "Doble escalera (bid | ask)",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Bid, Highlight = HighlightRule.Imbalance, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Ask, Highlight = HighlightRule.Imbalance, Align = ColumnTextAlign.Left, Width = 1 },
            },
        },
        new FootprintLayout
        {
            Name = "Doble escalera de delta",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Highlight = HighlightRule.HighVolume, Align = ColumnTextAlign.Left, Width = 1 },
            },
        },
        new FootprintLayout
        {
            Name = "Volumen (celdas)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.Volume } },
        },
        new FootprintLayout
        {
            Name = "Delta (celdas)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.Delta, ColorScheme = ClusterColorScheme.Delta } },
        },
        new FootprintLayout
        {
            Name = "Histograma bid / ask",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.BidAsk, Direction = ProfileDirection.Centered, ShowNumbers = true } },
        },
        new FootprintLayout
        {
            Name = "Perfil de volumen por vela",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, ShowNumbers = true } },
        },
        new FootprintLayout
        {
            Name = "Bid x Ask + perfil",
            Columns =
            {
                Candle(),
                new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.BidAsk, Width = 1.4 },
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Width = 0.8 },
            },
        },
    };

    // ------------------------------------------------------------------ persistencia

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FootprintLayout? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<FootprintLayout>(json, Json); }
        catch { return null; }
    }

    /// <summary>Carpeta de plantillas del usuario (con TRADINGHUB_DATA_DIR, la de las pruebas).</summary>
    public static string TemplatesFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } dir ? dir : TradingHub.Common.Product.AppData, "FootprintTemplates");

    public static List<FootprintLayout> LoadUserTemplates()
    {
        var list = new List<FootprintLayout>();
        if (!Directory.Exists(TemplatesFolder)) return list;
        foreach (var f in Directory.EnumerateFiles(TemplatesFolder, "*.json"))
        {
            var l = FromJson(File.ReadAllText(f));
            if (l != null) list.Add(l);
        }

        return list;
    }

    public void SaveAsTemplate()
    {
        Directory.CreateDirectory(TemplatesFolder);
        var safe = string.Concat(Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        File.WriteAllText(Path.Combine(TemplatesFolder, safe + ".json"), ToJson());
    }
}
