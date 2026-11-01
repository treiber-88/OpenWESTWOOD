using System.Numerics;
using OpenGZH.Core.Rules;
using OpenGZH.Logic.Construction;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Modules.Behaviors;
using OpenGZH.Logic.Modules.Production;
using OpenGZH.Logic.Objects;
using OpenGZH.Logic.Players;
using OpenGZH.Logic.Templates;

namespace OpenGZH.Logic.AI;

/// <summary>
/// OpenGZH addition: the computer player for a Tiberian Dawn faction in a Zero Hour engine match.
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
    const int RefineryRatio = 0x28, BarracksRatio = 0x28, WarRatio = 0x19, DefenseRatio = 0x66, AARatio = 0x24, HelipadRatio = 0x1E, AirstripRatio = 0x1E;
    const int RefineryLimit = 4, BarracksLimit = 2, WarLimit = 2, DefenseLimit = 40, AALimit = 10, HelipadLimit = 5, AirstripLimit = 5;
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

    public override void Update()
    {
        IssuePendingOrder();
        if (game.Frame < nextThinkFrame)
            return;
        nextThinkFrame = game.Frame + ThinkFrames;

        var mine = game.ObjectsOwnedBy(player).Where(o => !o.IsEffectivelyDead).ToList();
        DeployMcv(mine);
        PlaceReadyStructures(mine);
        BuildStructure(mine);
        ResearchUpgrades(mine);
        BuildUnits(mine);
        UsePowers(mine);
        Attack(mine);
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
        var yard = mine.FirstOrDefault(o => o.FindModule<ProductionUpdate>() is { PlacesStructures: true });
        if (yard?.FindModule<ProductionUpdate>() is not { } production || production.Queue.Count > 0)
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

        // The most urgent choice (the first one, on a tie).
        var best = choices.Where(c => c.Urgency != Urgency.None).OrderByDescending(c => c.Urgency).Select(c => c.Template).FirstOrDefault();
        // A structure it can't pay for yet: the factories wait (but for harvesters) until the money is there, as
        // HouseClass's AI holds back unit production for its base (else cheap units would take every credit).
        savingFor = best != null && production.QueueUnit(best) == null && production.CanQueueUnit(best) == "NotEnoughMoney" ? best : null;
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
            if (FindBuildLocation(template) is { } spot)
                production.PlaceReadyStructure(entry.Id, spot, 0);
        }
    }

    Vector3? FindBuildLocation(ObjectTemplate template)
    {
        if (!baseCenterSet)
            ComputeBase();
        var center = baseCenter;
        // Refineries go toward Tiberium, as TD's AI places them near the richest field.
        if (TdTypeOf(template) == "STRUCT_REFINERY")
        {
            var field = game.Objects.Where(o => o.IsKindOf(Kind.SupplySource) && MathUtil.Distance2D(o.Position, center) < 600)
                .OrderBy(o => MathUtil.Distance2D(o.Position, center)).FirstOrDefault();
            if (field != null)
                center += Vector3.Normalize((field.Position - center) with { Z = 0 } + new Vector3(0.001f, 0, 0)) * 100;
        }
        float start = game.Random.Float(0, MathF.Tau);
        for (float r = 0; r <= TdCell * 16; r += TdCell / 2)
        {
            int steps = Math.Max(1, (int)(r * MathF.Tau / (TdCell / 2)));
            for (int i = 0; i < steps; i++)
            {
                float a = start + i * MathF.Tau / steps;
                var p = center + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * r;
                p.Z = game.Terrain.GroundHeight(p);
                if (game.Building.CheckLocation(template, p, 0, player) == BuildLocation.Ok)
                    return p;
            }
        }
        return null;
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
            if (savingFor != null && player.Money.Amount < savingFor.BuildCost && (refineries <= harvesters || options.All(t => TdTypeOf(t) != "UNIT_HARVESTER")))
                continue;

            // A harvester for each refinery first.
            if (refineries > harvesters && options.FirstOrDefault(t => TdTypeOf(t) == "UNIT_HARVESTER") is { } harvester)
            {
                production.QueueUnit(harvester);
                harvesters++;
                continue;
            }

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
            && TdTypeOf(o.Template) is not ("UNIT_HARVESTER" or "UNIT_MCV")).ToList();
        if (army.Count < 4)
            return;
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
        return new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0) * spacing * ring;
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
                    away = new Vector3(MathF.Cos(j * 2.4f), MathF.Sin(j * 2.4f), 0);
                var to = b.Position + Vector3.Normalize(away) * (want - apart + Math.Max(8, b.Radius));
                if (game.Pathfinder.NearestPassable(to, b.AI!.Surfaces, 2) is { } spot)
                    b.AI.MoveTo(spot, Commands.CommandSource.AI);
                break;
            }
    }
}
