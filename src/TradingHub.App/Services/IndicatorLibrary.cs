using TradingHub.Indicators;

namespace TradingHub.App.Services;

/// <summary>Cómo armó el usuario la biblioteca de indicadores: sus grupos, su orden y los nombres que les puso.</summary>
public sealed class LibraryLayout
{
    /// <summary>Los grupos en el orden elegido (los que no están van después, en su orden de fábrica).</summary>
    public List<LibraryGroupState> Groups { get; set; } = new();

    /// <summary>Nombres puestos a los indicadores: tipo completo → nombre.</summary>
    public Dictionary<string, string> Names { get; set; } = new();
}

public sealed class LibraryGroupState
{
    /// <summary>La categoría de fábrica («Perfil de volumen»…), o «user:…» para un grupo creado por el usuario.</summary>
    public string Id { get; set; } = "";

    /// <summary>El nombre que le puso el usuario (null = el de fábrica, traducido).</summary>
    public string? Name { get; set; }

    /// <summary>Los indicadores puestos en este grupo, en su orden (tipo completo). Los demás del grupo van después.</summary>
    public List<string> Items { get; set; } = new();

    public bool Collapsed { get; set; }
}

/// <summary>Un grupo de la biblioteca ya armado para mostrar.</summary>
public sealed record LibraryGroup(string Id, string Name, bool IsUser, bool Collapsed, List<IndicatorInfo> Items);

/// <summary>
/// La biblioteca de indicadores en grupos: los de fábrica por categoría (01 Medias móviles … 15 Macro, VP definitivo,
/// los propios arriba), con el orden, los grupos nuevos y los nombres que haya puesto el usuario.
/// </summary>
public static class IndicatorLibrary
{
    public const string OwnGroup = "own";
    public const string UserPrefix = "user:";

    /// <summary>El grupo de fábrica de un indicador.</summary>
    public static string DefaultGroupOf(IndicatorInfo i) =>
        i.Source == UserScripts.Source && IndicatorCatalog.ReplacedBy(i.Type) == null ? OwnGroup : IndicatorCategories.Normalize(i.Category);

    /// <summary>Lugar de fábrica de un grupo (los propios primero, después 01 … 15, VP definitivo y el resto por nombre).</summary>
    private static (int, string) DefaultRank(string id)
    {
        if (id == OwnGroup) return (-1, "");
        int k = Array.IndexOf(IndicatorCategories.Order, id);
        if (k >= 0) return (k, "");
        if (id == "VP definitivo") return (IndicatorCategories.Order.Length, "");
        return (IndicatorCategories.Order.Length + 1, id);
    }

    /// <summary>El nombre de fábrica de un grupo (en español: se traduce al mostrarlo).</summary>
    public static string DefaultGroupName(string id) => id == OwnGroup ? "★ Mis indicadores" : id;

    /// <summary>Arma los grupos con los indicadores <paramref name="items"/> según <paramref name="layout"/>.</summary>
    public static List<LibraryGroup> Build(IReadOnlyList<IndicatorInfo> items, LibraryLayout layout)
    {
        var byType = new Dictionary<string, IndicatorInfo>(StringComparer.Ordinal);
        foreach (var i in items) byType.TryAdd(i.Type.FullName!, i);

        // dónde puso el usuario cada uno
        var placed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in layout.Groups)
            foreach (var t in g.Items)
                if (byType.ContainsKey(t)) placed.TryAdd(t, g.Id);

        var groups = new Dictionary<string, List<IndicatorInfo>>(StringComparer.Ordinal);
        foreach (var g in layout.Groups)
        {
            var list = groups.TryGetValue(g.Id, out var l) ? l : groups[g.Id] = new List<IndicatorInfo>();
            foreach (var t in g.Items)
                if (byType.TryGetValue(t, out var info) && placed[t] == g.Id && !list.Contains(info)) list.Add(info);
        }

        // los demás en su grupo de fábrica, después de los ordenados a mano, por nombre
        foreach (var info in items.Where(i => !placed.ContainsKey(i.Type.FullName!)).OrderBy(i => Indicator.DefaultName(i.Type), StringComparer.CurrentCultureIgnoreCase))
        {
            string id = DefaultGroupOf(info);
            (groups.TryGetValue(id, out var l) ? l : groups[id] = new List<IndicatorInfo>()).Add(info);
        }

        // el orden de los grupos: el del usuario, y los que no ordenó en su lugar de fábrica
        var order = new List<string>();
        foreach (var g in layout.Groups)
            if (!order.Contains(g.Id)) order.Add(g.Id);
        foreach (var id in groups.Keys.Where(k => !order.Contains(k)).OrderBy(DefaultRank))
        {
            // en su lugar de fábrica: antes del primer grupo de fábrica que va después
            var rank = DefaultRank(id);
            int at = order.FindIndex(o => !o.StartsWith(UserPrefix, StringComparison.Ordinal) && DefaultRank(o).CompareTo(rank) > 0);
            if (at < 0) order.Add(id);
            else order.Insert(at, id);
        }

