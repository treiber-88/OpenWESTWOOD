using System.Numerics;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Construction;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.Logic.Modules.Production;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Logic.AI;

/// <summary>
/// OpenWESTWOOD addition: the computer player for a Tiberian Dawn faction in a Zero Hour engine match.
/// The Zero Hour skirmish AI is driven by map scripts and build lists made for ZH factions, so a TD
/// faction gets Tiberian Dawn's own skirmish brain instead (the remaster's USE_RA_AI code in HOUSE.CPP):
/// <list type="bullet">
/// <item>it unfolds its MCV (UnitClass::Mission_Guard deploys a computer MCV);</item>
/// <item>HouseClass::AI_Building picks the next structure by urgency, with TD's RULES ratios and
/// limits, and the yard's finished structure is placed next to the base (Find_Build_Location);</item>
/// <item>AI_Unit / AI_Infantry: a harvester per refinery, then a weighted random pick (armed units 20, others 1);</item>
/// <item>attacks: TD sends its autocreated teams out; here, once AttackDelay minutes have passed, every
/// AttackInterval minutes the idle army attack-moves on the nearest enemy structure.</item>
/// </list>
/// TD's object types are recognised by the TdType each exported object carries (STRUCT_POWER, UNIT_HARVESTER...).
/// </summary>
public sealed class TdFactionAI : AIPlayer
{
    // RULES.INI defaults of Tiberian Dawn's RuleClass (ratios are fixed point, 256 = 1).
    const int PowerSurplus = 50, AttackIntervalMinutes = 3, AttackDelayMinutes = 5;
    // (OpenWESTWOOD: defences a fifth of the base rather than TD's 40%, so the money goes to the army.)
    const int RefineryRatio = 0x28, BarracksRatio = 0x28, WarRatio = 0x19, DefenseRatio = 0x33, AARatio = 0x24, HelipadRatio = 0x1E, AirstripRatio = 0x1E;
    // OpenWESTWOOD: bigger bases than TD's RULES (4 refineries, 2 barracks, 2 factories), and up to two expansions.
    const int RefineryLimit = 7, BarracksLimit = 3, WarLimit = 4, DefenseLimit = 14, AALimit = 8, HelipadLimit = 5, AirstripLimit = 5;
    const int MaxExpansions = 2;
    const int ThinkFrames = LogicTime.FramesPerSecond; // AI_Building returns TICKS_PER_SECOND
    const float TdCell = TdScale.Cell;

    enum Urgency { None, Low, Medium, High, Critical }

    int nextThinkFrame;
    int nextAttackFrame;

    public TdFactionAI(Player p) : base(p)
    {
        p.CanBuildUnits = true;
        nextAttackFrame = game.Frame + AttackDelayMinutes * 60 * LogicTime.FramesPerSecond;
    }

    /// <summary>Seconds before the first attack (five minutes; the shell battle's houses go sooner).</summary>
    public float AttackDelaySeconds
    {
        get => AttackDelayMinutes * 60;
        // (Each computer player a little later than the one before, so they don't all march on the same frame.)
        set => nextAttackFrame = game.Frame + (int)(value * LogicTime.FramesPerSecond) + player.Index * 2 * LogicTime.FramesPerSecond;
    }

    /// <summary>Seconds between attacks (three minutes).</summary>
    public float AttackIntervalSeconds { get; set; } = AttackIntervalMinutes * 60;

    public override bool IsSkirmishAI => true;

    /// <summary>A Tiberian Dawn object type name (STRUCT_POWER, UNIT_MTANK...) from the exported Draw module, or "".</summary>
    public static string TdTypeOf(ObjectTemplate template)
    {
        // Red Alert's objects name their role (their RA type: STRUCT_POWER, UNIT_HARVESTER...).
        if (template.Rules.GetString("TdRole") is { Length: > 0 } role)
            return role;
        foreach (var module in template.Modules)
            if (module.ClassName.Equals("TDSpriteDraw", StringComparison.OrdinalIgnoreCase) || module.ClassName.Equals("D2KSpriteDraw", StringComparison.OrdinalIgnoreCase))
                return module.Settings.GetString("TdType");
        return "";
    }

    bool difficultyApplied;

    /// <summary>
    /// OpenWESTWOOD: the lobby's difficulty (Easy Army / Hard Army; it made no difference to these factions before): an
    /// easy computer builds at 60% speed, keeps a small army (12), attacks later and with smaller waves; a hard one builds faster and attacks
    /// sooner with bigger ones. Medium is as it was.
    /// </summary>
    void ApplyDifficulty()
    {
        difficultyApplied = true;
        switch (AIDifficulty)
        {
            case Scripting.Difficulty.Easy:
                player.BuildSpeed = 0.6f;
                AttackDelaySeconds = 8 * 60;
                AttackIntervalSeconds = 4.5f * 60;
                MinimumWave = 5;
                WaveGrowth = 1;
                ArmyLimit = 12;
                break;
            case Scripting.Difficulty.Hard:
                player.BuildSpeed = 1.25f;
                AttackDelaySeconds = 4 * 60;
                AttackIntervalSeconds = 2.5f * 60;
                MinimumWave = 10;
                WaveGrowth = 4;
                break;
        }
    }

    public override void Update()
    {
        IssuePendingOrder();
        if (game.Frame < nextThinkFrame)
            return;
        nextThinkFrame = game.Frame + ThinkFrames;
        if (!difficultyApplied)
            ApplyDifficulty();

        var mine = game.ObjectsOwnedBy(player).Where(o => !o.IsEffectivelyDead).ToList();
        Expand(mine);
        RecoverEconomy(mine);
        DeployMcv(mine);
        PlaceReadyStructures(mine);
        BuildStructure(mine);
        ResearchUpgrades(mine);
        BuildUnits(mine);
        // (An easy computer leaves its superweapons alone.)
        if (AIDifficulty != Scripting.Difficulty.Easy)
            UsePowers(mine);
        Attack(mine);
        Muster(mine);
        SpreadOut(mine);
    }

    public override void NewMap() => ComputeBase();

    void ComputeBase()
    {
        var yard = game.ObjectsOwnedBy(player).FirstOrDefault(o => o.IsKindOf("COMMANDCENTER"));
        if (yard != null)
        {
            baseCenter = yard.Position;
            baseCenterSet = true;
        }
        else if (player.StartPosition is { } start)
        {
            baseCenter = start;
            baseCenterSet = true;
        }
    }

    // ---- MCV ----

