using System.Text;
using System.Xml.Linq;

namespace OpenWestwood.Tools;

public static partial class KwFactionExporter
{
    /// <summary>
    /// Kane's Wrath's structure powers (OCLSpecialPower: the Ion Cannon, the nuclear missile, the Rift Generator and the
    /// support powers) as Zero Hour OCLSpecialPowers (special-powers.yaml "KW_&lt;power&gt;", object-creation-lists.yaml
    /// "KWOCL_&lt;power&gt;", buttons "Command_KW_&lt;power&gt;"). A power's real effect is its special weapon's object creation
    /// list (WeaponFireSpecialAbilityUpdate SpecialWeapon → WeaponOCLNugget) or else its own OCL; what those create are effect
    /// objects (a model with particle systems, a lifetime, weapons fired on a timer or at death, FX at birth and death), made
    /// here as objects. A special weapon's own damage (the nuke's, after DelayTimeSeconds) becomes a strike object that fires
    /// it when the delay is up. Not converted: powers whose OCL creates units or aircraft on a scripted run (the support
    /// aircraft, drop pods), and the other special-power modules (teleports, cloning, commandeering...).
    /// </summary>
    sealed partial class Exporter
    {
        readonly StringBuilder powers = new("SpecialPowers:\n");
        readonly StringBuilder ocls = new("ObjectCreationLists:\n");
        readonly HashSet<string> donePowers = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string?> doneEffects = new(StringComparer.OrdinalIgnoreCase);
        readonly StringBuilder effects = new("Objects:\n");
        public int PowerCount => donePowers.Count;

        Dictionary<string, string[]>? powerButtonStates;

        /// <summary>
        /// A power's command bar picture: its PlayerPowerButtonTemplate's StateData, whose ButtonSingleStateData State has
        /// the Image (the support powers have no TimerImage).
        /// </summary>
        string? PowerButtonImage(string specialPower) => PowerButtonState(specialPower, 0);

