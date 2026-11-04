using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

internal enum CodeKind
{
    Indicator,
}

/// <summary>
/// Editor de indicadores propios (C#, API compatible con ATAS). Al guardar se recompilan solos y
/// aparecen en la biblioteca de indicadores de cada gráfico.
/// </summary>
internal sealed class CodeEditorWindow : Window
{
    private readonly AppServices _svc;
    private readonly ListBox _files = new() { Background = Brushes.Transparent };
    private readonly CodeEditor _editor = new();
    private readonly TextBlock _title = new() { Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Avalonia.Controls.Shapes.Ellipse _dot = new() { Width = 8, Height = 8 };
    private readonly TextBlock _state = new() { FontSize = 12 };
    private string? _path;
    private readonly (string Name, string Source)? _readOnly;

    /// <param name="open">Archivo propio a abrir (null = el primero).</param>
    /// <param name="readOnly">Solo ver: nombre y código de un indicador de la biblioteca.</param>
    public CodeEditorWindow(AppServices svc, CodeKind kind, string? open = null, (string Name, string Source)? readOnly = null)
    {
        _svc = svc;
        _readOnly = readOnly;
        Title = "Indicadores propios — editor";
        Width = 1180;
        Height = 780;
        MinWidth = 820;
        MinHeight = 520;
        Background = Ui.Bg0;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = AppIcon.Window;
        Content = Build();
        _editor.SaveRequested += SaveAndCompile;
        _editor.CompileRequested += SaveAndCompile;
        _editor.Changed += () => _title.Text = Path.GetFileName(_path) + " •";
        if (_svc.Scripts != null) _svc.Scripts.Compiled += OnCompiled;
        Closed += (_, _) =>
        {
            if (_svc.Scripts != null) _svc.Scripts.Compiled -= OnCompiled;
            if (_editor.IsDirty) Save();
        };
        if (readOnly is { } ro)
        {
            Title = ro.Name + " — código fuente (solo lectura)";
            _title.Text = ro.Name;
            _editor.Text = ro.Source;
            _editor.IsReadOnly = true;
            _editor.IsDirty = false;
            _state.Text = "Solo lectura: en Indicadores, «Copiar como indicador propio» para modificarlo";
            _dot.Fill = Ui.Text2;
        }
        else
        {
            FillFiles(open);
            if (_svc.Scripts?.LastResult is { } last) OnCompiled(last);
        }
    }

    private Control Build()
    {
        var add = Ui.Button("Nuevo", MaterialIconKind.Plus, () => _ = NewAsync());
        var addKvs = Ui.IconButton(MaterialIconKind.ScriptTextOutline, "Nuevo script KVS (el lenguaje simple: param / input / output / onBar, como wrun de openmarket)", () => _ = NewKvsAsync(), 17);
        var folder = Ui.IconButton(MaterialIconKind.FolderOpenOutline, Os.IsWindows ? "Abrir la carpeta en el Explorador de archivos" : "Abrir la carpeta en Finder", () => Shell.OpenFolder(AppPaths.UserIndicators));
        var pine = Ui.IconButton(MaterialIconKind.Import, "Importar un Pine Script de TradingView (Claude lo traduce a un indicador de KVP)", () => _ = ImportPineAsync(), 17);
        var rename = Ui.IconButton(MaterialIconKind.RenameBox, "Cambiar el nombre del archivo", () => _ = RenameAsync(), 17);
        var del = Ui.IconButton(MaterialIconKind.DeleteOutline, "Eliminar este archivo", () => _ = DeleteAsync(), 17);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var acts = Ui.H(2, pine, rename, del, folder, addKvs, add);
        DockPanel.SetDock(acts, Dock.Right);
        head.Children.Add(acts);
        head.Children.Add(Ui.H3("Archivos"));
        _files.SelectionChanged += (_, _) =>
        {
            if (_files.SelectedItem is ListBoxItem { Tag: string p }) Open(p);
        };
        var left = new DockPanel { Width = 250, Margin = new Thickness(14) };
        DockPanel.SetDock(head, Dock.Top);
        left.Children.Add(head);
        var note = new TextBlock
        {
            Text = "API de indicadores de Trading Hub (Indicator, OnCalculate, DataSeries, OnRender…); si pegás código escrito con esta API en otra plataforma, se adapta solo. Al guardar se recompila y queda disponible en Indicadores.",
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        DockPanel.SetDock(note, Dock.Bottom);
        left.Children.Add(note);
        left.Children.Add(Ui.Card(_files, 6));

        var save = Ui.Button("Guardar y compilar", MaterialIconKind.Hammer, SaveAndCompile, "primary");
        ToolTip.SetTip(save, Keys.Label("S"));
        var bar = new DockPanel { Margin = new Thickness(16, 10) };
        var right = Ui.H(10, new Border { Child = Ui.H(7, _dot, _state), Classes = { "pill" } }, save);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(_title);
        var top = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Background = Ui.Bg1 };
        var main = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        main.Children.Add(top);
        main.Children.Add(_editor);

        var root = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(new Border { Child = left, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) }.Also(b => DockPanel.SetDock(b, Dock.Left)));
        root.Children.Add(main);
        return root;
    }

