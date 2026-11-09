using System.Text;
using System.Xml.Linq;

namespace OpenWestwood.Tools;

public static partial class KwFactionExporter
{
    /// <summary>
    /// Kane's Wrath's upgrades: the UpgradeTemplates its buildings research (PLAYER_UPGRADE logic commands: upgrades.yaml
    /// "KW_&lt;id&gt;", buttons "Command_KW_&lt;command&gt;") and what they do to objects: weapons that only an upgrade's status
    /// unlocks (StatusBitsUpgrade WEAPON_UPGRADED_0n: upgraded weapon sets), PLAYER_UPGRADE armor sets (ArmorUpgrade), parts of
    /// the model shown or hidden (SubObjectsUpgrade) and attribute modifiers (damage, rate of fire, range, speed, armor).
    /// Not converted: the buildings an upgrade also requires (its GameDependency), and upgrades given by veterancy.
    /// </summary>
    sealed partial class Exporter
    {
        readonly StringBuilder upgrades = new("Upgrades:\n");
        readonly HashSet<string> doneUpgrades = new(StringComparer.OrdinalIgnoreCase);
        public int UpgradeCount => doneUpgrades.Count;

        /// <summary>Kane's Wrath's status bits for upgraded weapons and the weapon set flags that stand for them.</summary>
        static readonly Dictionary<string, string> WeaponStatusFlags = new(StringComparer.OrdinalIgnoreCase)
        {
            ["WEAPON_UPGRADED_01"] = "PLAYER_UPGRADE", ["WEAPON_UPGRADED_02"] = "CRATEUPGRADE_ONE", ["WEAPON_UPGRADED_03"] = "CRATEUPGRADE_TWO",
        };

        /// <summary>A researchable upgrade (one with a price), exported; null if it isn't one.</summary>
        string? Upgrade(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || x.Get("UpgradeTemplate", id.Trim()) is not { } u)
                return null;
            id = id.Trim();
            string name = P + id;
            if (!doneUpgrades.Add(id))
                return name;
            upgrades.Append($"  {name}:\n    DisplayName: {Text(x.Attr(u, "DisplayName"), id)}\n    Type: {x.Attr(u, "Type", "PLAYER").ToUpperInvariant()}\n");
            upgrades.Append($"    BuildTime: {F(x.Ms(u, "BuildTime", 30000) / 1000f)}\n    BuildCost: {F(x.Num(u, "BuildCost"))}\n");
            if (x.Attr(u, "IconImage") is { Length: > 0 } icon)
                upgrades.Append($"    ButtonImage: KWCAMEO:{icon}\n");
            if (Snd(x.Attr(u, "ResearchSound")) is { Length: > 0 } sound)
                upgrades.Append($"    ResearchSound: {sound}\n");
            return name;
        }

        /// <summary>The upgrade buttons of a building's command set (its PLAYER_UPGRADE commands).</summary>
        List<string> UpgradeButtons(XElement o)
        {
            var result = new List<string>();
            var set = x.Get("LogicCommandSet", x.Attr(o, "CommandSet"));
            foreach (var cmd in KwXml.Children(set, "Cmd"))
            {
                string cmdId = cmd.Value.Trim();
                var command = x.Get("LogicCommand", cmdId);
                if (command == null || !x.Attr(command, "Type").Equals("PLAYER_UPGRADE", StringComparison.OrdinalIgnoreCase))
                    continue;
                string upgradeId = KwXml.Child(command, "Upgrade")?.Value.Trim() ?? "";
                var u = x.Get("UpgradeTemplate", upgradeId);
                if (u == null || x.Num(u, "BuildCost") <= 0 || Upgrade(upgradeId) is not { } upgrade)
                    continue;
                string button = $"Command_KW_{cmdId}";
                if (doneButtons.Add(button))
                {
                    string label = Text(x.Attr(u, "DisplayName"), upgradeId);
                    buttons.Append($"  {button}:\n    Command: PLAYER_UPGRADE\n    Upgrade: {upgrade}\n    TextLabel: {label}\n");
                    if (x.Attr(u, "IconImage") is { Length: > 0 } icon)
                        buttons.Append($"    ButtonImage: KWCAMEO:{icon}\n");
                    buttons.Append($"    ButtonBorderType: UPGRADE\n    DescriptLabel: {Text(x.Attr(u, "Description"), label)}\n");
                }
                if (!result.Contains(button))
                    result.Add(button);
            }
            return result;
        }