        /// <summary>A power's command bar button state: 0 its Image, 1 its Title label, 2 its Description label.</summary>
        string? PowerButtonState(string specialPower, int field)
        {
            if (powerButtonStates == null)
            {
                string text = string.Join("\n", Directory.EnumerateFiles(x.Root, "*.xml", SearchOption.AllDirectories).Select(File.ReadAllText));
                var options = System.Text.RegularExpressions.RegexOptions.Singleline;
                var stateImage = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                    @"<Button(?:Single|Multi)StateData\s+id=""([^""]+)""\s*>(.*?)</Button(?:Single|Multi)StateData>", options))
                {
                    string Field(string name) => System.Text.RegularExpressions.Regex.Match(m.Groups[2].Value, name + @"=""([^""]+)""") is { Success: true } f ? f.Groups[1].Value : "";
                    stateImage.TryAdd(m.Groups[1].Value, [Field("Image"), Field("Title"), Field("Description")]);
                }
                powerButtonStates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                    @"<(?:PlayerPowerButtonTemplate|JoypadCommandBarButtonTemplate)\b(.*?)</(?:PlayerPowerButtonTemplate|JoypadCommandBarButtonTemplate)>", options))
                {
                    var power = System.Text.RegularExpressions.Regex.Match(m.Groups[1].Value, @"SpecialPower=""([^""]+)""");
                    var state = System.Text.RegularExpressions.Regex.Match(m.Groups[1].Value, @"StateData=""([^""]+)""");
                    if (power.Success && state.Success && stateImage.TryGetValue(state.Groups[1].Value, out var values))
                        powerButtonStates.TryAdd(power.Groups[1].Value, values);
                }
            }
            return powerButtonStates.TryGetValue(specialPower, out var found) && found[field].Length > 0 ? found[field] : null;
        }

        static readonly HashSet<string> MobileKinds = new(StringComparer.OrdinalIgnoreCase) { "INFANTRY", "VEHICLE", "AIRCRAFT", "HARVESTER" };

        /// <summary>An effect object (no AI, not a unit), exported; null if it isn't one.</summary>
        string? EffectObject(string id)
        {
            if (doneEffects.TryGetValue(id, out var done))
                return done;
            doneEffects[id] = null;
            var o = Obj(id);
            if (o == null || KwXml.Modules(o, "AI").Any() || KindOf(o).Any(MobileKinds.Contains))
                return null;
            var modules = KwXml.AllModules(o).ToList();
            bool lifetime = modules.Any(m => m.Name.LocalName == "LifetimeUpdate");
            if (!lifetime && !modules.Any(m => m.Name.LocalName is "FireWeaponUpdate" or "FireWeaponWhenDead"))
                return null;
            string name = P + id;
            doneEffects[id] = name;
            var sb = new StringBuilder();
            sb.Append($"  {name}:\n    EditorSorting: SYSTEM\n    KindOf: IMMOBILE NO_COLLIDE UNATTACKABLE\n    VisionRange: {F(x.Num(KwXml.Child(o, "VisionInfo"), "VisionRange", 200))}\n");
            sb.Append("    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n    Modules:\n");
            sb.Append(Draw(o, null));
            sb.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 9999999\n        InitialHealth: 9999999\n");
            sb.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
            int n = 0;
            string Tag() => $"      ModuleTag_E{++n:00}:\n        Module: Behavior\n";
            foreach (var m in modules)
            {
                switch (m.Name.LocalName)
                {
                    case "LifetimeUpdate":
                        sb.Append(Tag()).Append($"        Class: LifetimeUpdate\n        MinLifetime: {F(x.Ms(m, "MinLifetime", 1000))}\n        MaxLifetime: {F(x.Ms(m, "MaxLifetime", x.Ms(m, "MinLifetime", 1000)))}\n");
                        break;
                    case "FireWeaponUpdate":
                        foreach (var nugget in KwXml.Children(m, "FireWeaponNugget"))
                            if (Weapon(x.Attr(nugget, "WeaponName"), o) is { } weapon)
                                sb.Append(Tag()).Append($"        Class: FireWeaponUpdate\n        Weapon: {weapon}\n        InitialDelay: {F(x.Ms(nugget, "FireDelay"))}\n"
                                    + (x.Bool(nugget, "OneShot") ? "        OneShot: Yes\n" : $"        RepeatDelay: {F(Math.Max(x.Ms(nugget, "FireDelay"), 33))}\n"));
                        break;
                    case "FireWeaponWhenDead":
                        if (Weapon(x.Attr(m, "DeathWeapon"), o) is { } deathWeapon)
                            sb.Append(Tag()).Append($"        Class: FireWeaponWhenDeadBehavior\n        DeathWeapon: {deathWeapon}\n        StartsActive: Yes\n");
                        break;
                    case "FXListBehavior":
                        foreach (var e in KwXml.Children(m, "Event"))
                        {
                            string fx = Fx(x.Attr(e, "FX"));
                            if (fx.Length == 0)
                                continue;
                            if (x.Attr(e, "Index") == "onCreate")
                                sb.Append(Tag()).Append($"        Class: FXOnCreateBehavior\n        FX: {fx}\n");
                            else if (x.Attr(e, "Index") == "onDeath")
                                sb.Append(Tag()).Append($"        Class: FXListDie\n        DeathFX: {fx}\n");
                        }
                        break;
                }
            }
            effects.Append(sb);
            return name;
        }

        /// <summary>A strike: fires a weapon where it is made, after a delay, then goes.</summary>
        string Strike(string power, string weapon, float delayMs)
        {
            string name = $"{P}{power}_Strike";
            effects.Append($"  {name}:\n    EditorSorting: SYSTEM\n    KindOf: IMMOBILE NO_COLLIDE UNATTACKABLE\n    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n    Modules:\n");
            effects.Append("      ModuleTag_01:\n        Module: Draw\n        Class: W3DModelDraw\n        DefaultConditionState:\n          Model: None\n");
            effects.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 9999999\n        InitialHealth: 9999999\n");
            effects.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
            effects.Append($"      ModuleTag_04:\n        Module: Behavior\n        Class: LifetimeUpdate\n        MinLifetime: {F(delayMs + 500)}\n        MaxLifetime: {F(delayMs + 500)}\n");
            effects.Append($"      ModuleTag_05:\n        Module: Behavior\n        Class: FireWeaponUpdate\n        Weapon: {weapon}\n        InitialDelay: {F(delayMs)}\n        OneShot: Yes\n");
            return name;
        }

        /// <summary>
        /// A structure's powers: (the OCLSpecialPower modules as yaml, their buttons). Powers that make nothing this can
        /// convert are left out.
        /// </summary>
        (string Modules, List<string> Buttons) Powers(XElement o)
        {
            var sb = new StringBuilder();
            var result = new List<string>();
            var modules = KwXml.AllModules(o).ToList();
            int n = 0;
            foreach (var m in modules.Where(m => m.Name.LocalName == "OCLSpecialPower"))
            {
                string spId = x.Attr(m, "SpecialPowerTemplate");
                var sp = x.Get("SpecialPowerTemplate", spId);
                if (sp == null)
                    continue;
                string power = P + spId, ocl = "KWOCL_" + spId, button = "Command_KW_" + spId;
                if (!donePowers.Contains(spId))
                {
                    // What it makes: its special weapon's creation list and damage, or its own creation list.
                    var nuggets = new StringBuilder();
                    var fire = modules.FirstOrDefault(f => f.Name.LocalName == "WeaponFireSpecialAbilityUpdate" && x.Attr(f, "SpecialPowerTemplate") == spId);
                    var special = x.Get("WeaponTemplate", x.Attr(fire, "SpecialWeapon"));
                    string? oclId = KwXml.Child(special, "Nuggets")?.Elements().Where(g => g.Name.LocalName == "WeaponOCLNugget").Select(g => x.Attr(g, "WeaponOCL")).FirstOrDefault(s => s.Length > 0)
                                    ?? x.Attr(m, "OCL");
                    var list = x.Get("ObjectCreationList", oclId);
                    foreach (var create in list?.Descendants(KwXml.Ns + "CreateObject").Where(c => !c.HasElements) ?? [])
                        foreach (string made in create.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            if (EffectObject(made) is { } effect)
                                nuggets.Append($"    - Type: CreateObject\n      ObjectNames: {effect}\n      Count: 1\n      Offset: X:0 Y:0 Z:0\n");
                    var damage = KwXml.Child(special, "Nuggets")?.Elements().FirstOrDefault(g => g.Name.LocalName == "DamageNugget");
                    if (damage != null && Weapon(x.Attr(special, "id"), o) is { } weapon)
                    {
                        float delay = x.Ms(damage, "DelayTimeSeconds");
                        if (delay > 0)
                            nuggets.Append($"    - Type: CreateObject\n      ObjectNames: {Strike(spId, weapon, delay)}\n      Count: 1\n      Offset: X:0 Y:0 Z:0\n");
                        else
                            nuggets.Append($"    - Type: FireWeapon\n      Weapon: {weapon}\n");
                    }
                    if (nuggets.Length == 0)
                        continue;
                    donePowers.Add(spId);
                    ocls.Append($"  {ocl}:\n    Nuggets:\n{nuggets}");
                    bool superweapon = x.Attr(sp, "Flags").Contains("HAS_PUBLIC_TIMER", StringComparison.OrdinalIgnoreCase);
                    powers.Append($"  {power}:\n    Enum: SPECIAL_KW_{spId.ToUpperInvariant()}\n    ReloadTime: {F(x.Ms(sp, "ReloadTime", 120000))}\n");
                    powers.Append($"    PublicTimer: {(superweapon ? "Yes" : "No")}\n    SharedSyncedTimer: No\n    ViewObjectDuration: 10000\n    ViewObjectRange: 150\n");
                    powers.Append($"    RadiusCursorRadius: {F(x.Num(sp, "RadiusCursorRadius", 50))}\n    ShortcutPower: Yes\n");
                    bool targeted = !x.Attr(sp, "TargetType", "LOCATION").Equals("NONE", StringComparison.OrdinalIgnoreCase);
                    // Its name: the power's DisplayName, else its command bar button's title (the support powers have none).
                    string label = Text(x.Attr(sp, "DisplayName"), Text(PowerButtonState(spId, 1), spId));
                    buttons.Append($"  {button}:\n    Command: SPECIAL_POWER\n    SpecialPower: {power}\n    Options: {(targeted ? "NEED_TARGET_POS " : "")}CONTEXTMODE_COMMAND\n");
                    buttons.Append($"    TextLabel: {label}\n");
                    // Its icon: the timer's picture, else its command bar button's (button template -> state data -> Image).
                    if ((x.Attr(sp, "TimerImage") is { Length: > 0 } timer ? timer : PowerButtonImage(spId)) is { Length: > 0 } image)
                        buttons.Append($"    ButtonImage: KWCAMEO:{image}\n");
                    buttons.Append($"    ButtonBorderType: ACTION\n    DescriptLabel: {Text(x.Attr(sp, "Description"), Text(PowerButtonState(spId, 2), label))}\n");
                }
                if (!donePowers.Contains(spId))
                    continue;
                var template = x.Get("SpecialPowerTemplate", spId)!;
                bool atTarget = !x.Attr(template, "TargetType", "LOCATION").Equals("NONE", StringComparison.OrdinalIgnoreCase);
                sb.Append($"      ModuleTag_P{++n:00}:\n        Module: Behavior\n        Class: OCLSpecialPower\n        SpecialPowerTemplate: {power}\n        OCL: {ocl}\n");
                sb.Append($"        CreateLocation: {(atTarget ? "CREATE_AT_LOCATION" : "USE_OWNER_OBJECT")}\n");
                result.Add(button);
            }
            return (sb.ToString(), result);
        }

        Dictionary<string, string[]>? abilityButtonStates;

        /// <summary>A unit ability button's state (UnitAbilityButtonTemplates.xml, by LogicCommand): 0 Image, 1 Title, 2 Description.</summary>
        string? AbilityButtonState(string command, int field)
        {
            if (abilityButtonStates == null)
            {
                abilityButtonStates = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                string path = Path.Combine(x.Root, "GlobalData", "UnitAbilityButtonTemplates.xml");
                string text = File.Exists(path) ? File.ReadAllText(path) : "";
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                    @"<\w+\s+Id=""([^""]+)""[^>]*>\s*<State\b([^>]*)>", System.Text.RegularExpressions.RegexOptions.Singleline))
                {
                    string Field(string name) => System.Text.RegularExpressions.Regex.Match(m.Groups[2].Value, name + @"=""([^""]+)""") is { Success: true } f ? f.Groups[1].Value : "";
                    abilityButtonStates.TryAdd(m.Groups[1].Value, [Field("Image"), Field("Title"), Field("Description")]);
                }
            }
            return abilityButtonStates.TryGetValue(command, out var found) && found[field].Length > 0 ? found[field] : null;
        }

        /// <summary>
        /// A unit's own abilities (its, or its squad's, TeleportSpecialAbilityUpdate: Blink Packs, the Eradicator's and the
        /// Mastermind's teleports, the Firehawk's Stratofighter boosters) as SpecialAbility modules with their buttons. A power
        /// that needs an upgrade (its GameDependency NeededUpgrade) asks for it as RequiredUpgrade.
        /// </summary>
        (string Modules, List<string> Buttons) UnitAbilities(XElement o, XElement? squad)
        {
            var sb = new StringBuilder();
            var result = new List<string>();
            var shown = squad ?? o;
            var commands = KwXml.Children(x.Get("LogicCommandSet", x.Attr(shown, "CommandSet")), "Cmd").Select(c => c.Value.Trim())
                .Select(c => (Id: c, Command: x.Get("LogicCommand", c))).Where(c => c.Command != null).ToList();
            int n = 0;
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in new[] { o, squad }.Where(s => s != null).SelectMany(s => KwXml.AllModules(s!)).Where(m => m.Name.LocalName == "TeleportSpecialAbilityUpdate"))
            {
                string spId = x.Attr(m, "SpecialPowerTemplate");
                if (x.Get("SpecialPowerTemplate", spId) is not { } sp || !done.Add(spId))
                    continue;
                var command = commands.FirstOrDefault(c => KwXml.Child(c.Command, "SpecialPower")?.Value.Trim() == spId);
                string cmdId = command.Id ?? spId;
                string power = P + spId, button = "Command_KW_" + (cmdId.StartsWith("Command_") ? cmdId["Command_".Length..] : cmdId);
                if (donePowers.Add(spId))
                {
                    powers.Append($"  {power}:\n    Enum: SPECIAL_KW_{spId.ToUpperInvariant()}\n    ReloadTime: {F(x.Ms(sp, "ReloadTime", 30000))}\n");
                    powers.Append($"    PublicTimer: No\n    SharedSyncedTimer: No\n    RadiusCursorRadius: {F(x.Num(sp, "RadiusCursorRadius", 16))}\n");
                    if (Snd(x.Attr(sp, "InitiateSound")) is { Length: > 0 } sound)
                        powers.Append($"    InitiateSound: {sound}\n");
                    string needed = KwXml.Child(sp, "GameDependency") is { } dep ? KwXml.Child(dep, "NeededUpgrade")?.Value.Trim() ?? "" : "";
                    if (needed.Length > 0 && x.Get("UpgradeTemplate", needed) is { } nu && x.Num(nu, "BuildCost") > 0 && Upgrade(needed) is { } upgrade)
                        powers.Append($"    RequiredUpgrade: {upgrade}\n");
                }
                if (doneButtons.Add(button))
                {
                    string options = x.Attr(command.Command, "Options", "NEED_TARGET_POS");
                    string label = Text(AbilityButtonState(cmdId, 1), Text(x.Attr(sp, "DisplayName"), spId));
                    buttons.Append($"  {button}:\n    Command: SPECIAL_POWER\n    SpecialPower: {power}\n    Options: {(options.Contains("NEED_TARGET") ? options : "NEED_TARGET_POS")}\n");
                    buttons.Append($"    TextLabel: {label}\n");
                    if (AbilityButtonState(cmdId, 0) is { } image)
                        buttons.Append($"    ButtonImage: KWCAMEO:{image}\n");
                    buttons.Append($"    ButtonBorderType: ACTION\n    DescriptLabel: {Text(AbilityButtonState(cmdId, 2), label)}\n");
                }
                sb.Append($"      ModuleTag_A{++n:00}:\n        Module: Behavior\n        Class: SpecialAbility\n        SpecialPowerTemplate: {power}\n        UpdateModuleStartsAttack: Yes\n");
                sb.Append($"      ModuleTag_A{++n:00}:\n        Module: Behavior\n        Class: TeleportSpecialAbilityUpdate\n        SpecialPowerTemplate: {power}\n");
                sb.Append($"        MaxDistance: {F(x.Num(m, "MaxDistance", 750))}\n        UnpackTime: {F(x.Ms(m, "UnpackTime"))}\n        PackTime: {F(x.Ms(m, "PackTime"))}\n        BusyDuration: {F(x.Ms(m, "BusyDuration"))}\n");
                if (x.Num(m, "HeightToTeleportTo") is > 0 and var height)
                    sb.Append($"        HeightToTeleportTo: {F(height)}\n");
                foreach (var (attr, key) in new[] { ("SourceWeapon", "SourceFX"), ("DestinationWeapon", "DestinationFX") })
                    if (Fx(x.Attr(x.Get("WeaponTemplate", x.Attr(m, attr)), "FireFX")) is { Length: > 0 } fx)
                        sb.Append($"        {key}: {fx}\n");
                result.Add(button);
            }
            return (sb.ToString(), result);
        }

        void WritePowers(string output, string header)
        {
            File.WriteAllText(Path.Combine(output, "special-powers.yaml"), header + powers);
            File.WriteAllText(Path.Combine(output, "object-creation-lists.yaml"), header + ocls);
            File.WriteAllText(Path.Combine(output, "effects.yaml"), header + effects);
        }
    }
}
