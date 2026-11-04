using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TradingHub.Scripting;

/// <summary>
/// KVS: el lenguaje simple de scripts de KVP, parecido al «wrun» de openmarket (y entendiendo también su forma con
/// llamadas: <c>param("n", 20, {...})</c>, <c>input("c", ohlcv.close)</c>, <c>output("x", line, overlay)</c>,
/// <c>function onBar() {...}</c>, <c>out_x(...)</c>). Se traduce a un indicador de C# y se compila como los demás;
/// los errores salen con la línea del archivo .kvs.
/// <code>
/// name "Bandas EMA"
/// param length = 20 { min: 1, max: 500, description: "Velas" }
/// param mult = 2
/// input src = ohlcv.close
/// output basis = line { overlay: true, color: "#f2b53a", width: 2 }
/// output upper = line { overlay: true, color: "#3d8bfd" }
/// onBar {
///   basis = ema(src, length)
///   upper = basis + mult * stdev(src, length)
///   if (crossover(src, upper)) { alert("rompió la banda") }
/// }
/// </code>
/// </summary>
public static class KvsTranslator
{
    private sealed record Param(string Name, string Default, bool IsBool, string? Min, string? Max, string? Description, int Line);

    private sealed record Input(string Name, string Source, Dictionary<string, string> Options, int Line);

    private sealed record Output(string Name, string Kind, Dictionary<string, string> Options, int Line);

    private static readonly Dictionary<string, string> Implicit = new(StringComparer.Ordinal)
    {
        ["open"] = "ohlcv.open", ["high"] = "ohlcv.high", ["low"] = "ohlcv.low", ["close"] = "ohlcv.close", ["volume"] = "ohlcv.volume",
        ["hl2"] = "ohlcv.hl2", ["hlc3"] = "ohlcv.hlc3", ["ohlc4"] = "ohlcv.ohlc4", ["time"] = "ohlcv.time", ["delta"] = "trades.delta",
        ["buyvol"] = "trades.buy", ["sellvol"] = "trades.sell", ["trades"] = "trades.count",
    };

    private static readonly HashSet<string> Windowed = new(StringComparer.OrdinalIgnoreCase) { "ema", "rma", "sma", "wma", "stdev", "highest", "lowest", "sum", "change" };
    private static readonly HashSet<string> Stateful = new(StringComparer.OrdinalIgnoreCase) { "cum", "crossover", "crossunder" };

