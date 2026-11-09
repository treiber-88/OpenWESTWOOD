using OpenWestwood.Core.Rules;

namespace OpenWestwood.Logic.Players;

/// <summary>
/// Finds a player template (faction) by name, the original's PlayerTemplateStore::findPlayerTemplate: including
/// its "hokey code to quietly load old maps", which turns the factions of Generals' development (still named by
/// many of its campaign maps, e.g. USA01's FactionAmericaTankCommand) into America, China and GLA.
/// </summary>
public static class PlayerTemplateStore
{
    static readonly Dictionary<string, string> OldFactions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FactionAmericaChooseAGeneral"] = "FactionAmerica",
        ["FactionAmericaTankCommand"] = "FactionAmerica",
        ["FactionAmericaSpecialForces"] = "FactionAmerica",
        ["FactionAmericaAirForce"] = "FactionAmerica",
        ["FactionChinaChooseAGeneral"] = "FactionChina",
        ["FactionChinaRedArmy"] = "FactionChina",
        ["FactionChinaSpecialWeapons"] = "FactionChina",
        ["FactionChinaSecretPolice"] = "FactionChina",
        ["FactionGLAChooseAGeneral"] = "FactionGLA",
        ["FactionGLATerrorCell"] = "FactionGLA",
        ["FactionGLABiowarCommand"] = "FactionGLA",
        ["FactionGLAWarlordCommand"] = "FactionGLA",
    };

    /// <summary>The faction an old name stands for (the name itself otherwise).</summary>
    public static string Name(string name) => OldFactions.GetValueOrDefault(name, name);

    public static RuleNode? Find(RuleSet rules, string name) => rules.Get("PlayerTemplates", Name(name));
}