    private void FillFiles(string? select = null)
    {
        _files.Items.Clear();
        foreach (var f in Directory.EnumerateFiles(AppPaths.UserIndicators, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".kvs", StringComparison.OrdinalIgnoreCase))
                     .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).OrderBy(f => f))
        {
            bool kvs = f.EndsWith(".kvs", StringComparison.OrdinalIgnoreCase);
            var row = Ui.H(8, Ui.Icon(kvs ? MaterialIconKind.ScriptTextOutline : MaterialIconKind.LanguageCsharp, 16, Ui.Accent), new TextBlock { Text = Path.GetRelativePath(AppPaths.UserIndicators, f), TextTrimming = TextTrimming.CharacterEllipsis });
            _files.Items.Add(new ListBoxItem { Content = row, Tag = f });
        }

        _files.SelectedItem = _files.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag! == (select ?? _path)) ?? _files.Items.OfType<ListBoxItem>().FirstOrDefault();
    }

    private void Open(string path)
    {
        if (_path == path) return;
        if (_editor.IsDirty) Save();
        _path = path;
        _editor.Text = File.ReadAllText(path);
        _title.Text = Path.GetFileName(path);
        if (_svc.Scripts?.LastResult is { } last) _editor.ShowDiagnostics(last.Diagnostics, path);
    }

    private void Save()
    {
        if (_path == null) return;
        File.WriteAllText(_path, _editor.Text);
        _editor.IsDirty = false;
        _title.Text = Path.GetFileName(_path);
    }

    private void SaveAndCompile()
    {
        Save();
        _dot.Fill = Ui.Warn;
        _state.Text = "Compilando…";
        _svc.Scripts?.CompileAll();
    }

    private void OnCompiled(TradingHub.Scripting.ScriptCompileResult r)
    {
        int errors = r.Diagnostics.Count(d => d.Severity == "error");
        _dot.Fill = r.Success ? Ui.Up : Ui.Down;
        _state.Text = r.Success ? $"Compilado · {r.Elapsed.TotalMilliseconds:0} ms" : $"{errors} error(es)";
        if (_path != null) _editor.ShowDiagnostics(r.Diagnostics, _path);
    }

    private async Task DeleteAsync()
    {
        if (_path == null || _readOnly != null) return;
        bool ok = await ConfirmDialog.ShowAsync(this, "Eliminar indicador", $"Se borra {Path.GetFileName(_path)}. Los gráficos que lo usan lo pierden.", "Eliminar", danger: true);
        if (!ok) return;
        try
        {
            File.Delete(_path);
            _editor.IsDirty = false;
            _path = null;
            _editor.Text = "";
            _title.Text = "";
            FillFiles();
            _svc.Scripts?.CompileAll();
        }
        catch (Exception ex)
        {
            await ConfirmDialog.ShowAsync(this, "No se pudo borrar", ex.Message, "Cerrar");
        }
    }

    private async Task RenameAsync()
    {
        if (_path == null || _readOnly != null) return;
        var name = await PromptDialog.ShowAsync(this, "Cambiar nombre", "Nombre del archivo", Path.GetFileNameWithoutExtension(_path), "Renombrar");
        if (string.IsNullOrWhiteSpace(name)) return;
        var target = Path.Combine(Path.GetDirectoryName(_path)!, name.Trim() + Path.GetExtension(_path));
        if (File.Exists(target)) return;
        if (_editor.IsDirty) Save();
        File.Move(_path, target);
        _path = null;
        FillFiles(target);
        _svc.Scripts?.CompileAll();
    }

    /// <summary>Guarda el Pine Script en la carpeta propia y le pide a Claude (Backtesting → chat) que lo traduzca a C#.</summary>
    private async Task ImportPineAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Pine Script de TradingView",
            AllowMultiple = false,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Pine Script") { Patterns = new[] { "*.pine", "*.txt", "*.pinescript" } } },
        });
        if (files.Count == 0) return;
        string text;
        await using (var stream = await files[0].OpenReadAsync())
        using (var reader = new StreamReader(stream))
            text = await reader.ReadToEndAsync();
        var pineDir = Path.Combine(AppPaths.UserIndicators, "Pine");
        Directory.CreateDirectory(pineDir);
        string baseName = new string(Path.GetFileNameWithoutExtension(files[0].Name).Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (baseName.Length == 0) baseName = "script";
        var pinePath = Path.Combine(pineDir, baseName + ".pine");
        File.WriteAllText(pinePath, text);
        if (TopLevel.GetTopLevel(this) is not MainWindow && Owner is not MainWindow)
        {
            // sin ventana principal a mano: queda guardado para pedirlo desde el chat
            await ConfirmDialog.ShowAsync(this, "Pine Script guardado", $"Quedó en {pinePath}. En Backtesting → chat con Claude pedile que lo traduzca a un indicador.", "Cerrar");
            return;
        }

        var main = (Owner as MainWindow) ?? (MainWindow)TopLevel.GetTopLevel(this)!;
        main.Navigate("strategies");
        if (main.Page("strategies") is Views.StrategiesView sv)
            sv.AskClaudeToPortPine(pinePath);
    }

    private async Task NewAsync()
    {
        var name = await PromptDialog.ShowAsync(this, "Nuevo indicador", "Nombre de la clase", "MiIndicador", "Crear");
        if (name == null) return;
        string cls = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (cls.Length == 0 || char.IsDigit(cls[0])) cls = "Indicador" + cls;
        var path = Path.Combine(AppPaths.UserIndicators, cls + ".cs");
        if (!File.Exists(path)) File.WriteAllText(path, IndicatorTemplate(cls, name));
        FillFiles(path);
    }

    private async Task NewKvsAsync()
    {
        var name = await PromptDialog.ShowAsync(this, "Nuevo script KVS", "Nombre del script", "MiScript", "Crear");
        if (name == null) return;
        string file = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        if (file.Length == 0) file = "MiScript";
        var path = Path.Combine(AppPaths.UserIndicators, file + ".kvs");
        if (!File.Exists(path)) File.WriteAllText(path, KvsTemplate(name.Trim()));
        FillFiles(path);
        _svc.Scripts?.CompileAll();
    }

    /// <summary>Un script KVS de ejemplo: bandas de una media con alerta, y el delta del libro / de las operaciones.</summary>
    private static string KvsTemplate(string display) => $$"""
        // Script KVS: el lenguaje simple de KVP (parecido a wrun de openmarket). Al guardar se compila y aparece en
        // Indicadores → «Scripts KVS». Funciones: ema sma rma wma stdev highest lowest sum change cum crossover crossunder
        // abs min max sqrt log pow round nz. Entradas: ohlcv.open/high/low/close/volume/hl2/hlc3/ohlc4/time,
        // trades.volume {side: "BUY"/"SELL"}, trades.delta, trades.count, book.bid / book.ask / book.delta {within: ticks}.
        // Una serie con [n] es su valor de hace n velas: close[1] es el cierre anterior.
        name "{{display}}"

        param length = 20 { min: 1, max: 500, description: "Velas de la media" }
        param mult = 2 { min: 0.1, max: 10, description: "Ancho de las bandas (× desvío)" }

        input src = ohlcv.close
        input buys = trades.volume { side: "BUY" }

        output basis = line { overlay: true, color: "#f2b53a", width: 2 }
        output upper = line { overlay: true, color: "#3d8bfd" }
        output lower = line { overlay: true, color: "#3d8bfd" }

        onBar {
          basis = ema(src, length)
          var dev = stdev(src, length)
          upper = basis + mult * dev
          lower = basis - mult * dev
          if (crossover(src, upper)) {
            alert("cerró arriba de la banda")
          }
        }
        """;

    private static string IndicatorTemplate(string cls, string display) => $$"""
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations;
        using TradingHub.Indicators;
        using TradingHub.Indicators.Drawing;

        [DisplayName("{{display}}")]
        [Category("Mis indicadores")]
        public class {{cls}} : Indicator
        {
            private readonly ValueDataSeries _serie = new("Serie", "Valor") { Color = System.Drawing.Color.Orange, Width = 2 };

            [Display(Name = "Período", GroupName = "Configuración")]
            [Range(1, 1000)]
            public int Periodo { get; set; } = 20;

            public {{cls}}()
            {
                DataSeries[0] = _serie;
            }

            protected override void OnCalculate(int bar, decimal value)
            {
                // media simple del cierre como ejemplo
                if (bar < Periodo) return;
                decimal suma = 0;
                for (int i = 0; i < Periodo; i++) suma += GetCandle(bar - i).Close;
                _serie[bar] = suma / Periodo;
            }
        }
        """;
}