    private static readonly Dictionary<string, string> MathFns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["abs"] = "Math.Abs", ["min"] = "Math.Min", ["max"] = "Math.Max", ["sqrt"] = "Math.Sqrt", ["log"] = "Math.Log", ["log10"] = "Math.Log10",
        ["exp"] = "Math.Exp", ["pow"] = "Math.Pow", ["round"] = "Math.Round", ["floor"] = "Math.Floor", ["ceil"] = "Math.Ceiling", ["sign"] = "(double)Math.Sign",
        ["nz"] = "KvsRuntime.Nz", ["na"] = "KvsRuntime.Na", ["sin"] = "Math.Sin", ["cos"] = "Math.Cos", ["tan"] = "Math.Tan",
    };

    /// <summary>Nombre de la clase de C# de un archivo .kvs (Kvs_ + el nombre del archivo).</summary>
    public static string ClassNameFor(string fileNameWithoutExtension)
    {
        var sb = new StringBuilder("Kvs_");
        foreach (var c in fileNameWithoutExtension) sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
        return sb.ToString();
    }

    /// <summary>Error de un script, con su línea (1 = la primera).</summary>
    public sealed class KvsException : Exception
    {
        public KvsException(int line, string message) : base(message) => Line = line;

        public int Line { get; }
    }

    private static string StripComments(string src)
    {
        // /* … */ (respetando los saltos de línea para que las líneas no cambien) y // …
        src = Regex.Replace(src, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var lines = src.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            bool inStr = false;
            for (int k = 0; k < l.Length - 1; k++)
            {
                if (l[k] == '"') inStr = !inStr;
                if (!inStr && l[k] == '/' && l[k + 1] == '/')
                {
                    lines[i] = l[..k];
                    break;
                }
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>La forma con llamadas de wrun → la forma corta de KVS.</summary>
    private static string Normalize(string line)
    {
        var t = line.Trim();
        var m = Regex.Match(t, @"^(?:const\s+\w+\s*=\s*)?param\(\s*""(\w+)""\s*,\s*([^,{)]+?)\s*(?:,\s*(\{.*\}))?\s*\)\s*;?$");
        if (m.Success) return $"param {m.Groups[1].Value} = {m.Groups[2].Value} {m.Groups[3].Value}";
        m = Regex.Match(t, @"^(?:const\s+\w+\s*=\s*)?input\(\s*""(\w+)""\s*,\s*([\w.]+)\s*(?:,\s*(\{.*\}))?\s*\)\s*;?$");
        if (m.Success) return $"input {m.Groups[1].Value} = {m.Groups[2].Value} {m.Groups[3].Value}";
        m = Regex.Match(t, @"^(?:const\s+\w+\s*=\s*)?output\(\s*""(\w+)""\s*,\s*(\w+)\s*(?:,\s*(overlay|lower|panel))?\s*(?:,\s*\{(.*)\})?\s*\)\s*;?$");
        if (m.Success)
        {
            string opts = m.Groups[4].Value.Trim();
            string where = m.Groups[3].Value == "overlay" ? "overlay: true" : m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? "overlay: false" : "";
            string all = string.Join(", ", new[] { where, opts }.Where(x => x.Length > 0));
            return $"output {m.Groups[1].Value} = {m.Groups[2].Value} {{ {all} }}";
        }

        t = Regex.Replace(t, @"^(?:export\s+)?function\s+(onBar|onStart)\s*\(\s*\)\s*\{", "$1 {");
        return t;
    }

    /// <summary>«{ min: 1, color: "#fff", colors: ["#a", "#b"] }» → diccionario (los valores como texto, sin comillas).</summary>
    private static Dictionary<string, string> Options(string text, int line)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.Trim();
        if (text.Length == 0) return d;
        if (!text.StartsWith('{') || !text.EndsWith('}')) throw new KvsException(line, "Las opciones van entre llaves: { clave: valor, … }");
        var body = text[1..^1];
        foreach (Match m in Regex.Matches(body, @"(\w+)\s*:\s*(""[^""]*""|\[[^\]]*\]|[^,]+)"))
            d[m.Groups[1].Value] = m.Groups[2].Value.Trim().Trim('"');
        return d;
    }

    private static string Num(string s, int line)
    {
        s = s.Trim();
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v.ToString("R", CultureInfo.InvariantCulture) + "d";
        throw new KvsException(line, $"«{s}» no es un número");
    }

    private static string Str(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Traduce un script .kvs a C#. <paramref name="path"/> se usa en las directivas #line (errores con la línea del .kvs).</summary>
    public static string Translate(string source, string fileNameWithoutExtension, string path)
    {
        var lines = StripComments(source).Split('\n');
        string display = fileNameWithoutExtension;
        var parms = new List<Param>();
        var inputs = new List<Input>();
        var outputs = new List<Output>();
        var levels = new List<(string Value, string? Color, int Line)>();
        var onBar = new List<(string Text, int Line)>();
        var onStart = new List<(string Text, int Line)>();
        List<(string, int)>? block = null;
        int depth = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            int ln = i + 1;
            var raw = lines[i];
            var t = Normalize(raw);
            if (t.Length == 0) continue;
            if (block != null)
            {
                int opens = t.Count(c => c == '{'), closes = t.Count(c => c == '}');
                if (depth + opens - closes <= 0 && t == "}")
                {
                    block = null;
                    depth = 0;
                    continue;
                }

                depth += opens - closes;
                if (depth <= 0)
                {
                    // «x = 1 }»: lo de antes de la llave de cierre del bloque
                    var inner = t[..t.LastIndexOf('}')].Trim();
                    if (inner.Length > 0) block.Add((inner, ln));
                    block = null;
                    depth = 0;
                    continue;
                }

                block.Add((t, ln));
                continue;
            }

            Match m;
            if ((m = Regex.Match(t, @"^name\s+""([^""]+)""\s*;?$")).Success)
            {
                display = m.Groups[1].Value;
                continue;
            }

            if ((m = Regex.Match(t, @"^param\s+(\w+)\s*=\s*([^{]+?)\s*(\{.*\})?\s*;?$")).Success)
            {
                var o = Options(m.Groups[3].Value, ln);
                string val = m.Groups[2].Value.Trim();
                bool isBool = val is "true" or "false";
                parms.Add(new Param(m.Groups[1].Value, isBool ? val : Num(val, ln), isBool, o.GetValueOrDefault("min"), o.GetValueOrDefault("max"), o.GetValueOrDefault("description"), ln));
                continue;
            }

            if ((m = Regex.Match(t, @"^input\s+(\w+)\s*=\s*([\w.]+)\s*(\{.*\})?\s*;?$")).Success)
            {
                inputs.Add(new Input(m.Groups[1].Value, m.Groups[2].Value, Options(m.Groups[3].Value, ln), ln));
                continue;
            }

            if ((m = Regex.Match(t, @"^output\s+(\w+)\s*=\s*(\w+)\s*(\{.*\})?\s*;?$")).Success)
            {
                outputs.Add(new Output(m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant(), Options(m.Groups[3].Value, ln), ln));
                continue;
            }

            if ((m = Regex.Match(t, @"^level\s+(-?[\d.]+)\s*(\{.*\})?\s*;?$")).Success)
            {
                levels.Add((Num(m.Groups[1].Value, ln), Options(m.Groups[2].Value, ln).GetValueOrDefault("color"), ln));
                continue;
            }

            if ((m = Regex.Match(t, @"^(onBar|onStart)\s*\{\s*(.*)$")).Success)
            {
                block = m.Groups[1].Value == "onBar" ? onBar : onStart;
                depth = 1;
                var rest = m.Groups[2].Value.Trim();
                if (rest.Length > 0)
                {
                    if (rest.EndsWith('}'))
                    {
                        var inner = rest[..^1].Trim();
                        if (inner.Length > 0) block.Add((inner, ln));
                        block = null;
                        depth = 0;
                    }
                    else block.Add((rest, ln));
                }

                continue;
            }

            throw new KvsException(ln, $"No entiendo «{t}» (se esperaba name, param, input, output, level, onStart {{ }} u onBar {{ }})");
        }

        if (block != null) throw new KvsException(lines.Length, "Falta cerrar un bloque con «}»");
        if (outputs.Count == 0) throw new KvsException(1, "El script no tiene ninguna salida (output nombre = line { … })");

        // variables (var / let / const dentro de los bloques)
        var vars = new List<string>();
        foreach (var (text, _) in onStart.Concat(onBar))
        {
            var vm = Regex.Match(text, @"^(?:var|let|const)\s+(\w+)\s*=");
            if (vm.Success && !vars.Contains(vm.Groups[1].Value)) vars.Add(vm.Groups[1].Value);
        }

        var series = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in inputs) series.Add(x.Name);
        foreach (var x in outputs) series.Add(x.Name);
        foreach (var v in vars) series.Add(v);
        var paramNames = parms.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

        // las series de precio que se usan sin declararlas (close, high, delta…) entran solas
        foreach (var (text, ln) in onBar.Concat(onStart))
            foreach (Match tok in Regex.Matches(text, @"\b([A-Za-z_]\w*)\b"))
            {
                var name = tok.Groups[1].Value;
                if (!series.Contains(name) && !paramNames.ContainsKey(name) && Implicit.TryGetValue(name, out var src))
                {
                    inputs.Add(new Input(name, src, new Dictionary<string, string>(), ln));
                    series.Add(name);
                }
            }

        int sites = 0;
        string Expr(string e, int ln) => TranslateExpr(e, ln, series, paramNames, ref sites);

        string cls = ClassNameFor(fileNameWithoutExtension);
        // sin «overlay: true», las salidas van en un panel aparte (como en wrun, «lower» es lo normal)
        bool overlayAll = outputs.All(o => o.Options.GetValueOrDefault("overlay") == "true");
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated> traducido de " + Path.GetFileName(path) + " por KVP (lenguaje KVS) </auto-generated>");
        sb.AppendLine("using System;\nusing System.ComponentModel;\nusing System.ComponentModel.DataAnnotations;\nusing System.Drawing;\nusing TradingHub.Indicators;\nusing TradingHub.Indicators.Kvs;");
        sb.AppendLine("namespace TradingHub.UserScripts.Kvs");
        sb.AppendLine("{");
        sb.AppendLine($"[DisplayName({Str(display)})]");
        sb.AppendLine("[Category(\"Scripts KVS\")]");
        sb.AppendLine($"[Description({Str("Script KVS: " + Path.GetFileName(path))})]");
        sb.AppendLine($"public class {cls} : Indicator");
        sb.AppendLine("{");
        foreach (var name in series) sb.AppendLine($"    private readonly KvsSeries S_{name} = new KvsSeries();");
        var palette = new[] { "#3d8bfd", "#f2b53a", "#26c281", "#f0525a", "#b478ff", "#ffffff" };
        for (int k = 0; k < outputs.Count; k++)
        {
            var o = outputs[k];
            string vis = o.Kind switch
            {
                "histogram" or "histo" or "bar" or "bars" => "VisualMode.Histogram",
                "dots" or "points" or "dot" => "VisualMode.Dots",
                "line" or "area" or "step" => "VisualMode.Line",
                _ => throw new KvsException(o.Line, $"Tipo de salida «{o.Kind}»: line, histogram o dots"),
            };
            string color = o.Options.GetValueOrDefault("color") ?? palette[k % palette.Length];
            string width = o.Options.TryGetValue("width", out var w) ? Math.Clamp(int.TryParse(w, out var wi) ? wi : 1, 1, 10).ToString(CultureInfo.InvariantCulture) : "1";
            string label = o.Options.GetValueOrDefault("label") ?? o.Name;
            int alpha = o.Options.TryGetValue("opacity", out var op) && double.TryParse(op, NumberStyles.Float, CultureInfo.InvariantCulture, out var opv) ? (int)Math.Round(Math.Clamp(opv, 0, 1) * 255) : -1;
            string colorExpr = alpha >= 0 ? $"Color.FromArgb({alpha}, KvsRuntime.ParseColor({Str(color)}, Color.White))" : $"KvsRuntime.ParseColor({Str(color)}, Color.White)";
            sb.AppendLine($"    private readonly ValueDataSeries O_{o.Name} = new ValueDataSeries({Str(o.Name)}, {Str(label)}) {{ Color = {colorExpr}, Width = {width}, VisualType = {vis}, ShowZeroValue = false }};");
        }

        sb.AppendLine($"    private readonly KvsRuntime _f;");
        foreach (var p in parms)
        {
            sb.AppendLine($"#line {p.Line} {Str(path)}");
            sb.Append($"    [Display(Name = {Str(p.Name)}, GroupName = \"Parámetros\", Order = {p.Line}{(p.Description != null ? ", Description = " + Str(p.Description) : "")})]");
            if (!p.IsBool && (p.Min != null || p.Max != null))
                sb.Append($" [Range({(p.Min != null ? Num(p.Min, p.Line) : "double.MinValue")}, {(p.Max != null ? Num(p.Max, p.Line) : "double.MaxValue")})]");
            sb.AppendLine();
            if (p.IsBool) sb.AppendLine($"    public bool {p.Name} {{ get => _p_{p.Name}; set {{ _p_{p.Name} = value; RecalculateValues(); }} }}\n    private bool _p_{p.Name} = {p.Default};");
            else sb.AppendLine($"    public decimal {p.Name} {{ get => (decimal)_p_{p.Name}; set {{ _p_{p.Name} = (double)value; RecalculateValues(); }} }}\n    private double _p_{p.Name} = {p.Default};");
        }

        sb.AppendLine("#line default");
        sb.AppendLine($"    public {cls}() : base(true)");
        sb.AppendLine("    {");
        sb.AppendLine("        _f = new KvsRuntime(SITES);");
        if (!overlayAll) sb.AppendLine("        Panel = IndicatorDataProvider.NewPanel;");
        sb.AppendLine($"        DataSeries[0] = O_{outputs[0].Name};");
        foreach (var o in outputs.Skip(1)) sb.AppendLine($"        DataSeries.Add(O_{o.Name});");
        for (int k = 0; k < levels.Count; k++)
            sb.AppendLine($"        LineSeries.Add(new LineSeries(\"L{k}\", \"Nivel\") {{ Value = (decimal){levels[k].Value}, Color = KvsRuntime.ParseColor({Str(levels[k].Color ?? "#80808c")}, Color.Gray) }});");
        sb.AppendLine("    }");

        sb.AppendLine("    protected override void OnRecalculate()");
        sb.AppendLine("    {");
        sb.AppendLine("        _f.Reset();");
        foreach (var name in series) sb.AppendLine($"        S_{name}.Clear();");
        sb.AppendLine("        int bar = 0;");
        foreach (var (text, ln) in onStart) sb.AppendLine(Statement(text, ln, Expr, series, path));
        sb.AppendLine("#line default");
        sb.AppendLine("    }");

        sb.AppendLine("    protected override void OnCalculate(int bar, decimal value)");
        sb.AppendLine("    {");
        sb.AppendLine("        var c = GetCandle(bar);");
        sb.AppendLine("        bool lastBar = bar == CurrentBar - 1;");
        foreach (var x in inputs) sb.AppendLine(InputLine(x, path));
        sb.AppendLine("#line default");
        foreach (var (text, ln) in onBar) sb.AppendLine(Statement(text, ln, Expr, series, path));
        sb.AppendLine("#line default");
        foreach (var o in outputs)
        {
            sb.AppendLine($"        {{ double v = S_{o.Name}.Get(bar); O_{o.Name}[bar] = KvsRuntime.D(v);");
            if (o.Options.GetValueOrDefault("color_by") == "sign")
            {
                var cols = (o.Options.GetValueOrDefault("colors") ?? "").Trim('[', ']').Split(',').Select(x => x.Trim().Trim('"')).ToArray();
                string up = cols.Length > 0 && cols[0].Length > 0 ? cols[0] : "#26c281", dn = cols.Length > 1 ? cols[1] : "#f0525a";
                sb.AppendLine($"          if (!double.IsNaN(v)) O_{o.Name}.Colors[bar] = v >= 0 ? KvsRuntime.ParseColor({Str(up)}, Color.Green) : KvsRuntime.ParseColor({Str(dn)}, Color.Red);");
            }

            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("}");
        return sb.ToString().Replace("SITES", Math.Max(1, sites).ToString(CultureInfo.InvariantCulture));
    }

    private static string InputLine(Input x, string path)
    {
        string within = x.Options.TryGetValue("within", out var w) && int.TryParse(w, out var wi) ? wi.ToString(CultureInfo.InvariantCulture) : "20";
        string side = (x.Options.GetValueOrDefault("side") ?? "").ToUpperInvariant();
        string expr = x.Source.ToLowerInvariant() switch
        {
            "ohlcv.open" or "open" => "(double)c.Open",
            "ohlcv.high" or "high" => "(double)c.High",
            "ohlcv.low" or "low" => "(double)c.Low",
            "ohlcv.close" or "close" => "(double)c.Close",
            "ohlcv.volume" or "volume" => "(double)c.Volume",
            "ohlcv.hl2" => "(double)((c.High + c.Low) / 2)",
            "ohlcv.hlc3" => "(double)((c.High + c.Low + c.Close) / 3)",
            "ohlcv.ohlc4" => "(double)((c.Open + c.High + c.Low + c.Close) / 4)",
            "ohlcv.time" => "(c.Time - DateTime.UnixEpoch).TotalSeconds",
            "trades.volume" => side == "BUY" ? "(double)c.Ask" : side == "SELL" ? "(double)c.Bid" : "(double)c.Volume",
            "trades.buy" => "(double)c.Ask",
            "trades.sell" => "(double)c.Bid",
            "trades.delta" or "delta" => "(double)c.Delta",
            "trades.count" => "(double)c.Ticks",
            "book.bid" => $"KvsRuntime.Book(GetDepthSamples(bar), {within}).Bid",
            "book.ask" => $"KvsRuntime.Book(GetDepthSamples(bar), {within}).Ask",
            "book.delta" => $"(KvsRuntime.Book(GetDepthSamples(bar), {within}) is var bk ? bk.Bid - bk.Ask : double.NaN)",
            _ => throw new KvsException(x.Line, $"Entrada «{x.Source}»: ohlcv.open/high/low/close/volume/hl2/hlc3/ohlc4/time, trades.volume/buy/sell/delta/count o book.bid/ask/delta"),
        };
        return $"#line {x.Line} {Str(path)}\n        S_{x.Name}.Set(bar, {expr});";
    }

    private static string Statement(string text, int ln, Func<string, int, string> expr, HashSet<string> series, string path)
    {
        string head = $"#line {ln} {Str(path)}\n        ";
        var t = text.Trim().TrimEnd(';').Trim();
        Match m;
        if (t is "{" or "}" or "} else {" or "else {") return head + t;
        // if (…) { a; b } en una sola línea
        if ((m = Regex.Match(t, @"^(\}\s*)?(else\s+)?if\s*\((.*)\)\s*\{(.+)\}\s*$")).Success)
        {
            var inner = m.Groups[4].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => Statement(x, ln, expr, series, path).Split('\n')[^1].Trim());
            return head + $"{m.Groups[1].Value}{m.Groups[2].Value}if ({expr(m.Groups[3].Value, ln)}) {{ {string.Join(" ", inner)} }}";
        }

        if ((m = Regex.Match(t, @"^(\}\s*)?(else\s+)?if\s*\((.*)\)\s*\{\s*$")).Success)
            return head + $"{m.Groups[1].Value}{m.Groups[2].Value}if ({expr(m.Groups[3].Value, ln)}) {{";
        if ((m = Regex.Match(t, @"^(\}\s*)?(else\s+)?if\s*\((.*)\)\s*(.+)$")).Success && !m.Groups[4].Value.StartsWith('{'))
            return head + $"{m.Groups[1].Value}{m.Groups[2].Value}if ({expr(m.Groups[3].Value, ln)}) {{ {Statement(m.Groups[4].Value, ln, expr, series, path).Split('\n')[^1].Trim()} }}";
        if ((m = Regex.Match(t, @"^(?:var|let|const)\s+(\w+)\s*=\s*(.+)$")).Success)
            return head + $"S_{m.Groups[1].Value}.Set(bar, (double)({expr(m.Groups[2].Value, ln)}));";
        if ((m = Regex.Match(t, @"^out_(\w+)\s*\((.*)\)$")).Success)
        {
            if (!series.Contains(m.Groups[1].Value)) throw new KvsException(ln, $"No hay una salida «{m.Groups[1].Value}»");
            return head + $"S_{m.Groups[1].Value}.Set(bar, (double)({expr(m.Groups[2].Value, ln)}));";
        }

        if ((m = Regex.Match(t, @"^alert\s*\((.*)\)$")).Success)
        {
            string msg = m.Groups[1].Value.Trim();
            string arg = msg.StartsWith('"') ? msg : $"({expr(msg, ln)}).ToString()";
            return head + $"_f.Alert(this, bar, {arg}, lastBar);";
        }

        if ((m = Regex.Match(t, @"^(\w+)\s*([+\-*/])?=\s*(.+)$")).Success && !t.Contains("=="))
        {
            var name = m.Groups[1].Value;
            if (!series.Contains(name)) throw new KvsException(ln, $"«{name}» no está declarada (output {name} = line, o var {name} = …)");
            string rhs = expr(m.Groups[3].Value, ln);
            string op = m.Groups[2].Value;
            return head + (op.Length == 0 ? $"S_{name}.Set(bar, (double)({rhs}));" : $"S_{name}.Set(bar, S_{name}.Get(bar) {op} (double)({rhs}));");
        }

        throw new KvsException(ln, $"No entiendo la instrucción «{t}»");
    }

    /// <summary>Una expresión de KVS → C#: series con [n], parámetros, funciones con su sitio, and / or / not.</summary>
    private static string TranslateExpr(string e, int ln, HashSet<string> series, Dictionary<string, Param> parms, ref int sites)
    {
        var tokens = Regex.Matches(e, @"""[^""]*""|\d+\.?\d*(?:[eE][-+]?\d+)?|[A-Za-z_]\w*|&&|\|\||==|!=|<=|>=|[-+*/%()\[\],?:<>!]|\S")
            .Select(m => m.Value).ToList();
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            var tk = tokens[i];
            if (tk[0] == '"')
            {
                sb.Append(tk);
                continue;
            }

            if (char.IsDigit(tk[0]))
            {
                sb.Append(tk.Contains('.') || tk.Contains('e') || tk.Contains('E') ? tk : tk + ".0").Append('d');
                continue;
            }

            if (char.IsLetter(tk[0]) || tk[0] == '_')
            {
                switch (tk)
                {
                    case "and":
                        sb.Append(" && ");
                        continue;
                    case "or":
                        sb.Append(" || ");
                        continue;
                    case "not":
                        sb.Append('!');
                        continue;
                    case "true" or "false":
                        sb.Append(tk);
                        continue;
                    case "na" when !(i + 1 < tokens.Count && tokens[i + 1] == "("):
                        sb.Append("double.NaN");
                        continue;
                    case "bar_index":
                        sb.Append("(double)bar");
                        continue;
                }

                bool call = i + 1 < tokens.Count && tokens[i + 1] == "(";
                if (call)
                {
                    var fn = tk.ToLowerInvariant();
                    // los argumentos hasta el paréntesis que cierra
                    int depth = 0, j = i + 1;
                    var args = new List<string>();
                    var cur = new List<string>();
                    for (; j < tokens.Count; j++)
                    {
                        var x = tokens[j];
                        if (x == "(")
                        {
                            depth++;
                            if (depth == 1) continue;
                        }
                        else if (x == ")")
                        {
                            depth--;
                            if (depth == 0)
                            {
                                if (cur.Count > 0) args.Add(string.Join(" ", cur));
                                break;
                            }
                        }
                        else if (x == "," && depth == 1)
                        {
                            args.Add(string.Join(" ", cur));
                            cur.Clear();
                            continue;
                        }

                        cur.Add(x);
                    }

                    if (j >= tokens.Count) throw new KvsException(ln, $"Falta cerrar el paréntesis de {tk}(");
                    var tr = new List<string>();
                    foreach (var a in args) tr.Add(TranslateExpr(a, ln, series, parms, ref sites));
                    if (Windowed.Contains(fn))
                    {
                        if (tr.Count < (fn == "change" ? 1 : 2)) throw new KvsException(ln, $"{fn}(valor, largo)");
                        sb.Append($"_f.{char.ToUpperInvariant(fn[0]) + fn[1..]}({sites++}, bar, (double)({tr[0]}), {(tr.Count > 1 ? $"(double)({tr[1]})" : "1d")})");
                    }
                    else if (Stateful.Contains(fn))
                    {
                        if (fn == "cum") sb.Append($"_f.Cum({sites++}, bar, (double)({tr[0]}))");
                        else
                        {
                            if (tr.Count < 2) throw new KvsException(ln, $"{fn}(a, b)");
                            sb.Append($"_f.{(fn == "crossover" ? "Crossover" : "Crossunder")}({sites++}, bar, (double)({tr[0]}), (double)({tr[1]}))");
                        }
                    }
                    else if (MathFns.TryGetValue(fn, out var mf))
                    {
                        sb.Append(mf).Append('(').Append(string.Join(", ", tr.Select(a => $"(double)({a})"))).Append(')');
                    }
                    else throw new KvsException(ln, $"No conozco la función «{tk}» (ema, sma, rma, wma, stdev, highest, lowest, sum, change, cum, crossover, crossunder, abs, min, max, sqrt, log, pow, round, nz…)");

                    i = j;
                    continue;
                }

                if (series.Contains(tk))
                {
                    if (i + 1 < tokens.Count && tokens[i + 1] == "[")
                    {
                        int depth = 0, j = i + 1;
                        var inner = new List<string>();
                        for (; j < tokens.Count; j++)
                        {
                            if (tokens[j] == "[")
                            {
                                depth++;
                                if (depth == 1) continue;
                            }
                            else if (tokens[j] == "]")
                            {
                                depth--;
                                if (depth == 0) break;
                            }

                            inner.Add(tokens[j]);
                        }

                        sb.Append($"S_{tk}.Get(bar, (int)({TranslateExpr(string.Join(" ", inner), ln, series, parms, ref sites)}))");
                        i = j;
                    }
                    else sb.Append($"S_{tk}.Get(bar)");
                    continue;
                }

                if (parms.TryGetValue(tk, out var p))
                {
                    sb.Append("_p_").Append(p.Name);
                    continue;
                }

                if (tk is "PI") { sb.Append("Math.PI"); continue; }
                throw new KvsException(ln, $"«{tk}» no está declarado (param, input, output o var)");
            }

            sb.Append(tk switch { "?" => " ? ", ":" => " : ", "&&" => " && ", "||" => " || ", _ => tk });
        }

        return sb.ToString();
    }
}
