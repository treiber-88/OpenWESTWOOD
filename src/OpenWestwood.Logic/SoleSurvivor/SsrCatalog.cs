using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Logic.SoleSurvivor;

/// <summary>A unit's kind in Sole Survivor Remix's choice screen.</summary>
public enum SsrCategory { Vehicle, Infantry, Aircraft, Naval }

/// <summary>A unit a player can be: its template, kind and price (its original game's build cost).</summary>
/// <param name="Power">How hard it hits units: its weapons' damage a second (0: nothing that harms a unit; the computer
/// players don't pick those).</param>
public sealed record SsrUnit(string Template, string DisplayName, string ButtonImage, SsrCategory Category, int Cost, float Power = 0);

/// <summary>A faction to play: its game, PlayerTemplate and the units it can be.</summary>
public sealed record SsrFaction(string Game, string Name, string DisplayName, string Side, IReadOnlyList<SsrUnit> Units);

/// <summary>
/// OpenWESTWOOD: Sole Survivor Remix's catalogue: every game's factions (as the skirmish lobby offers them) and the
/// fighting units each one builds (from its buildings' build buttons): infantry, vehicles, aircraft that can hover
/// (fixed-wing jets need their airfields) and boats. Harvesters, construction vehicles and units without a weapon are left
/// out.
/// </summary>
public static class SsrCatalog
{
    /// <summary>The game a faction comes from, by its PlayerTemplate name.</summary>
    public static string GameOf(string faction) => faction switch
    {
        _ when faction.StartsWith("FactionTD", StringComparison.OrdinalIgnoreCase) => "Tiberian Dawn",
        _ when faction.StartsWith("FactionRA2", StringComparison.OrdinalIgnoreCase) => "Red Alert 2",
        _ when faction.StartsWith("FactionRA", StringComparison.OrdinalIgnoreCase) => "Red Alert",
        _ when faction.StartsWith("FactionTS", StringComparison.OrdinalIgnoreCase) => "Tiberian Sun",
        _ when faction.StartsWith("FactionD2K", StringComparison.OrdinalIgnoreCase) => "Dune 2000",
        _ when faction.StartsWith("FactionEM", StringComparison.OrdinalIgnoreCase) => "Emperor: Battle for Dune",
        _ when faction.StartsWith("FactionKW", StringComparison.OrdinalIgnoreCase) => "Kane's Wrath",
        _ => "Zero Hour",
    };

    /// <summary>The games, in the order offered.</summary>
    public static readonly string[] Games =
        ["Zero Hour", "Tiberian Dawn", "Red Alert", "Tiberian Sun", "Red Alert 2", "Dune 2000", "Emperor: Battle for Dune", "Kane's Wrath"];

    static readonly Dictionary<RuleSet, List<SsrFaction>> cache = new();

    /// <summary>Every playable faction with its units (worked out once per rule set).</summary>
    public static IReadOnlyList<SsrFaction> Factions(RuleSet rules, TemplateStore templates, Func<RuleNode, bool>? allowed = null)
    {
        lock (cache)
        {
            if (!cache.TryGetValue(rules, out var list))
                cache[rules] = list = Build(rules, templates);
            return allowed == null ? list : list.Where(f => rules.Get("PlayerTemplates", f.Name) is { } node && allowed(node)).ToList();
        }
    }

