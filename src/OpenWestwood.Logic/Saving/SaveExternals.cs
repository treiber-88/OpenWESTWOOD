using System.Runtime.CompilerServices;
using OpenWestwood.Core.Rules;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic.Combat;
using OpenWestwood.Logic.Locomotion;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Logic.Saving;

/// <summary>
/// What a saved game does not store but points at: the rules and everything made from them (object templates and their
/// modules, weapons, armor, locomotors, special powers, command buttons), the models and the map's terrain. A saved
/// reference to one of these is a key ("T:AmericaTankCrusader", "R:12345"...) that the loading game turns back into its
/// own copy, so a loaded game shares them exactly as the saved one did.
/// </summary>
public sealed class SaveExternals
{
    readonly GameLogic game;
    readonly MapFile? map;
    readonly Dictionary<object, string> singletons = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, object> singletonsByKey = new();
    Dictionary<RuleNode, string>? templateNodes;

    /// <summary>The rule tree's nodes in a fixed order, made once per rule set.</summary>
    sealed class RuleIndex
    {
        public readonly List<RuleNode> Order = new();
        public readonly Dictionary<RuleNode, int> Index = new(ReferenceEqualityComparer.Instance);
    }

    static readonly ConditionalWeakTable<RuleNode, RuleIndex> indices = new();

    static RuleIndex IndexOf(RuleNode root) => indices.GetValue(root, r =>
    {
        var index = new RuleIndex();
        Walk(r, index);
        return index;
    });

    /// <summary>Depth first, entries then items: the same order in every run for the same rules.</summary>
    static void Walk(RuleNode root, RuleIndex index)
    {
        var stack = new Stack<RuleNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!index.Index.TryAdd(node, index.Order.Count))
                continue;
            index.Order.Add(node);
            for (int i = node.Items.Count - 1; i >= 0; i--)
                stack.Push(node.Items[i]);
            for (int i = node.Entries.Count - 1; i >= 0; i--)
                stack.Push(node.Entries[i].Value);
        }
    }

    public SaveExternals(GameLogic game, MapFile? map)
    {
        this.game = game;
        this.map = map;
        Add("Rules", game.Rules);
        Add("Templates", game.Templates);
        Add("Catalog", game.Catalog);
        Add("GameData", game.Catalog.GameData);
        Add("Armor.None", ArmorTemplate.None);
        Add("Terrain.Heights", game.Terrain.Heights);
        Add("Terrain.Tiles", game.Terrain.Tiles);
        if (game.Bones.Library is { } models)
            Add("Models", models);
        if (map != null)
            for (int i = 0; i < map.PolygonTriggers.Count; i++)
                Add($"Polygon#{i}", map.PolygonTriggers[i]);
    }

    void Add(string key, object? value)
    {
        if (value == null)
            return;
        singletons.TryAdd(value, key);
        singletonsByKey[key] = value;
    }

    /// <summary>The key for something the save points at instead of storing, or null if it is part of the game.</summary>
    public string? KeyOf(object value)
    {
        if (singletons.TryGetValue(value, out var key))
            return key;
        switch (value)
        {
            case ObjectTemplate t:
                return "T:" + t.Name;
            case ModuleDefinition d:
                foreach (var t in game.Templates.Loaded)
                    for (int i = 0; i < t.Modules.Count; i++)
                        if (ReferenceEquals(t.Modules[i], d))
                            return $"M:{t.Name}#{i}";
                return null;
            case WeaponTemplate w when ReferenceEquals(game.Catalog.Weapon(w.Name), w):
                return "W:" + w.Name;
            case ArmorTemplate a when ReferenceEquals(game.Catalog.Armor(a.Name), a):
                return "A:" + a.Name;
            case LocomotorTemplate l when ReferenceEquals(game.Catalog.Locomotor(l.Name), l):
                return "L:" + l.Name;
            case SpecialPowerTemplate s when ReferenceEquals(game.Catalog.SpecialPower(s.Name), s):
                return "S:" + s.Name;
            case CommandButton b when ReferenceEquals(game.Catalog.CommandButtonTemplate(b.Name), b):
                return "B:" + b.Name;
            case RuleNode node:
                if (IndexOf(game.Rules.Root).Index.TryGetValue(node, out int at))
                    return "R:" + at;
                return TemplateNodes().GetValueOrDefault(node);
        }
        return null;
    }

    /// <summary>Nodes inside the templates' own (resolved) rules: "N:template#index".</summary>
    Dictionary<RuleNode, string> TemplateNodes()
    {
        if (templateNodes != null)
            return templateNodes;
        templateNodes = new Dictionary<RuleNode, string>(ReferenceEqualityComparer.Instance);
        foreach (var t in game.Templates.Loaded)
        {
            var index = IndexOf(t.Rules);
            for (int i = 0; i < index.Order.Count; i++)
                templateNodes.TryAdd(index.Order[i], $"N:{t.Name}#{i}");
        }
        return templateNodes;
    }

    /// <summary>The loading game's own copy of what a key names.</summary>
    public object Resolve(string key)
    {
        if (singletonsByKey.TryGetValue(key, out var single))
            return single;
        int colon = key.IndexOf(':');
        string kind = colon > 0 ? key[..colon] : "", name = colon > 0 ? key[(colon + 1)..] : key;
        object? found = kind switch
        {
            "T" => game.Templates.Find(name),
            "M" => Split(name) is var (t, i) && game.Templates.Find(t) is { } mt && i < mt.Modules.Count ? mt.Modules[i] : null,
            "W" => game.Catalog.Weapon(name),
            "A" => game.Catalog.Armor(name),
            "L" => game.Catalog.Locomotor(name),
            "S" => game.Catalog.SpecialPower(name),
            "B" => game.Catalog.CommandButtonTemplate(name),
            "R" => int.TryParse(name, out int r) && IndexOf(game.Rules.Root).Order is var order && r < order.Count ? order[r] : null,
            "N" => Split(name) is var (tn, ni) && game.Templates.Find(tn) is { } nt && IndexOf(nt.Rules).Order is var nodes && ni < nodes.Count ? nodes[ni] : null,
            _ => null,
        };
        return found ?? throw new SaveGameException($"The saved game needs '{key}', which these rules don't have (made with other games or mods installed?).");
    }

    static (string, int) Split(string s)
    {
        int hash = s.LastIndexOf('#');
        return (s[..hash], int.Parse(s[(hash + 1)..]));
    }
}

/// <summary>A saved game that can't be read (damaged, or made with other rules).</summary>
public sealed class SaveGameException(string message, Exception? inner = null) : Exception(message, inner);