        var res = new List<LibraryGroup>();
        foreach (var id in order)
        {
            var state = layout.Groups.FirstOrDefault(g => g.Id == id);
            bool user = id.StartsWith(UserPrefix, StringComparison.Ordinal);
            var list = groups.TryGetValue(id, out var l) ? l : new List<IndicatorInfo>();
            if (list.Count == 0 && !user) continue; // un grupo de fábrica vacío no se muestra
            res.Add(new LibraryGroup(id, state?.Name is { Length: > 0 } n ? n : DefaultGroupName(id), user, state?.Collapsed ?? false, list));
        }

        return res;
    }

    /// <summary>Copia el orden que se ve a los ajustes (así mover algo no desordena lo demás).</summary>
    private static void Materialize(LibraryLayout layout, IReadOnlyList<LibraryGroup> shown)
    {
        var old = layout.Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        layout.Groups = shown.Select(g => new LibraryGroupState
        {
            Id = g.Id,
            Name = old.TryGetValue(g.Id, out var o) ? o.Name : null,
            Collapsed = g.Collapsed,
            Items = g.Items.Select(i => i.Type.FullName!).ToList(),
        }).ToList();
        // los grupos del usuario vacíos que no se ven igual siguen
        foreach (var o in old.Values.Where(o => o.Id.StartsWith(UserPrefix, StringComparison.Ordinal) && layout.Groups.All(g => g.Id != o.Id)))
            layout.Groups.Add(o);
    }

    private static LibraryGroupState Group(LibraryLayout layout, string id)
    {
        var g = layout.Groups.FirstOrDefault(x => x.Id == id);
        if (g != null) return g;
        g = new LibraryGroupState { Id = id };
        layout.Groups.Add(g);
        return g;
    }

    /// <summary>Mueve un indicador al grupo <paramref name="toGroup"/>, antes de <paramref name="beforeType"/> (null = al final).</summary>
    public static void Move(LibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string type, string toGroup, string? beforeType)
    {
        Materialize(layout, Build(items, layout));
        foreach (var g in layout.Groups) g.Items.Remove(type);
        var target = Group(layout, toGroup);
        int at = beforeType == null ? -1 : target.Items.IndexOf(beforeType);
        if (at < 0) target.Items.Add(type);
        else target.Items.Insert(at, type);
    }

    /// <summary>Sube (−1) o baja (+1) un indicador dentro de su grupo.</summary>
    public static void Shift(LibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string type, int delta)
    {
        Materialize(layout, Build(items, layout));
        var g = layout.Groups.FirstOrDefault(x => x.Items.Contains(type));
        if (g == null) return;
        int i = g.Items.IndexOf(type), j = Math.Clamp(i + delta, 0, g.Items.Count - 1);
        if (i == j) return;
        g.Items.RemoveAt(i);
        g.Items.Insert(j, type);
    }

    /// <summary>Sube (−1) o baja (+1) un grupo.</summary>
    public static void ShiftGroup(LibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string id, int delta)
    {
        Materialize(layout, Build(items, layout));
        int i = layout.Groups.FindIndex(g => g.Id == id), j = Math.Clamp(i + delta, 0, layout.Groups.Count - 1);
        if (i < 0 || i == j) return;
        var g = layout.Groups[i];
        layout.Groups.RemoveAt(i);
        layout.Groups.Insert(j, g);
    }

    /// <summary>Un grupo nuevo del usuario (vacío) después de <paramref name="afterId"/> (null = al final). Devuelve su Id.</summary>
    public static string CreateGroup(LibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string name, string? afterId)
    {
        Materialize(layout, Build(items, layout));
        var g = new LibraryGroupState { Id = UserPrefix + Guid.NewGuid().ToString("N")[..8], Name = name.Trim() };
        int at = afterId == null ? -1 : layout.Groups.FindIndex(x => x.Id == afterId);
        if (at < 0) layout.Groups.Add(g);
        else layout.Groups.Insert(at + 1, g);
        return g.Id;
    }

    public static void RenameGroup(LibraryLayout layout, string id, string? name) => Group(layout, id).Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>Borra un grupo del usuario: sus indicadores vuelven a su grupo de fábrica.</summary>
    public static void DeleteGroup(LibraryLayout layout, string id)
    {
        if (!id.StartsWith(UserPrefix, StringComparison.Ordinal)) return;
        layout.Groups.RemoveAll(g => g.Id == id);
    }

    public static void SetCollapsed(LibraryLayout layout, string id, bool collapsed) => Group(layout, id).Collapsed = collapsed;

    /// <summary>Ordena un grupo por nombre (A→Z).</summary>
    public static void SortGroup(LibraryLayout layout, IReadOnlyList<IndicatorInfo> items, string id)
    {
        Materialize(layout, Build(items, layout));
        var g = Group(layout, id);
        var byType = items.GroupBy(i => i.Type.FullName!).ToDictionary(x => x.Key, x => x.First());
        g.Items = g.Items.OrderBy(t => byType.TryGetValue(t, out var i) ? Indicator.DefaultName(i.Type) : t, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Pone (o quita, con null o vacío) el nombre de un indicador.</summary>
    public static void Rename(LibraryLayout layout, string type, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) layout.Names.Remove(type);
        else layout.Names[type] = name.Trim();
        Indicator.SetRenamed(layout.Names);
    }

    /// <summary>Vuelve todo a como viene de fábrica (grupos, orden y nombres).</summary>
    public static void Reset(LibraryLayout layout)
    {
        layout.Groups.Clear();
        layout.Names.Clear();
        Indicator.SetRenamed(layout.Names);
    }
}