    static List<SsrFaction> Build(RuleSet rules, TemplateStore templates)
    {
        var objects = rules.Section("Objects");
        var catalog = new RulesCatalog(rules);
        var result = new List<SsrFaction>();
        var seenSides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in rules.Section("PlayerTemplates").Entries)
        {
            if (!Multiplayer.GameInfo.IsLobbyFaction(node))
                continue;
            string side = node.GetString("Side");
            if (side.Length == 0 || !seenSides.Add(side))
                continue;
            // The units this side's buildings make (their command sets' build buttons).
            var unitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (objectName, obj) in objects.Entries)
            {
                if (!obj.GetString("Side").Equals(side, StringComparison.OrdinalIgnoreCase) || obj.GetString("CommandSet") is not { Length: > 0 } set)
                    continue;
                foreach (string unit in CommandSets.BuildableObjects(catalog, set, "UNIT_BUILD"))
                    unitNames.Add(unit);
            }
            var units = new List<SsrUnit>();
            foreach (string unitName in unitNames)
                if (templates.Find(unitName) is { } t && Unit(t, catalog) is { } unit)
                    units.Add(unit);
            if (units.Count == 0)
                continue;
            units.Sort((a, b) => a.Cost != b.Cost ? a.Cost.CompareTo(b.Cost) : string.Compare(a.Template, b.Template, StringComparison.Ordinal));
            string display = node.GetString("DisplayName");
            if (display.Length == 0 || display.Contains(':'))
                display = name.StartsWith("Faction") ? name[7..] : name;
            result.Add(new SsrFaction(GameOf(name), name, display, side, units));
        }
        return result;
    }

    static readonly HashSet<string> HarmlessDamage = new(StringComparer.OrdinalIgnoreCase)
        { "MICROWAVE", "HACK", "STATUS", "SURRENDER", "KILL_PILOT", "DEPLOY", "PENALTY", "HEALING", "SUBDUAL_BUILDING", "SUBDUAL_VEHICLE", "DISARM", "HAZARD_CLEANUP", "KILL_GARRISONED" };

    /// <summary>A unit's damage a second from its first weapon set (secondary weapons half), harmless kinds left out.</summary>
    static float Power(ObjectTemplate t, RulesCatalog catalog)
    {
        if (!t.Rules.TryGet("WeaponSet", out var sets) || sets.AsList().FirstOrDefault() is not { } set || !set.TryGet("Weapon", out var weapons))
            return 0;
        float power = 0;
        foreach (var entry in weapons.AsList())
        {
            var words = RuleValue.Words(entry.Value ?? "");
            if (words.Length < 2 || catalog.Weapon(words[1]) is not { } w || HarmlessDamage.Contains(w.DamageType) || w.PrimaryDamage <= 0)
                continue;
            float perSecond = (w.PrimaryDamage + w.SecondaryDamage * 0.5f) * LogicTimeFrames / Math.Max(1, (w.MinDelayBetweenShots + w.MaxDelayBetweenShots) / 2f);
            power += words[0].Equals("PRIMARY", StringComparison.OrdinalIgnoreCase) ? perSecond : perSecond * 0.5f;
        }
        return power;
    }

    const float LogicTimeFrames = Core.LogicTime.FramesPerSecond;

    static SsrUnit? Unit(ObjectTemplate t, RulesCatalog catalog)
    {
        if (t.Name.Contains("MCV", StringComparison.OrdinalIgnoreCase) || t.Is("STRUCTURE") || t.Is("HARVESTER") || t.Is("DOZER") || t.Is("TD_MCV") || t.Is("COMMANDCENTER") || !t.Rules.TryGet("WeaponSet", out _))
            return null;
        // (Jets that need a runway can't be flown alone; helicopters, JetAIUpdate with NeedsRunway No, can.)
        if (t.Modules.Any(m => m.ClassName.Equals("JetAIUpdate", StringComparison.OrdinalIgnoreCase) && RuleValue.Bool(m.Settings.GetString("NeedsRunway"), true)
                || m.ClassName.Equals("TdDeployToStructure", StringComparison.OrdinalIgnoreCase)
                || m.ClassName.Equals("SupplyTruckAIUpdate", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (!t.Is("INFANTRY") && !t.Is("VEHICLE") && !t.Is("AIRCRAFT"))
            return null;
        var category = t.Is("AIRCRAFT") ? SsrCategory.Aircraft : t.Is("INFANTRY") ? SsrCategory.Infantry
            : IsNaval(t) ? SsrCategory.Naval : SsrCategory.Vehicle;
        return new SsrUnit(t.Name, t.DisplayName, t.ButtonImage, category, (int)t.BuildCost, Power(t, catalog));
    }

    static bool IsNaval(ObjectTemplate t) =>
        t.Is("BOAT") || t.Is("SHIP") || t.Modules.Any(m => m.ModuleType.Equals("Draw", StringComparison.OrdinalIgnoreCase)
            && (m.Settings.GetString("TdKind").Equals("Vessel", StringComparison.OrdinalIgnoreCase) || m.Settings.GetString("Kind").Equals("Vessel", StringComparison.OrdinalIgnoreCase)));
}