        /// <summary>The upgrades (exported researchable ones) a module waits for, as its TriggeredBy, or "".</summary>
        string Triggers(XElement module)
        {
            var names = KwXml.Children(module, "TriggeredBy").Select(t => t.Value.Trim())
                .Where(t => x.Get("UpgradeTemplate", t) is { } u && x.Num(u, "BuildCost") > 0).Select(t => Upgrade(t)!).Distinct().ToList();
            return string.Join(' ', names);
        }

        /// <summary>
        /// The weapon sets: the plain one, and for each mix of upgraded-weapon statuses the object's upgrades set, the set with
        /// those weapons swapped in. Also returns the statuses an upgrade sets.
        /// </summary>
        (string Yaml, List<string> Slots) WeaponSets(XElement o, List<(string Ordering, string Weapon, string Status)> weapons)
        {
            var plain = weapons.Where(w => w.Status.Length == 0).GroupBy(w => w.Ordering).Select(g => g.First()).ToList();
            var slots = plain.Select(w => $"{w.Ordering} {w.Weapon}").ToList();
            var statuses = weapons.Select(w => w.Status).Where(s => WeaponStatusFlags.ContainsKey(s)).Distinct().OrderBy(s => s).ToList();
            // (Only statuses some exported upgrade sets.)
            statuses = statuses.Where(s => KwXml.AllModules(o).Any(m => m.Name.LocalName == "StatusBitsUpgrade"
                && x.Attr(m, "StatusToSet").Split(' ').Contains(s, StringComparer.OrdinalIgnoreCase) && Triggers(m).Length > 0)).ToList();
            if (slots.Count == 0 && statuses.Count == 0)
                return ("", slots);
            if (statuses.Count == 0)
                return ($"    WeaponSet:\n      Conditions: None\n      Weapon: [{string.Join(", ", slots)}]\n", slots);
            var sb = new StringBuilder("    WeaponSet:\n");
            sb.Append($"    - Conditions: None\n      Weapon: {(slots.Count > 0 ? $"[{string.Join(", ", slots)}]" : "PRIMARY None")}\n");
            for (int mask = 1; mask < 1 << statuses.Count; mask++)
            {
                var on = statuses.Where((_, i) => (mask >> i & 1) != 0).ToList();
                var set = plain.ToDictionary(w => w.Ordering, w => w.Weapon);
                foreach (var s in on)
                    foreach (var w in weapons.Where(w => w.Status.Equals(s, StringComparison.OrdinalIgnoreCase)))
                        set[w.Ordering] = w.Weapon;
                var order = new[] { "PRIMARY", "SECONDARY", "TERTIARY" }.Where(set.ContainsKey);
                sb.Append($"    - Conditions: {string.Join(' ', on.Select(s => WeaponStatusFlags[s]))}\n      Weapon: [{string.Join(", ", order.Select(k => $"{k} {set[k]}"))}]\n");
            }
            return (sb.ToString(), slots);
        }

