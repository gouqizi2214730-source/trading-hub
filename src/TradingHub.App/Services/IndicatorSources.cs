using System.Reflection;
using System.Text.RegularExpressions;
using TradingHub.Indicators;

namespace TradingHub.App.Services;

/// <summary>
/// Código fuente de los indicadores: los de la biblioteca viajan embebidos en su ensamblado (se pueden ver y copiar como
/// propios para modificarlos); los propios son archivos .cs en la carpeta del usuario (se editan y borran).
/// </summary>
public static class IndicatorSources
{
    /// <summary>Archivo .cs del indicador propio, o null si no es propio.</summary>
    public static string? UserFile(Type t)
    {
        if (!Directory.Exists(AppPaths.UserIndicators)) return null;
        var rx = new Regex(@"\bclass\s+" + Regex.Escape(t.Name) + @"\b");
        foreach (var f in Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories))
        {
            try
            {
                if (rx.IsMatch(File.ReadAllText(f))) return f;
            }
            catch
            {
                // se sigue con el próximo
            }
        }

        return null;
    }

    /// <summary>Fuente embebida de un indicador de la biblioteca (null si su ensamblado no la trae).</summary>
    public static string? EmbeddedSource(Type t)
    {
        var asm = t.Assembly;
        var rx = new Regex(@"\bclass\s+" + Regex.Escape(t.Name) + @"\b");
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("source/", StringComparison.Ordinal) || !name.EndsWith(".cs", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) continue;
            using var r = new StreamReader(s);
            var text = r.ReadToEnd();
            if (rx.IsMatch(text)) return text;
        }

        return null;
    }

    /// <summary>Fuente de cualquier indicador (propio o de la biblioteca).</summary>
    public static string? Source(Type t)
    {
        var f = UserFile(t);
        if (f != null)
        {
            try
            {
                return File.ReadAllText(f);
            }
            catch
            {
                return null;
            }
        }

        return EmbeddedSource(t);
    }

    /// <summary>
    /// Copia un indicador de la biblioteca a la carpeta propia con otro nombre de clase (y nombre visible «… (copia)»), para
    /// modificarlo sin tocar el original. Devuelve la ruta del archivo nuevo.
    /// </summary>
    public static string Fork(Type t, string? newClassName = null, string? folder = null)
    {
        folder ??= AppPaths.UserIndicators;
        var src = EmbeddedSource(t) ?? throw new InvalidOperationException("Este indicador no trae su código fuente.");
        string cls = newClassName ?? t.Name + "Propio";
        cls = new string(cls.Where(char.IsLetterOrDigit).ToArray());
        if (cls.Length == 0 || char.IsDigit(cls[0])) cls = "Indicador" + cls;
        string text = src;
        // el archivo puede traer varias clases (ayudas internas): la del indicador cambia de nombre; el resto queda igual
        text = Regex.Replace(text, @"\bclass\s+" + Regex.Escape(t.Name) + @"\b", "class " + cls);
        text = Regex.Replace(text, @"\b" + Regex.Escape(t.Name) + @"\s*\(\)", cls + "()"); // constructor
        text = Regex.Replace(text, @"\[DisplayName\(""([^""]*)""\)\]", m => $"[DisplayName(\"{m.Groups[1].Value} (copia)\")]", RegexOptions.None, TimeSpan.FromSeconds(1));
        // la copia vive en un namespace hijo («.Copia»): sigue viendo los tipos de la biblioteca (TradingHub.Indicators, los
        // ayudantes de su carpeta) y las clases auxiliares que vengan en el mismo archivo no chocan con las originales
        text = Regex.Replace(text, @"^(\s*namespace\s+[\w.]+)\s*;", "$1.Copia;", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^(\s*namespace\s+[\w.]+)\s*\{", "$1.Copia {", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\binternal\s+(static\s+|sealed\s+|abstract\s+)?class\b", m => m.Value.Replace("internal", "public"));
        // los using implícitos del proyecto de la biblioteca
        text = "using System.IO;\nusing System.Threading;\nusing System.Threading.Tasks;\n" + text;
        text = "// Copia de " + Indicator.DefaultName(t) + " (KVP). Modificala a gusto: se recompila sola al guardar.\n" + text;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, cls + ".cs");
        int n = 2;
        while (File.Exists(path)) path = Path.Combine(folder, cls + n++ + ".cs");
        File.WriteAllText(path, text);
        return path;
    }
}