    /// <summary>A computer MCV unfolds where it stands, or drives a little way off if the yard doesn't fit.</summary>
    void DeployMcv(List<GameObject> mine)
    {
        foreach (var obj in mine)
        {
            if (obj.FindModule<TdDeployToStructure>() is not { } deploy || obj.AI is not { } ai || ai.IsMoving)
                continue;
            // An MCV on its way to an expansion unfolds only once there (or when it has given up on getting there).
            if (travelling.TryGetValue(obj.Id, out var trip))
            {
                bool arrived = MathUtil.Distance2D(obj.Position, trip.To) < TdCell * 3;
                bool givenUp = game.Frame - trip.Since > 120 * LogicTime.FramesPerSecond;
                if (!arrived && !givenUp)
                {
                    ai.MoveTo(trip.To, Commands.CommandSource.AI);
                    continue;
                }
                travelling.Remove(obj.Id);
            }
            if (deploy.CanDeploy())
            {
                deploy.Deploy();
                ComputeBase();
            }
            else
                ai.MoveTo(obj.Position + MathUtil.Forward(game.Random.Float(0, MathF.Tau)) * TdCell * 2, Commands.CommandSource.AI);
        }
    }

    // ---- Structures ----

    static int Count(List<GameObject> mine, params string[] types) =>
        mine.Count(o => Array.IndexOf(types, TdTypeOf(o.Template)) >= 0 && !o.IsUnderConstruction);

    /// <summary>TD's Round_Up(Ratio(ratio, CurBuildings)).</summary>
    static int Wanted(int ratio, int buildings) => (ratio * buildings + 255) / 256;

    /// <summary>HouseClass::AI_Building: the most urgent structure the yard should make next.</summary>
    void BuildStructure(List<GameObject> mine)
    {
        // OpenWESTWOOD: every construction yard builds (the first base's, then each expansion's).
        bool first = true;
        foreach (var yard in mine.Where(o => !o.IsUnderConstruction && o.FindModule<ProductionUpdate>() is { PlacesStructures: true }).ToList())
        {
            if (yard.FindModule<ProductionUpdate>() is { } production && production.Queue.Count == 0)
                BuildStructure(mine, yard, production, first);
            first = false;
        }
    }