        /// <summary>The upgrade modules of an object, as Zero Hour's (numbered from ModuleTag_U01).</summary>
        string UpgradeModules(XElement o)
        {
            var sb = new StringBuilder();
            int n = 0;
            string Tag() => $"      ModuleTag_U{++n:00}:\n        Module: Behavior\n";
            foreach (var m in KwXml.AllModules(o))
            {
                if (!m.Name.LocalName.EndsWith("Upgrade") || Triggers(m) is not { Length: > 0 } triggers)
                    continue;
                switch (m.Name.LocalName)
                {
                    case "StatusBitsUpgrade":
                        foreach (string status in x.Attr(m, "StatusToSet").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            if (WeaponStatusFlags.TryGetValue(status, out var flag))
                                sb.Append(Tag()).Append($"        Class: WeaponSetUpgrade\n        WeaponSetFlag: {flag}\n        TriggeredBy: {triggers}\n");
                            else if (status.Equals("SHIELDBODY_ENABLED", StringComparison.OrdinalIgnoreCase))
                                sb.Append(Tag()).Append($"        Class: StatusBitsUpgrade\n        StatusToSet: SHIELDBODY_ENABLED\n        TriggeredBy: {triggers}\n");
                        break;
                    case "ArmorUpgrade":
                        sb.Append(Tag()).Append($"        Class: ArmorUpgrade\n        TriggeredBy: {triggers}\n");
                        break;
                    case "SubObjectsUpgrade":
                    {
                        string show = x.Attr(m, "ShowSubObjects").ToUpperInvariant(), hide = x.Attr(m, "HideSubObjects").ToUpperInvariant();
                        if (show.Length == 0 && hide.Length == 0)
                            break;
                        sb.Append(Tag()).Append("        Class: SubObjectsUpgrade\n");
                        if (show.Length > 0)
                            sb.Append($"        ShowSubObjects: {show}\n");
                        if (hide.Length > 0)
                            sb.Append($"        HideSubObjects: {hide}\n");
                        sb.Append($"        TriggeredBy: {triggers}\n");
                        break;
                    }
                    case "AttributeModifierUpgrade":
                    {
                        var modifier = x.Get("AttributeModifier", x.Attr(m, "AttributeModifier"));
                        var fields = new List<string>();
                        foreach (var mod in KwXml.Children(modifier, "Modifier"))
                        {
                            string key = x.Attr(mod, "Type").ToUpperInvariant() switch
                            {
                                "DAMAGE_MULT" => "Damage", "RATE_OF_FIRE" => "RateOfFire", "RANGE" => "Range", "SPEED" => "Speed",
                                "VISION" => "Vision", "ARMOR" => "Armor", _ => "",
                            };
                            if (key.Length > 0)
                                fields.Add($"        {key}: {F(KwXml.ParseNumber(x.Attr(mod, "Value"), 100))}%\n");
                        }
                        if (fields.Count > 0)
                            sb.Append(Tag()).Append("        Class: AttributeModifierUpgrade\n").Append(string.Concat(fields)).Append($"        TriggeredBy: {triggers}\n");
                        break;
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>The ShieldBody's settings (the Scrin Forcefield Generator), as module fields.</summary>
        string ShieldSettings(XElement body)
        {
            var sb = new StringBuilder($"        ShieldAmount: {F(x.Num(body, "ShieldAmount"))}\n");
            sb.Append($"        ShieldRechargeIdleTime: {F(x.Ms(body, "ShieldRechargeIdleTime", 0))}\n");
            if (ArmorFor(new XElement(KwXml.Ns + "ArmorSet", new XAttribute("Armor", x.Attr(body, "ShieldArmor")))) is { } armorName)
                sb.Append($"        ShieldArmor: {armorName}\n");
            foreach (string key in new[] { "ShieldEnabledFX", "ShieldTakeDamageFX", "ShieldDepleteFX", "ShieldRechargeEndFX" })
                if (Fx(x.Attr(body, key)) is { Length: > 0 } fx)
                    sb.Append($"        {key}: {fx}\n");
            if (x.Attr(body, "ShieldActiveModelCondition") is { Length: > 0 } condition && !condition.Equals("INVALID", StringComparison.OrdinalIgnoreCase))
                sb.Append($"        ShieldActiveModelCondition: {condition.ToUpperInvariant()}\n");
            return sb.ToString();
        }

        /// <summary>The armor set used while a ShieldBody's shield is enabled (SHIELDBODY_ENABLED), or "".</summary>
        string ShieldArmorSet(XElement o)
        {
            var set = KwXml.Children(o, "ArmorSet").FirstOrDefault(a => x.Attr(a, "Conditions").Contains("SHIELDBODY_ENABLED", StringComparison.OrdinalIgnoreCase));
            if (set == null || !KwXml.AllModules(o).Any(m => m.Name.LocalName == "StatusBitsUpgrade"
                    && x.Attr(m, "StatusToSet").Contains("SHIELDBODY_ENABLED", StringComparison.OrdinalIgnoreCase) && Triggers(m).Length > 0))
                return "";
            return ArmorFor(set) ?? "";
        }

        /// <summary>The PLAYER_UPGRADE armor set (composite armor and the like), or "".</summary>
        string UpgradedArmorSet(XElement o)
        {
            var set = KwXml.Children(o, "ArmorSet").FirstOrDefault(a => x.Attr(a, "Conditions").Contains("PLAYER_UPGRADE", StringComparison.OrdinalIgnoreCase));
            if (set == null || !KwXml.AllModules(o).Any(m => m.Name.LocalName == "ArmorUpgrade" && Triggers(m).Length > 0))
                return "";
            return ArmorFor(set) is { } armorName ? armorName : "";
        }
    }
}