    void BuildStructure(List<GameObject> mine, GameObject yard, ProductionUpdate production, bool mainYard)
    {
        // Saving for an expansion's MCV: only power when short of it. Broke: nothing until a harvester is bought.
        if (SavingForExpansion && player.Energy.Production + player.Energy.Bonus > player.Energy.Consumption || IsBroke(mine))
            return;

        var buildable = CommandSets.BuildableObjects(game.Catalog, yard.CommandSetName, "UNIT_BUILD")
            .Select(n => game.Templates.Find(n)).OfType<ObjectTemplate>()
            .Where(t => TdTypeOf(t).Length > 0)
            .GroupBy(t => TdTypeOf(t), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        int money = player.Money.Amount;
        int buildings = mine.Count(o => o.IsKindOf(Kind.Structure));
        bool hasIncome = Count(mine, "STRUCT_REFINERY") > 0 && Count(mine, "UNIT_HARVESTER") > 0;
        var choices = new List<(Urgency Urgency, ObjectTemplate Template)>();

        // Several structures may fill one role (Dune 2000's light and heavy factory): the one owned fewest of first.
        bool CanBuild(string type, out ObjectTemplate template)
        {
            template = null!;
            if (!buildable.TryGetValue(type, out var options))
                return false;
            foreach (var t in options.OrderBy(t => mine.Count(o => o.Template == t)))
                if (production.CanQueueUnit(t) is null or "NotEnoughMoney" && player.CanBuild(t))
                {
                    template = t;
                    return true;
                }
            return false;
        }
        bool Affordable(ObjectTemplate t) => t.BuildCost < money || hasIncome;

        // An expansion yard's own refinery first: it was put there for the field.
        if (!mainYard && !mine.Any(o => TdTypeOf(o.Template) == "STRUCT_REFINERY" && MathUtil.Distance2D(o.Position, yard.Position) < TdCell * 10)
            && CanBuild("STRUCT_REFINERY", out var expansionRefinery))
        {
            production.QueueUnit(expansionRefinery);
            return;
        }
        void Choose(Urgency urgency, ObjectTemplate template) => choices.Add((urgency, template));

        int refineries = Count(mine, "STRUCT_REFINERY");
        var energy = player.Energy;
        if (energy.Production + energy.Bonus <= energy.Consumption + PowerSurplus)
        {
            if (CanBuild("STRUCT_ADVANCED_POWER", out var power) && power.BuildCost < money
                || CanBuild("STRUCT_POWER", out power) && power.BuildCost < money)
                Choose(refineries == 0 ? Urgency.Low : Urgency.Medium, power);
        }

        if (refineries < Wanted(RefineryRatio, buildings) && refineries < RefineryLimit
            && CanBuild("STRUCT_REFINERY", out var refinery) && (money > refinery.BuildCost || hasIncome))
            Choose(refineries == 0 ? Urgency.High : Urgency.Medium, refinery);

        int barracks = Count(mine, "STRUCT_BARRACKS", "STRUCT_HAND");
        if (barracks < Wanted(BarracksRatio, buildings) && barracks < BarracksLimit && (money > 300 || hasIncome)
            && (CanBuild("STRUCT_BARRACKS", out var rax) || CanBuild("STRUCT_HAND", out rax)) && Affordable(rax))
            Choose(barracks > 0 ? Urgency.Low : Urgency.Medium, rax);

        int factories = Count(mine, "STRUCT_WEAP");
        if (factories < Wanted(WarRatio, buildings) && factories < WarLimit && (money > 2000 || hasIncome)
            && CanBuild("STRUCT_WEAP", out var weap) && Affordable(weap))
            Choose(factories > 0 ? Urgency.Low : Urgency.Medium, weap);

        // TD's AI only knows the Obelisk and the Nod turret; GDI's towers stand in for them here.
        // Red Alert's (by its AI's preference, strongest first): the Tesla coil, flame tower, turret, pillboxes.
        string[] defenseRoles = ["STRUCT_OBELISK", "STRUCT_TESLA", "STRUCT_TURRET", "STRUCT_FLAME_TURRET", "STRUCT_ATOWER", "STRUCT_GTOWER", "STRUCT_PILLBOX", "STRUCT_CAMOPILLBOX"];
        int defenses = Count(mine, defenseRoles);
        ObjectTemplate defense = null!;
        if (defenses < Wanted(DefenseRatio, buildings) && defenses < DefenseLimit
            && defenseRoles.Any(role => CanBuild(role, out defense)) && Affordable(defense))
            Choose(Urgency.Medium, defense);

        // Anti-aircraft: TD's SAM site; Red Alert's SAM (Soviet) and AA gun (Allies).
        int sams = Count(mine, "STRUCT_SAM", "STRUCT_AAGUN");
        int enemyAircraft = game.Players.Active.Where(IsEnemy).Sum(p => game.ObjectsOwnedBy(p).Count(o => o.IsKindOf(Kind.Aircraft)));
        if (sams < Wanted(AARatio, buildings) && sams < AALimit && enemyAircraft > 0)
        {
            if (Count(mine, "STRUCT_RADAR") == 0 && CanBuild("STRUCT_RADAR", out var radar) && Affordable(radar))
                Choose(Urgency.High, radar);
            if ((CanBuild("STRUCT_SAM", out var sam) || CanBuild("STRUCT_AAGUN", out sam)) && Affordable(sam))
                Choose(sams < enemyAircraft ? Urgency.High : Urgency.Medium, sam);
        }

        int ownAircraft = mine.Count(o => o.IsKindOf(Kind.Aircraft));
        int helipads = Count(mine, "STRUCT_HELIPAD");
        if (helipads < Wanted(HelipadRatio, buildings) && helipads < HelipadLimit && CanBuild("STRUCT_HELIPAD", out var pad) && Affordable(pad))
            Choose(ownAircraft < enemyAircraft ? Urgency.High : Urgency.Medium, pad);

        int airstrips = Count(mine, "STRUCT_AIRSTRIP");
        if (airstrips < Wanted(AirstripRatio, buildings) && airstrips < AirstripLimit && CanBuild("STRUCT_AIRSTRIP", out var strip) && Affordable(strip))
            Choose(ownAircraft < enemyAircraft ? Urgency.High : Urgency.Medium, strip);

        // Red Alert 2's tech (AIBuildThis: Battle Labs, superweapons, ...): one of each, once the base is up and
        // there is money to spare; a radar first, which they all need.
        if (factories > 0 && refineries > 0 && buildable.TryGetValue("STRUCT_TECH", out var techs))
        {
            if (Count(mine, "STRUCT_RADAR") == 0 && CanBuild("STRUCT_RADAR", out var techRadar) && Affordable(techRadar))
                Choose(refineries >= 2 ? Urgency.High : Urgency.Low, techRadar);
            foreach (var t in techs.Where(t => mine.All(o => o.Template != t)).OrderBy(t => t.BuildCost))
                if (production.CanQueueUnit(t) is null && player.CanBuild(t) && (t.BuildCost < money || hasIncome))
                {
                    Choose(Count(mine, "STRUCT_RADAR") > 0 ? Urgency.Medium : Urgency.Low, t);
                    break;
                }
        }

        // An expansion's MCV needs a building first (a radar, a service depot...): that, soon.
        if (mainYard && expansionPrerequisite is { } wanted && mine.All(o => o.Template != wanted)
            && production.CanQueueUnit(wanted) is null or "NotEnoughMoney" && player.CanBuild(wanted))
            Choose(Urgency.High, wanted);

        // The most urgent choice (the first one, on a tie).
        var best = choices.Where(c => c.Urgency != Urgency.None).OrderByDescending(c => c.Urgency).Select(c => c.Template).FirstOrDefault();
        if (expandTrace && game.Frame % (15 * LogicTime.FramesPerSecond) < ThinkFrames)
            Console.WriteLine($"   [yard] {game.Frame / LogicTime.FramesPerSecond}s {player.FactionName} money {money}: " + string.Join(", ", choices.Select(c => $"{c.Template.Name}:{c.Urgency}")) + $" -> {best?.Name}"
                + (expansionPrerequisite is { } ep ? $" [prereq {ep.Name}: queue {production.CanQueueUnit(ep) ?? "ok"}, canbuild {player.CanBuild(ep)}, owned {mine.Count(o => o.Template == ep)}, main {mainYard}]" : ""));
        // A structure it can't pay for yet: the factories wait (but for harvesters) until the money is there, as
        // HouseClass's AI holds back unit production for its base (else cheap units would take every credit).
        var saving = best != null && production.QueueUnit(best) == null && production.CanQueueUnit(best) == "NotEnoughMoney" ? best : null;
        if (mainYard || saving != null)
            savingFor = saving;
    }

    /// <summary>The structure the construction yard is saving up for (null: none).</summary>
    ObjectTemplate? savingFor;

    /// <summary>A finished structure goes next to the base: the nearest legal spot to the base centre (Find_Build_Location).</summary>
    void PlaceReadyStructures(List<GameObject> mine)
    {
        foreach (var factory in mine)
        {
            if (factory.FindModule<ProductionUpdate>() is not { } production || production.ReadyStructure is not { Unit: { } template } entry)
                continue;
            if (FindBuildLocation(template, factory) is { } spot)
                production.PlaceReadyStructure(entry.Id, spot, 0);
        }
    }

    Vector3? FindBuildLocation(ObjectTemplate template, GameObject yard)
    {
        if (!baseCenterSet)
            ComputeBase();
        // Each yard builds round itself: the first base round its centre, an expansion round its own yard.
        var center = MathUtil.Distance2D(yard.Position, baseCenter) > TdCell * 12 ? yard.Position : baseCenter;
        // Refineries go toward Tiberium, as TD's AI places them near the richest field.
        if (TdTypeOf(template) == "STRUCT_REFINERY")
        {
            var field = game.Objects.Where(o => o.IsKindOf(Kind.SupplySource) && MathUtil.Distance2D(o.Position, center) < 600)
                .OrderBy(o => MathUtil.Distance2D(o.Position, center)).FirstOrDefault();
            if (field != null)
                center += Vector3.Normalize((field.Position - center) with { Z = 0 } + new Vector3(0.001f, 0, 0)) * 100;
        }
        // OpenWESTWOOD: defences stand on the base's edge toward the enemy (spread along it), not by the yard.
        bool defense = DefenseRoles.Contains(TdTypeOf(template));
        if (defense && TowardEnemy(center) is { } toward)
        {
            var side = new Vector3(-toward.Y, toward.X, 0);
            center += toward * (BaseReach(center, toward) + TdCell * 2) + side * game.Random.Float(-TdCell * 5, TdCell * 5);
        }
        // OpenWESTWOOD: first a spot that leaves room round the other buildings (a lane for units between them); a
        // packed-in one only if there is none.
        float start = game.Random.Float(0, MathF.Tau);
        for (int pass = 0; pass < 2; pass++)
            for (float r = 0; r <= TdCell * 16; r += TdCell / 2)
            {
                int steps = Math.Max(1, (int)(r * MathF.Tau / (TdCell / 2)));
                for (int i = 0; i < steps; i++)
                {
                    float a = start + i * MathF.Tau / steps;
                    var p = center + new Vector3(DMath.Cos(a), DMath.Sin(a), 0) * r;
                    p.Z = game.Terrain.GroundHeight(p);
                    if ((pass == 1 || defense || HasRoom(template, p)) && game.Building.CheckLocation(template, p, 0, player) == BuildLocation.Ok)
                        return p;
                }
            }
        return null;
    }

    static readonly HashSet<string> DefenseRoles = ["STRUCT_OBELISK", "STRUCT_TESLA", "STRUCT_TURRET", "STRUCT_FLAME_TURRET", "STRUCT_ATOWER",
        "STRUCT_GTOWER", "STRUCT_PILLBOX", "STRUCT_CAMOPILLBOX", "STRUCT_SAM", "STRUCT_AAGUN"];

    /// <summary>No building of its own (walls and defences aside) closer than a cell's gap to this one's footprint.</summary>
    bool HasRoom(ObjectTemplate template, Vector3 p)
    {
        float radius = Math.Max(template.GeometryMajorRadius, template.GeometryMinorRadius);
        foreach (var other in game.ObjectsInRadius(p, radius + TdCell * 4))
        {
            if (other.Owner != player || !other.IsKindOf(Kind.Structure) || other.IsEffectivelyDead || DefenseRoles.Contains(TdTypeOf(other.Template))
                || other.IsKindOf("WALK_ON_TOP_OF_WALL") || TdTypeOf(other.Template).Contains("WALL"))
                continue;
            float otherRadius = Math.Max(other.Template.GeometryMajorRadius, other.Template.GeometryMinorRadius);
            if (MathUtil.Distance2D(other.Position, p) < radius + otherRadius + TdCell)
                return false;
        }
        return true;
    }

    /// <summary>The way to the nearest enemy (its buildings, else its start), level; null if none is known.</summary>
    Vector3? TowardEnemy(Vector3 from)
    {
        Vector3? nearest = null;
        float best = float.MaxValue;
        foreach (var enemy in game.Players.Active.Where(IsEnemy))
        {
            var spots = game.ObjectsOwnedBy(enemy).Where(o => o.IsKindOf(Kind.Structure) && !o.IsEffectivelyDead).Select(o => o.Position).ToList();
            if (spots.Count == 0 && enemy.StartPosition is { } start)
                spots.Add(start);
            foreach (var spot in spots)
            {
                float d = MathUtil.Distance2D(spot, from);
                if (d < best && d > 1)
                {
                    best = d;
                    nearest = spot;
                }
            }
        }
        return nearest is { } n ? Vector3.Normalize((n - from) with { Z = 0 }) : null;
    }

    /// <summary>How far the base reaches from its centre that way (its farthest building's reach along it).</summary>
    float BaseReach(Vector3 center, Vector3 toward)
    {
        float reach = TdCell * 3;
        foreach (var o in game.ObjectsOwnedBy(player))
            if (o.IsKindOf(Kind.Structure) && !o.IsEffectivelyDead && MathUtil.Distance2D(o.Position, center) < TdCell * 14)
                reach = Math.Max(reach, Vector3.Dot((o.Position - center) with { Z = 0 }, toward) + o.Radius);
        return reach;
    }

    int nextMusterFrame;

    /// <summary>
    /// OpenWESTWOOD: the army at home doesn't stand in a clump where it was made: every few seconds the idle units in
    /// the base form up, spread out (a ring formation, a little over a cell apart), on the base's edge toward the
    /// enemy, between it and the base. (Harvesters, MCVs and units out in the field are left alone.)
    /// </summary>
    void Muster(List<GameObject> mine)
    {
        if (game.Frame < nextMusterFrame || !baseCenterSet)
            return;
        nextMusterFrame = game.Frame + 8 * LogicTime.FramesPerSecond;
        if (TowardEnemy(baseCenter) is not { } toward)
            return;
        float reach = BaseReach(baseCenter, toward);
        var point = baseCenter + toward * (reach + TdCell * 4);
        var army = mine.Where(o => o.IsMobile && !o.IsKindOf(Kind.Aircraft) && o.AI is { IsIdle: true } && !o.IsContained && !o.IsEffectivelyDead
                && o.Template.Rules.TryGet("WeaponSet", out _) && TdTypeOf(o.Template) is not ("UNIT_HARVESTER" or "UNIT_MCV")
                && !travelling.ContainsKey(o.Id) && MathUtil.Distance2D(o.Position, baseCenter) < reach + TdCell * 10)
            .OrderBy(o => o.IsKindOf(Kind.Infantry) ? 1 : 0).ThenBy(o => o.Id).ToList();
        // Each unit takes the nearest free spot (so those already in place stay when others come or fall).
        var slots = Enumerable.Range(0, army.Count).Select(i => point + SpreadOffset(i, TdCell * 1.4f)).ToList();
        foreach (var unit in army)
        {
            int nearest = 0;
            for (int i = 1; i < slots.Count; i++)
                if (MathUtil.Distance2D(unit.Position, slots[i]) < MathUtil.Distance2D(unit.Position, slots[nearest]))
                    nearest = i;
            var slot = slots[nearest];
            slots.RemoveAt(nearest);
            if (MathUtil.Distance2D(unit.Position, slot) < TdCell)
                continue;
            slot.Z = game.Terrain.GroundHeight(slot);
            if (game.Pathfinder.NearestPassable(slot, unit.AI!.Surfaces, 3) is { } spot)
                unit.AI!.MoveTo(spot, Commands.CommandSource.AI);
        }
    }

    // ---- Units ----

    /// <summary>AI_Unit / AI_Infantry / AI_Aircraft: each idle factory gets something to make.</summary>
    /// <summary>
    /// Building upgrades (Dune 2000's; the d2k AI wants each once): researched at an idle building when there's
    /// money to spare for it and a reserve.
    /// </summary>
    void ResearchUpgrades(List<GameObject> mine)
    {
        foreach (var building in mine)
        {
            if (building.IsUnderConstruction || building.FindModule<ProductionUpdate>() is not { } production || production.Queue.Count > 0)
                continue;
            foreach (var (_, button) in CommandSets.Buttons(game.Catalog, building.CommandSetName))
            {
                if (button.CommandType != "PLAYER_UPGRADE" || button.Upgrade.Length == 0 || player.HasUpgrade(button.Upgrade)
                    || mine.Any(o => o.FindModule<ProductionUpdate>()?.Queue.Any(e => e.Upgrade == button.Upgrade) == true))
                    continue;
                int cost = RuleValue.Int(game.Catalog.Upgrade(button.Upgrade)?.GetString("BuildCost") ?? "0");
                if (player.Money.Amount >= cost + 1500)
                    production.QueueUpgrade(button.Upgrade);
                break;
            }
        }
    }

    void BuildUnits(List<GameObject> mine)
    {
        int harvesters = Count(mine, "UNIT_HARVESTER");
        int refineries = Count(mine, "STRUCT_REFINERY");
        // Infantry is cheap and a barracks never waits, so it would spend everything: once foot soldiers clearly
        // outnumber vehicles, the barracks leaves the money to a war factory (TD and RA's AIs balance their teams).
        bool HasArms(GameObject o) => o.Template.Rules.TryGet("WeaponSet", out _);
        int infantry = mine.Count(o => o.IsKindOf(Kind.Infantry) && HasArms(o) && !o.IsEffectivelyDead);
        int vehicles = mine.Count(o => o.IsKindOf(Kind.Vehicle) && HasArms(o) && !o.IsEffectivelyDead && TdTypeOf(o.Template) is not ("UNIT_HARVESTER" or "UNIT_MCV"));
        bool hasWarFactory = mine.Any(o => !o.IsUnderConstruction && o.IsKindOf("FS_WARFACTORY"));
        bool saveForVehicles = hasWarFactory && infantry > vehicles * 2 + 4;
        // A harvester a refinery, and a second for each while money is short (OpenWESTWOOD): these are bought even while
        // the money is being saved for a building or an expansion.
        int wantedHarvesters = player.Money.Amount < 3000 ? refineries * 2 : refineries;
        foreach (var factory in mine)
        {
            if (factory.IsUnderConstruction || factory.FindModule<ProductionUpdate>() is not { PlacesStructures: false } production || production.Queue.Count > 0)
                continue;
            var options = CommandSets.BuildableObjects(game.Catalog, factory.CommandSetName, "UNIT_BUILD")
                .Select(n => game.Templates.Find(n)).OfType<ObjectTemplate>()
                .Where(t => production.CanQueueUnit(t) == null && player.CanBuild(t)).ToList();
            if (options.Count == 0)
                continue;
            if (saveForVehicles && factory.IsKindOf("FS_BARRACKS") && !factory.IsKindOf("FS_WARFACTORY"))
                continue;
            if (savingFor != null && player.Money.Amount < savingFor.BuildCost && (wantedHarvesters <= harvesters || options.All(t => TdTypeOf(t) != "UNIT_HARVESTER")))
                continue;
            // Saving for an expansion's MCV, or broke: harvesters only.
            if ((SavingForExpansion || IsBroke(mine)) && (wantedHarvesters <= harvesters || options.All(t => TdTypeOf(t) != "UNIT_HARVESTER")))
                continue;

            // A harvester for each refinery first (OpenWESTWOOD: a second for each while money is short).
            if (wantedHarvesters > harvesters && options.FirstOrDefault(t => TdTypeOf(t) == "UNIT_HARVESTER") is { } harvester)
            {
                production.QueueUnit(harvester);
                harvesters++;
                continue;
            }

            // (An easy computer keeps a small army.)
            if (infantry + vehicles >= ArmyLimit)
                continue;
            // Then a weighted random pick: armed units 20, others 1 (never a harvester or an MCV).
            var weighted = options.Where(t => TdTypeOf(t) is not ("UNIT_HARVESTER" or "UNIT_MCV"))
                .Select(t => (Template: t, Weight: t.Rules.TryGet("WeaponSet", out _) ? 20 : 1)).ToList();
            int total = weighted.Sum(w => w.Weight);
            if (total == 0)
                continue;
            int choice = game.Random.Int(0, total - 1);
            foreach (var (template, weight) in weighted)
            {
                if (choice < weight)
                {
                    production.QueueUnit(template);
                    break;
                }
                choice -= weight;
            }
        }
    }

    // ---- Superweapons (HouseClass::Special_Weapon_AI) ----

    /// <summary>
    /// A ready power is used: a targeted one (Ion Cannon, nuke, Death Hand) on the enemy structure with the
    /// most other enemy structures round it (TD aims at the enemy's best building); a summoning one at once.
    /// </summary>
    void UsePowers(List<GameObject> mine)
    {
        foreach (var obj in mine)
        {
            if (obj.IsUnderConstruction)
                continue;
            foreach (var power in obj.FindModules<Modules.SpecialPowers.OCLSpecialPower>())
            {
                if (!power.IsReady || power.Power is not { } template)
                    continue;
                if (template.RadiusCursorRadius <= 0)
                {
                    power.DoSpecialPower(0);
                    continue;
                }
                var enemyStructures = game.Players.Active.Where(IsEnemy).SelectMany(p => game.ObjectsOwnedBy(p))
                    .Where(o => !o.IsEffectivelyDead && o.IsKindOf(Kind.Structure)).ToList();
                var best = enemyStructures.OrderByDescending(o => enemyStructures.Count(n => MathUtil.Distance2D(n.Position, o.Position) < template.RadiusCursorRadius * 1.5f)).FirstOrDefault();
                if (best != null)
                    power.DoSpecialPowerAtLocation(best.Position, float.NaN, 0);
            }
            // Red Alert's: the GPS at once; the Iron Curtain on its strongest vehicle when an enemy is near it.
            // (The Chronosphere is left to players.)
            foreach (var weapon in obj.FindModules<Modules.Behaviors.RaSuperweapon>())
            {
                if (!weapon.IsReady)
                    continue;
                if (weapon.Mode == "GPS")
                    weapon.DoSpecialPower(0);
                else if (weapon.Mode == "IronCurtain"
                    && mine.Where(o => !o.IsEffectivelyDead && o.IsMobile && o.IsKindOf(Kind.Vehicle) && o.AI is { IsInAttackState: true })
                        .OrderByDescending(o => o.MaxHealth).FirstOrDefault() is { } tank)
                    weapon.DoSpecialPowerAtObject(tank, 0);
            }
            // Red Alert 2's: storms and the Dominator on the enemy's densest base spot, the Mutator on its densest
            // infantry, the Iron Curtain on its own attacking tanks, the Force Shield over its base when hit.
            // (The Chronosphere is left to players.)
            foreach (var weapon in obj.FindModules<Modules.Behaviors.Ra2Superweapon>())
            {
                if (!weapon.IsReady)
                    continue;
                List<GameObject> Enemy(Func<GameObject, bool> which) => game.Players.Active.Where(IsEnemy).SelectMany(p => game.ObjectsOwnedBy(p))
                    .Where(o => !o.IsEffectivelyDead && which(o)).ToList();
                GameObject? Densest(List<GameObject> targets, float radius) =>
                    targets.OrderByDescending(o => targets.Count(n => MathUtil.Distance2D(n.Position, o.Position) < radius)).FirstOrDefault();
                switch (weapon.Mode)
                {
                    case "LightningStorm" or "PsychicDominator":
                        if (Densest(Enemy(o => o.IsKindOf(Kind.Structure) || o.IsMobile), TdScale.Cell * 4) is { } spot)
                            weapon.DoSpecialPowerAtLocation(spot.Position, float.NaN, 0);
                        break;
                    case "GeneticConverter":
                        var infantry = Enemy(o => o.IsKindOf(Kind.Infantry));
                        if (infantry.Count >= 4 && Densest(infantry, TdScale.Cell * 2.5f) is { } crowd)
                            weapon.DoSpecialPowerAtLocation(crowd.Position, float.NaN, 0);
                        break;
                    case "IronCurtain":
                        if (mine.Where(o => !o.IsEffectivelyDead && o.IsMobile && o.IsKindOf(Kind.Vehicle) && o.AI is { IsInAttackState: true })
                                .OrderByDescending(o => o.MaxHealth).FirstOrDefault() is { } tank)
                            weapon.DoSpecialPowerAtLocation(tank.Position, float.NaN, 0);
                        break;
                    case "ForceShield":
                        if (mine.Where(o => o.IsKindOf(Kind.Structure) && !o.IsEffectivelyDead && o.Body is { } body && game.Frame - body.LastDamageFrame < LogicTime.FramesPerSecond * 2)
                                .FirstOrDefault() is { } hit)
                            weapon.DoSpecialPowerAtLocation(hit.Position, float.NaN, 0);
                        break;
                    // Tiberian Sun's: the EMP on the enemy's densest group of vehicles near its base, the Hunter-Seeker at once,
                    // the Firestorm when its base is under fire.
                    case "EMPulse":
                        var vehicles = Enemy(o => o.IsKindOf(Kind.Vehicle) && o.IsMobile && !o.IsKindOf(Kind.Aircraft));
                        if (vehicles.Count >= 3 && Densest(vehicles, TdScale.Cell * 5) is { } group)
                            weapon.DoSpecialPowerAtLocation(group.Position, float.NaN, 0);
                        break;
                    case "HunterSeeker":
                        weapon.DoSpecialPower(0);
                        break;
                    case "Firestorm":
                        if (mine.Any(o => o.IsKindOf(Kind.Structure) && !o.IsEffectivelyDead && o.Body is { } burnt && game.Frame - burnt.LastDamageFrame < LogicTime.FramesPerSecond * 2))
                            weapon.DoSpecialPower(0);
                        break;
                }
            }
        }
    }

    // ---- Attacks ----

    bool IsEnemy(Player other) => other != player && !other.IsNeutral && player.RelationshipTo(other) == Relationship.Enemies;

    void Attack(List<GameObject> mine)
    {
        if (game.Frame < nextAttackFrame)
            return;
        nextAttackFrame = game.Frame + (int)(AttackIntervalSeconds * LogicTime.FramesPerSecond);

        var army = mine.Where(o => o.IsMobile && o.AI is { IsIdle: true } && o.Template.Rules.TryGet("WeaponSet", out _)
            && TdTypeOf(o.Template) is not ("UNIT_HARVESTER" or "UNIT_MCV") && !travelling.ContainsKey(o.Id)).ToList();
        // OpenWESTWOOD: waves grow (8 units, then 3 more each attack, up to 40), and a home guard stays behind once
        // the army is big. Not enough yet: look again in 20 seconds rather than a whole interval.
        int wave = Math.Min(MinimumWave + attackWaves * WaveGrowth, 40);
        int guard = army.Count >= 12 ? Math.Max(3, army.Count / 4) : 0;
        if (army.Count - guard < wave)
        {
            nextAttackFrame = game.Frame + 20 * LogicTime.FramesPerSecond;
            return;
        }
        // The guard: the units nearest the base stay.
        var home = baseCenterSet ? baseCenter : army[0].Position;
        army = army.OrderBy(o => MathUtil.Distance2D(o.Position, home)).Skip(guard).ToList();
        attackWaves++;
        var from = baseCenterSet ? baseCenter : army[0].Position;
        var target = game.Players.Active.Where(IsEnemy).SelectMany(p => game.ObjectsOwnedBy(p))
            .Where(o => !o.IsEffectivelyDead && (o.IsKindOf(Kind.Structure) || o.IsMobile))
            .OrderBy(o => o.IsKindOf(Kind.Structure) ? 0 : 1).ThenBy(o => MathUtil.Distance2D(o.Position, from)).FirstOrDefault();
        if (target == null)
            return;
        // Each unit its own spot round the target (rings of six, twelve, ...), so the army arrives spread out
        // rather than piled onto one point (TD's teams move in formation). The orders go out one a frame: each
        // finds its own path, and a whole army doing so at once stalls the game.
        for (int i = 0; i < army.Count; i++)
            pendingOrders.Enqueue((army[i].Id, target.Position + SpreadOffset(i, Math.Max(18, army[i].Radius * 2.2f))));
    }

    readonly Queue<(uint Unit, Vector3 To)> pendingOrders = new();

    int attackWaves;

    /// <summary>The smallest attack (8; the shell battle's houses attack with 4) and how much each next one grows.</summary>
    public int MinimumWave { get; set; } = 8;
    /// <summary>The most armed units it keeps (an easy computer's is small; no limit otherwise).</summary>
    public int ArmyLimit { get; set; } = int.MaxValue;
    public int WaveGrowth { get; set; } = 3;

    // ---- Out of money (OpenWESTWOOD) ----

    int nextSaleFrame;

    /// <summary>
    /// A refinery but no harvester and not the money for one: the AI would sit there for good. It sells a building it
    /// can spare (a defence, then a spare power plant or barracks) every few seconds until it can buy a harvester,
    /// as a player would.
    /// </summary>
    void RecoverEconomy(List<GameObject> mine)
    {
        if (game.Frame < nextSaleFrame || Count(mine, "STRUCT_REFINERY") == 0 || Count(mine, "UNIT_HARVESTER") > 0)
            return;
        var harvester = mine.Where(o => !o.IsUnderConstruction && o.FindModule<ProductionUpdate>() is { PlacesStructures: false })
            .SelectMany(f => CommandSets.BuildableObjects(game.Catalog, f.CommandSetName, "UNIT_BUILD"))
            .Select(n => game.Templates.Find(n)).OfType<ObjectTemplate>().FirstOrDefault(t => TdTypeOf(t) == "UNIT_HARVESTER");
        if (harvester == null || player.Money.Amount >= harvester.BuildCost)
            return;
        nextSaleFrame = game.Frame + 3 * LogicTime.FramesPerSecond;
        // What it can spare, first to last: defences and extras, the radar, then all but one power plant and barracks.
        string[] spare = ["STRUCT_OBELISK", "STRUCT_TESLA", "STRUCT_TURRET", "STRUCT_FLAME_TURRET", "STRUCT_ATOWER", "STRUCT_GTOWER",
            "STRUCT_PILLBOX", "STRUCT_CAMOPILLBOX", "STRUCT_SAM", "STRUCT_AAGUN", "STRUCT_HELIPAD", "STRUCT_AIRSTRIP", "STRUCT_TECH", "STRUCT_RA", "STRUCT_RADAR"];
        bool Sellable(GameObject o) => o.IsKindOf(Kind.Structure) && !o.IsUnderConstruction && !o.IsUnsellable;
        var sale = mine.Where(o => Sellable(o) && Array.IndexOf(spare, TdTypeOf(o.Template)) >= 0).OrderByDescending(o => o.Template.BuildCost).FirstOrDefault();
        foreach (string[] kept in new[] { new[] { "STRUCT_POWER", "STRUCT_ADVANCED_POWER" }, ["STRUCT_BARRACKS", "STRUCT_HAND"], ["STRUCT_REFINERY"] })
            if (sale == null && Count(mine, kept) > 1)
                sale = mine.Where(o => Sellable(o) && Array.IndexOf(kept, TdTypeOf(o.Template)) >= 0).OrderBy(o => o.Template.BuildCost).FirstOrDefault();
        if (sale != null)
            game.Building.Sell(sale);
    }

    // ---- Expansion (OpenWESTWOOD) ----

    /// <summary>MCVs on their way to an expansion: where to, and since when.</summary>
    readonly Dictionary<uint, (Vector3 To, int Since)> travelling = new();

    /// <summary>
    /// Once the first base runs (two refineries, a war factory) and money is to spare, the AI builds another MCV and
    /// sends it to the nearest resource field nobody has built at, away from the enemy, where it unfolds and builds a
    /// refinery and the rest (up to two expansions). Expansion yards build round themselves (see FindBuildLocation).
    /// </summary>
    static readonly bool expandTrace = Environment.GetEnvironmentVariable("EXPAND_TRACE") == "1";
    int nextExpandTraceFrame;

    void ExpandTrace(string why)
    {
        if (!expandTrace || game.Frame < nextExpandTraceFrame)
            return;
        nextExpandTraceFrame = game.Frame + 30 * LogicTime.FramesPerSecond;
        Console.WriteLine($"   [expand] {game.Frame / LogicTime.FramesPerSecond}s {player.FactionName}: {why} (money {player.Money.Amount}, prerequisite {expansionPrerequisite?.Name}, saving {SavingForExpansion})");
    }

    void Expand(List<GameObject> mine)
    {
        if (game.Frame < 6 * 60 * LogicTime.FramesPerSecond)
            return;
        int yards = mine.Count(o => o.IsKindOf("COMMANDCENTER"));
        var mcvs = mine.Where(o => TdTypeOf(o.Template) == "UNIT_MCV").ToList();
        // A new MCV (not yet sent): pick its field.
        foreach (var mcv in mcvs)
        {
            if (travelling.ContainsKey(mcv.Id) || mcv.AI is not { } ai || yards == 0)
                continue;
            if (ExpansionSite(mine) is { } site)
            {
                travelling[mcv.Id] = (site, game.Frame);
                ai.MoveTo(site, Commands.CommandSource.AI);
            }
        }
        if (yards == 0 || yards >= 1 + MaxExpansions || mcvs.Count > 0)
        {
            expansionSavingSince = -1;
            return;
        }
        // Gave up saving a while ago: try again three minutes later.
        if (expansionSavingSince >= 0 && !SavingForExpansion)
        {
            if (game.Frame - expansionSavingSince < 480 * LogicTime.FramesPerSecond)
                return;
            expansionSavingSince = -1;
        }
        if (Count(mine, "STRUCT_REFINERY") < 2 || !mine.Any(o => !o.IsUnderConstruction && o.IsKindOf("FS_WARFACTORY")))
        {
            ExpandTrace("needs two refineries and a war factory");
            return;
        }
        if (mine.Any(o => o.FindModule<ProductionUpdate>()?.Queue.Any(e => e.Unit is { } u && TdTypeOf(u) == "UNIT_MCV") == true))
            return;
        if (ExpansionSite(mine) == null)
        {
            ExpandTrace("no free resource field");
            return;
        }
        ExpandTrace("wants to expand");
        foreach (var factory in mine.Where(o => !o.IsUnderConstruction && o.FindModule<ProductionUpdate>() is { PlacesStructures: false }))
        {
            var production = factory.FindModule<ProductionUpdate>()!;
            var mcvTemplate = CommandSets.BuildableObjects(game.Catalog, factory.CommandSetName, "UNIT_BUILD")
                .Select(n => game.Templates.Find(n)).OfType<ObjectTemplate>()
                .FirstOrDefault(t => TdTypeOf(t) == "UNIT_MCV" && production.CanQueueUnit(t) == null && player.CanBuild(t));
            if (mcvTemplate == null)
            {
                // The factory lists an MCV it can't build yet: find the building it still needs.
                // (Worked out afresh each time: once one is built, the next one it needs.)
                expansionPrerequisite = null;
                if (CommandSets.BuildableObjects(game.Catalog, factory.CommandSetName, "UNIT_BUILD")
                        .Select(n => game.Templates.Find(n)).OfType<ObjectTemplate>().FirstOrDefault(t => TdTypeOf(t) == "UNIT_MCV") is { } locked
                    && mine.FirstOrDefault(o => !o.IsUnderConstruction && o.FindModule<ProductionUpdate>() is { PlacesStructures: true }) is { } yard)
                    expansionPrerequisite = NextPrerequisite(locked, mine, yard, 0);
                continue;
            }
            expansionPrerequisite = null;
            if (player.Money.Amount >= mcvTemplate.BuildCost)
            {
                production.QueueUnit(mcvTemplate);
                expansionSavingSince = -1;
                return;
            }
            // Not enough yet: save for it (for up to three minutes, then let the base and army have the money again).
            if (expansionSavingSince < 0)
                expansionSavingSince = game.Frame;
            return;
        }
    }

    /// <summary>A building the expansion's MCV still needs, for the construction yard to make (null: none).</summary>
    ObjectTemplate? expansionPrerequisite;

    /// <summary>
    /// The first building on the way to <paramref name="want"/> that the yard can make now: a missing prerequisite, or
    /// that building's own missing prerequisite (up to three deep). Null if none is missing or one is being built.
    /// </summary>
    ObjectTemplate? NextPrerequisite(ObjectTemplate want, List<GameObject> mine, GameObject yard, int depth)
    {
        if (depth > 3 || !want.Rules.TryGet("Prerequisites", out var prerequisites) || !prerequisites.IsMapping
            || !prerequisites.TryGet("Object", out var lines))
            return null;
        var yardBuilds = CommandSets.BuildableObjects(game.Catalog, yard.CommandSetName, "UNIT_BUILD").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.AsList())
        {
            var names = RuleValue.Words(line.Value).ToList();
            var have = mine.Where(o => names.Contains(o.Template.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (have.Count > 0)
            {
                if (have.Any(o => !o.IsUnderConstruction))
                    continue;
                return null; // on its way
            }
            foreach (string name in names)
                if (yardBuilds.Contains(name) && game.Templates.Find(name) is { } template)
                    return player.CanBuild(template) ? template : NextPrerequisite(template, mine, yard, depth + 1);
        }
        return null;
    }

    /// <summary>The frame it began saving for an expansion's MCV (-1: not saving).</summary>
    int expansionSavingSince = -1;

    bool SavingForExpansion => expansionSavingSince >= 0 && game.Frame - expansionSavingSince < 300 * LogicTime.FramesPerSecond;

    /// <summary>Refineries but no harvester: every credit goes to a harvester (see RecoverEconomy).</summary>
    static bool IsBroke(List<GameObject> mine) => Count(mine, "STRUCT_REFINERY") > 0 && Count(mine, "UNIT_HARVESTER") == 0;

    /// <summary>The nearest resource field to the base with no structure near it and no enemy close by.</summary>
    Vector3? ExpansionSite(List<GameObject> mine)
    {
        var from = baseCenterSet ? baseCenter : mine.FirstOrDefault()?.Position ?? Vector3.Zero;
        var structures = game.Objects.Where(o => !o.IsEffectivelyDead && o.IsKindOf(Kind.Structure) && !o.Owner.IsNeutral).ToList();
        var enemyStuff = game.Players.Active.Where(IsEnemy).SelectMany(p => game.ObjectsOwnedBy(p)).Where(o => !o.IsEffectivelyDead).ToList();
        var taken = travelling.Values.Select(t => t.To).ToList();
        foreach (var field in game.Objects.Where(o => o.IsKindOf(Kind.SupplySource) && !o.IsEffectivelyDead)
                     .OrderBy(o => MathUtil.Distance2D(o.Position, from)))
        {
            var p = field.Position;
            if (MathUtil.Distance2D(p, from) < TdCell * 14)
                continue; // the home field
            if (structures.Any(o => MathUtil.Distance2D(o.Position, p) < TdCell * 10))
                continue;
            if (enemyStuff.Any(o => MathUtil.Distance2D(o.Position, p) < TdCell * 14))
                continue;
            if (taken.Any(t => MathUtil.Distance2D(t, p) < TdCell * 10))
                continue;
            // Beside the field, not on it (the yard would block the harvesters).
            var toward = Vector3.Normalize((from - p) with { Z = 0 } + new Vector3(0.001f, 0, 0));
            var spot = p + toward * TdCell * 4;
            spot.Z = game.Terrain.GroundHeight(spot);
            return spot;
        }
        return null;
    }

    void IssuePendingOrder()
    {
        while (pendingOrders.Count > 0)
        {
            var (id, to) = pendingOrders.Dequeue();
            if (game.FindObject(id) is { IsEffectivelyDead: false, AI: { } ai })
            {
                ai.AttackMoveTo(to, Commands.CommandSource.AI);
                return;
            }
        }
    }

    /// <summary>The i-th spot of a spread: the middle, then rings of 6, 12, 18 ... spots, spacing apart.</summary>
    static Vector3 SpreadOffset(int i, float spacing)
    {
        if (i == 0)
            return Vector3.Zero;
        int ring = 1, first = 1;
        while (i >= first + 6 * ring)
        {
            first += 6 * ring;
            ring++;
        }
        float angle = (i - first) / (6f * ring) * MathF.Tau + ring * 0.5f;
        return new Vector3(DMath.Cos(angle), DMath.Sin(angle), 0) * spacing * ring;
    }

    /// <summary>
    /// Idle units standing on top of each other (left there by a fight, or by a factory's rally point) step apart, so
    /// ten units never look like one.
    /// </summary>
    void SpreadOut(List<GameObject> mine)
    {
        var idle = mine.Where(o => o.IsMobile && !o.IsKindOf(Kind.Aircraft) && o.AI is { IsIdle: true } && !o.IsEffectivelyDead && !o.IsContained).ToList();
        for (int i = 0; i < idle.Count; i++)
            for (int j = i + 1; j < idle.Count; j++)
            {
                var a = idle[i];
                var b = idle[j];
                float apart = MathUtil.Distance2D(a.Position, b.Position), want = (a.Radius + b.Radius) * 0.8f;
                if (apart >= want)
                    continue;
                // The later one steps away (a random way if they are on the very same spot).
                var away = (b.Position - a.Position) with { Z = 0 };
                if (away.LengthSquared() < 0.01f)
                    away = new Vector3(DMath.Cos(j * 2.4f), DMath.Sin(j * 2.4f), 0);
                var to = b.Position + Vector3.Normalize(away) * (want - apart + Math.Max(8, b.Radius));
                if (game.Pathfinder.NearestPassable(to, b.AI!.Surfaces, 2) is { } spot)
                    b.AI.MoveTo(spot, Commands.CommandSource.AI);
                break;
            }
    }
}
