using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.AI;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.Logic.Modules.Production;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Tiberian Dawn's GDI and Nod as factions in Zero Hour engine matches (mods/zh/rules/td).</summary>
public class TdFactionMatchTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic NewMatch(string alice, string bob, bool aliceHuman = true, string map = "Maps/Tournament Desert/Tournament Desert.map")
    {
        var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes(map));
        return GameSetup.FromMap(mapFile, content.Rules!, seed: 1234,
        [
            new PlayerSlot("Alice", alice, IsHuman: aliceHuman, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", bob, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    [Fact]
    public void LobbyOffersTdFactionsAndStartsTheMatch()
    {
        if (!content.Available) return;
        var templates = Logic.Multiplayer.GameInfo.PlayerTemplates(content.Rules!);
        int nod = templates.First(t => t.Name == "FactionTDNod").Index;
        int gdi = templates.First(t => t.Name == "FactionTDGDI").Index;
        Assert.True(Logic.Multiplayer.GameInfo.IsPlayableTemplate(templates[nod].Rules));

        var cache = Logic.Multiplayer.MapCache.Load(content.FileSystem!);
        var info = new Logic.Multiplayer.GameInfo { Map = "Maps/Tournament Desert/Tournament Desert.map", Seed = 7, StartingCash = 10000 };
        info.Slots[0].SetState(Logic.Multiplayer.SlotState.Player, "Alice");
        info.Slots[0].PlayerTemplate = nod;
        info.Slots[1].SetState(Logic.Multiplayer.SlotState.MedAI, "Bob");
        info.Slots[1].PlayerTemplate = gdi;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes(info.Map));
        var game = GameSetup.FromGameInfo(map, content.Rules!, info, cache, content.Models,
            p => content.FileSystem.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        Assert.Contains(game.ObjectsOwnedBy(alice), o => o.Template.Name == "TDNOD_MCV");
        Assert.Contains(game.ObjectsOwnedBy(bob), o => o.Template.Name == "TDGDI_MCV");
        Assert.IsType<TdFactionAI>(bob.AI);
        Assert.Null(alice.AI);
    }

    [Fact]
    public void SidebarListsWhatTheYardCanBuild()
    {
        if (!content.Available) return;
        var game = NewMatch("FactionTDGDI", "FactionChina");
        var alice = game.Players.Find("Alice")!;
        var mcv = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_MCV");
        Assert.True(mcv.FindModule<TdDeployToStructure>()!.Deploy());
        var yard = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_FACT");
        foreach (string name in Logic.Templates.CommandSets.BuildableObjects(game.Catalog, yard.CommandSetName, "UNIT_BUILD"))
        {
            var t = game.Templates.Find(name);
            output.WriteLine($"{name}: found {t != null} canBuild {t != null && alice.CanBuild(t)} prereq {t != null && Prerequisites.AreMet(game, alice, t.Rules)}");
        }
        var panel = new Client.TiberianDawn.TdSidebarPanel(game, alice, null!);
        var structures = panel.Column(0);
        output.WriteLine("structures: " + string.Join(", ", structures.Select(i => i.Template.Name)));
        Assert.Contains(structures, i => i.Template.Name == "TDGDI_NUKE");
    }

    [Fact]
    public void McvDeploysAtEveryStartOfTheSkirmishMaps()
    {
        if (!content.Available) return;
        var cache = Logic.Multiplayer.MapCache.Load(content.FileSystem!);
        var failures = new List<string>();
        foreach (var meta in cache.Maps.Where(m => m.IsMultiplayer && content.FileSystem!.Exists(m.FileName)).OrderBy(m => m.FileName))
        {
            var mapFile = MapFile.Read(content.FileSystem!.ReadAllBytes(meta.FileName));
            for (int start = 1; start <= meta.NumPlayers; start++)
            {
                var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 1,
                [
                    new PlayerSlot("Alice", "FactionTDGDI", IsHuman: true, StartPosition: start, Color: 0x0000FF),
                ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
                var alice = game.Players.Find("Alice")!;
                var mcv = game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.Template.Name == "TDGDI_MCV");
                if (mcv == null)
                {
                    failures.Add($"{meta.FileName} start {start}: no MCV");
                    continue;
                }
                var yard = game.Templates.Find("TDGDI_FACT")!;
                var result = game.Building.CheckLocation(yard, mcv.Position, 0, alice, mcv);
                if (result != Logic.Construction.BuildLocation.Ok)
                {
                    var near = game.ObjectsInRadius(mcv.Position, 160).Where(o => o != mcv && (o.IsKindOf(Kind.Structure) || o.IsKindOf(Kind.Immobile)))
                        .Select(o => $"{o.Template.Name}@{Logic.Core.MathUtil.Distance2D(o.Position, mcv.Position):F0}");
                    failures.Add($"{meta.FileName} start {start}: {result} near {string.Join(" ", near)}");
                }
            }
        }
        foreach (var f in failures)
            output.WriteLine(f);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("FactionTDGDI", "TDGDI_")]
    [InlineData("FactionTDNod", "TDNOD_")]
    public void HumanHarvesterCollectsZeroHourSupplies(string faction, string prefix)
    {
        if (!content.Available) return;
        // A TD computer opponent: it doesn't attack in the first five minutes.
        var game = NewMatch(faction, "FactionTDGDI");
        var alice = game.Players.Find("Alice")!;
        Assert.True(game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == prefix + "MCV").FindModule<TdDeployToStructure>()!.Deploy());
        var yard = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == prefix + "FACT");
        var production = yard.FindModule<ProductionUpdate>()!;
        // A power plant, then a refinery, each placed next to the base as a player would.
        foreach (string name in new[] { "NUKE", "PROC" })
        {
            var template = game.Templates.Find(prefix + name)!;
            var entry = production.QueueUnit(template)!;
            for (int i = 0; i < 120 * LogicTime.FramesPerSecond && production.ReadyStructure == null; i++)
                game.Update();
            Assert.NotNull(production.ReadyStructure);
            Logic.Objects.GameObject? placed = null;
            for (float r = 40; r < 400 && placed == null; r += 18)
                for (int k = 0; k < 16 && placed == null; k++)
                {
                    var p = yard.Position + new System.Numerics.Vector3(MathF.Cos(k * MathF.Tau / 16), MathF.Sin(k * MathF.Tau / 16), 0) * r;
                    placed = production.PlaceReadyStructure(entry.Id, p with { Z = game.Terrain.GroundHeight(p) }, 0);
                }
            Assert.NotNull(placed);
        }
        int before = alice.Money.Amount;
        for (int i = 0; i < 150 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (30 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: " + string.Join(", ", game.ObjectsOwnedBy(alice).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        }
        var harvester = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == prefix + "HARV");
        output.WriteLine($"money {before} -> {alice.Money.Amount}, earned {alice.Score.TotalMoneyEarned}; harvester {harvester.Position} {harvester.AI!.State}");
        foreach (var dock in game.Objects.Where(o => o.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() != null))
            output.WriteLine($"supply {dock.Template.Name} at {dock.Position} distance {Logic.Core.MathUtil.Distance2D(dock.Position, harvester.Position):F0} boxes {dock.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>()!.Boxes}");
        var g = Logic.Modules.Supply.SupplyGatherer.Of(harvester)!;
        output.WriteLine($"gatherer {g.State} scan {g.WarehouseScanDistance} best {g.FindBestSupplyWarehouse()?.Position}");
        Assert.True(alice.Score.TotalMoneyEarned > 0, "the harvester brings in supplies");
    }

    [Fact]
    public void TdFactionsLoadAsZeroHourRules()
    {
        if (!content.Available) return;
        var game = NewMatch("FactionTDGDI", "FactionTDNod");
        foreach (var name in new[] { "TDGDI_MCV", "TDGDI_FACT", "TDGDI_MTNK", "TDNOD_OBLI", "TDNOD_HAND", "TD_Tiberium12" })
            Assert.NotNull(game.Templates.Find(name));
        Assert.Equal("Mammoth Tank", game.Templates.Find("TDGDI_HTNK")!.DisplayName);
    }

    [Fact]
    public void TdFactionStartsWithAnMcvThatDeploys()
    {
        if (!content.Available) return;
        var game = NewMatch("FactionTDGDI", "FactionAmerica");
        var alice = game.Players.Find("Alice")!;
        var mcv = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_MCV");
        Assert.DoesNotContain(game.ObjectsOwnedBy(alice), o => o.IsKindOf(Kind.Structure));
        Assert.True(mcv.FindModule<TdDeployToStructure>()!.Deploy());
        game.Update();
        var yard = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_FACT");
        Assert.False(yard.IsUnderConstruction);

        // Build a power plant at the yard, then place it next to the yard.
        var production = yard.FindModule<ProductionUpdate>()!;
        alice.Money.Deposit(5000);
        var entry = production.QueueUnit(game.Templates.Find("TDGDI_NUKE")!);
        Assert.NotNull(entry);
        for (int i = 0; i < 60 * LogicTime.FramesPerSecond && production.ReadyStructure == null; i++)
            game.Update();
        Assert.NotNull(production.ReadyStructure);
        var far = yard.Position + new System.Numerics.Vector3(300, 0, 0);
        Assert.Null(production.PlaceReadyStructure(entry!.Id, far, 0)); // TD: must touch the base
        var plantTemplate = game.Templates.Find("TDGDI_NUKE")!;
        var near = yard.Position + new System.Numerics.Vector3(yard.Geometry.MajorRadius + plantTemplate.GeometryMajorRadius + 4, 0, 0);
        var plant = production.PlaceReadyStructure(entry.Id, near, 0);
        Assert.NotNull(plant);
        Assert.Empty(production.Queue);
    }

    [Theory]
    [InlineData("FactionTDGDI", "FactionChina")]
    [InlineData("FactionTDNod", "FactionTDGDI")]
    public void TdComputerPlayerBuildsABaseAndAnArmy(string bobFaction, string aliceFaction)
    {
        if (!content.Available) return;
        var game = NewMatch(aliceFaction, bobFaction);
        var bob = game.Players.Find("Bob")!;
        Assert.IsType<TdFactionAI>(bob.AI);
        bool trace = Environment.GetEnvironmentVariable("OPENWESTWOOD_TD_TRACE") == "1";
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("OPENWESTWOOD_AI_MINUTES"), out int m) ? m : 6;
        for (int i = 0; i < minutes * 60 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (60 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: Bob ${bob.Money.Amount}, "
                    + string.Join(", ", game.ObjectsOwnedBy(bob).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
            if (trace && i % (5 * LogicTime.FramesPerSecond) == 0)
                foreach (var h in game.ObjectsOwnedBy(bob).Where(o => o.AI is Logic.Modules.Supply.SupplyTruckAIUpdate))
                    output.WriteLine($"  {i / LogicTime.FramesPerSecond}s harvester {h.Id} {h.Position} {((Logic.Modules.Supply.SupplyTruckAIUpdate)h.AI!).Gatherer.State} {h.AI!.DockStepName} ${bob.Money.Amount}");
        }
        var mine = game.ObjectsOwnedBy(bob).ToList();
        output.WriteLine("end: " + string.Join(", ", mine.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        if (trace)
            foreach (var st in mine.Where(o => o.IsKindOf(Kind.Structure)))
                output.WriteLine($"  structure {st.Template.Name} at {st.Position} half {st.Geometry.MajorRadius}x{st.Geometry.MinorRadius} angle {st.Orientation}");
        foreach (var truck in mine.Where(o => o.AI is Logic.Modules.Supply.SupplyTruckAIUpdate))
        {
            var g = ((Logic.Modules.Supply.SupplyTruckAIUpdate)truck.AI!).Gatherer;
            var dockWith = game.FindObject(truck.AI!.DockTargetId);
            output.WriteLine($"  docking with {dockWith?.Template.Name} at {dockWith?.Position} r {dockWith?.Geometry.MajorRadius}x{dockWith?.Geometry.MinorRadius} dockpos {dockWith?.FindModule<Logic.Modules.Dock.DockUpdate>()?.GetDockPosition(truck)}");
            output.WriteLine($"harvester {truck.Id} at {truck.Position} state {g.State} step {truck.AI!.DockStepName} boxes {g.Boxes} pref {g.PreferredCenterId} ai {truck.AI!.State}");
        }
        Assert.Contains(mine, o => o.IsKindOf("COMMANDCENTER"));
        Assert.True(mine.Count(o => o.IsKindOf(Kind.Structure)) >= 4, "the TD computer player builds a base");
        Assert.Contains(mine, o => TdFactionAI.TdTypeOf(o.Template) == "STRUCT_REFINERY");
        Assert.Contains(mine, o => TdFactionAI.TdTypeOf(o.Template) == "UNIT_HARVESTER");
        Assert.True(bob.Score.TotalMoneyEarned > 0, "harvesters bring in money");
        Assert.True(mine.Count(o => o.IsMobile && TdFactionAI.TdTypeOf(o.Template) != "UNIT_HARVESTER") >= 3, "and trains units");
    }
}

/// <summary>Walking infantry: the walk cycle's rate follows the soldier's speed (W3DModelDraw::adjustAnimSpeedToMovementSpeed).</summary>
public class InfantryWalkTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void RangerWalkCycleCoversItsDistanceAtWalkingSpeed()
    {
        if (!content.Available) return;
        var anim = content.Models!.GetAnimation("AIRngr_SKL.AIRngr_RNA");
        Assert.NotNull(anim);
        float speedPerFrame = 20f / LogicTime.FramesPerSecond; // BasicHumanLocomotor Speed 20
        float rate = speedPerFrame * anim!.FrameCount / 30f * LogicTime.FramesPerSecond / anim.FrameRate;
        output.WriteLine($"frames {anim.FrameCount} at {anim.FrameRate} fps; plays at x{rate:F2} when walking");
        Assert.InRange(rate, 0.2f, 5f);
    }
}

/// <summary>TD weapons show TD's own muzzle flashes, bullets and explosions.</summary>
public class TdWeaponFxTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void TdShotsRaiseTdAnimationsThatExist()
    {
        if (!content.Available || OpenWestwood.Client.TiberianDawnContent.Art is not { } art) return;
        var td = OpenWestwood.Client.TiberianDawnContent.Get()!;
        var types = new OpenWestwood.TiberianDawn.Logic.Types.TdTypes(td.Rules);
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionTDNod", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionTDGDI", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var names = new List<string>();
        game.Events.OnFx += fx => { if (fx.Name.StartsWith("TDFX:")) names.Add(fx.Name); };
        game.Events.OnWeaponFireFx += fx => { if (fx.Name.StartsWith("TDFX:")) names.Add(fx.Name); };
        var middle = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        foreach (var (unit, owner, dx) in new[] { ("TDNOD_LTNK", "Alice", -60f), ("TDNOD_E1", "Alice", -50f), ("TDGDI_MTNK", "Bob", 60f), ("TDGDI_E3", "Bob", 50f) })
        {
            var p = middle + new System.Numerics.Vector3(dx, dx > 0 ? 20 : -20, 0);
            game.CreateObject(unit, game.Players.Find(owner)!, p with { Z = game.Terrain.GroundHeight(p) });
        }
        for (int i = 0; i < 20 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine(string.Join(", ", names.Distinct()));
        Assert.NotEmpty(names);
        foreach (string anim in names.SelectMany(n => n["TDFX:".Length..].Split('|')).Distinct().Where(a => a != "ANIM_NONE"))
        {
            var type = types.Anim(Enum.Parse<OpenWestwood.TiberianDawn.Logic.AnimType>(anim));
            Assert.True(art.Shape(type.IniName) != null, $"{anim} ({type.IniName}) has art");
        }
        foreach (var bullet in types.AllBullets.Where(b => !b.IsInvisible))
            output.WriteLine($"bullet {bullet.Type} {bullet.IniName}: {(art.Shape(bullet.IniName) is { } s ? s.FrameCount + " frames" : "NO ART")}");
    }
}

public class TdArtProbe(ITestOutputHelper output)
{
    [Fact]
    public void TransportColours()
    {
        if (OpenWestwood.Client.TiberianDawnContent.Art is not { } art) return;
        foreach (string name in new[] { "TRAN", "RROTOR", "LROTOR", "HELI", "MTNK" })
        {
            var shape = art.Shape(name);
            if (shape == null) { output.WriteLine($"{name}: none"); continue; }
            var px = shape.Frame(0);
            int ramp = px.Count(c => c >= 176 && c <= 191), solid = px.Count(c => c != 0 && c != 4);
            output.WriteLine($"{name}: {shape.FrameCount} frames {shape.Width}x{shape.Height}, frame 0 ramp {ramp} of {solid}");
        }
    }
}

/// <summary>Tiberian Dawn's multiplayer maps played on the Zero Hour engine ("td:SCM01EA").</summary>
public class TdMapTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void EveryTdMapStartsAMatchWithDeployableMcvs()
    {
        if (!content.Available || OpenWestwood.Client.TiberianDawnContent.Get() == null) return;
        var cache = new Logic.Multiplayer.MapCache();
        OpenWestwood.Client.TiberianDawn.TdMaps.AddTo(cache);
        Assert.NotEmpty(cache.Maps);
        foreach (var meta in cache.Maps.OrderBy(m => m.FileName))
        {
            var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, meta.FileName);
            var game = GameSetup.FromMap(map, content.Rules!, seed: 5,
            [
                new PlayerSlot("Alice", "FactionTDGDI", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
                new PlayerSlot("Bob", "FactionAmerica", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
            ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
            var alice = game.Players.Find("Alice")!;
            var mcv = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "TDGDI_MCV");
            var bob = game.Players.Find("Bob")!;
            int tiberium = game.Objects.Count(o => o.Template.Name.StartsWith("TD_Tiberium") || o.Template.Name.StartsWith("RA_Ore") || o.Template.Name.StartsWith("RA_Gem"));
            int trees = game.Objects.Count(o => o.Template.Name.StartsWith("TD_Terrain_") || o.Template.Name.StartsWith("RA_Terrain_"));
            bool deploys = mcv.FindModule<TdDeployToStructure>()!.CanDeploy();
            if (!deploys)
                output.WriteLine($"  why: {game.Building.CheckLocation(game.Templates.Find("TDGDI_FACT")!, mcv.Position, 0, alice, mcv)} at {mcv.Position}");
            output.WriteLine($"{meta.NameLookupTag}: {meta.NumPlayers} players, {tiberium} Tiberium, {trees} trees, size {game.Terrain.WorldWidth}x{game.Terrain.WorldHeight}, MCV deploys {deploys}, America has {game.ObjectsOwnedBy(bob).Count()} objects");
            Assert.True(deploys, $"{meta.FileName}: the MCV can deploy at its start");
            Assert.True(tiberium > 0);
            for (int i = 0; i < 60 * LogicTime.FramesPerSecond; i++)
                game.Update();
        }
    }
}

/// <summary>Dune 2000's houses as Zero Hour engine factions (mods/zh/rules/d2k).</summary>
public class D2kFactionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("FactionD2KAtreides", "D2KATR_")]
    [InlineData("FactionD2KHarkonnen", "D2KHAR_")]
    [InlineData("FactionD2KOrdos", "D2KORD_")]
    public void D2kComputerPlayerBuildsABase(string faction, string prefix)
    {
        if (!content.Available || content.Rules!.Get("PlayerTemplates", faction) == null) return;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 9,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", faction, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        Assert.Contains(game.ObjectsOwnedBy(bob), o => o.Template.Name == prefix + "MCV");
        for (int i = 0; i < 240 * LogicTime.FramesPerSecond; i++)
            game.Update();
        var mine = game.ObjectsOwnedBy(bob).ToList();
        output.WriteLine(string.Join(", ", mine.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")) + $"; ${bob.Money.Amount}, earned {bob.Score.TotalMoneyEarned}");
        Assert.Contains(mine, o => o.Template.Name == prefix + "CONSTRUCTION_YARD");
        var yard = mine.First(o => o.Template.Name == prefix + "CONSTRUCTION_YARD");
        var heavy = game.Templates.Find(prefix + "HEAVY_FACTORY")!;
        output.WriteLine($"heavy factory: CanQueue {yard.FindModule<Logic.Modules.Production.ProductionUpdate>()!.CanQueueUnit(heavy)} CanBuild {bob.CanBuild(heavy)} role {TdFactionAI.TdTypeOf(heavy)}");
        Assert.True(mine.Count(o => o.IsKindOf(Kind.Structure)) >= 4);
    }
}

/// <summary>Dune 2000's skirmish maps on the Zero Hour engine ("d2k:habbanya-erg").</summary>
public class D2kMapTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void EveryD2kMapStartsAMatch()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var cache = new Logic.Multiplayer.MapCache();
        OpenWestwood.Client.Dune2000.D2kMaps.AddTo(cache);
        Assert.NotEmpty(cache.Maps);
        var failures = new List<string>();
        foreach (var meta in cache.Maps.OrderBy(m => m.FileName))
        {
            var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, meta.FileName);
            var game = GameSetup.FromMap(map, content.Rules!, seed: 5,
            [
                new PlayerSlot("Alice", "FactionD2KAtreides", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
                new PlayerSlot("Bob", "FactionD2KHarkonnen", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
            ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
            var alice = game.Players.Find("Alice")!;
            var mcv = game.ObjectsOwnedBy(alice).Single(o => o.Template.Name == "D2KATR_MCV");
            int spice = game.Objects.Count(o => o.Template.Name.StartsWith("D2K_Spice"));
            bool deploys = mcv.FindModule<TdDeployToStructure>()!.CanDeploy();
            output.WriteLine($"{meta.NameLookupTag}: {meta.NumPlayers} players, {spice} spice piles, {game.Terrain.WorldWidth}x{game.Terrain.WorldHeight}, MCV deploys {deploys}");
            if (!deploys || spice == 0)
                failures.Add(meta.FileName);
        }
        Assert.Empty(failures);
    }
}

public class D2kCameoProbe(ITestOutputHelper output)
{
    [Fact]
    public void EveryD2kButtonHasACameo()
    {
        if (OpenWestwood.Client.Dune2000.D2kArt.Get() is not { } art) return;
        string file = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "mods", "zh", "rules", "d2k", "command-buttons.yaml");
        var images = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"D2KCAMEO:(\S+)").Select(m => m.Groups[1].Value).Distinct();
        var missing = images.Where(i => art.Cameo(i) == null).ToList();
        foreach (var i in images)
            output.WriteLine($"{i}: {(art.Cameo(i) is { } c ? $"{c.Width}x{c.Height}" : "NONE")}");
        Assert.Empty(missing);
    }
}

/// <summary>TD / Dune 2000 mechanics on the Zero Hour engine: capture, C4, repair, storage, transports.</summary>
public class TdMechanicsTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic Match()
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionTDGDI", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionTDNod", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    static Logic.Objects.GameObject Spawn(GameLogic game, string name, string owner, System.Numerics.Vector3 at) =>
        game.CreateObject(name, game.Players.Find(owner)!, at with { Z = game.Terrain.GroundHeight(at) })!;

    [Fact]
    public void EngineerCapturesAndCommandoDemolishes()
    {
        if (!content.Available) return;
        var game = Match();
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var plant = Spawn(game, "TDNOD_NUKE", "Bob", mid);
        game.TreatAsBuilt(plant);
        var engineer = Spawn(game, "TDGDI_E6", "Alice", mid + new System.Numerics.Vector3(-120, 0, 0));
        var job = engineer.FindModule<Logic.Modules.Behaviors.TdInfiltrate>();
        output.WriteLine($"module {job != null}, can target {job?.CanTarget(plant)}, kinds capturable {plant.IsKindOf("CAPTURABLE")}");
        game.Commands.Enqueue(new Logic.Commands.TdInfiltrateCommand(game.Players.Find("Alice")!.Index, [engineer.Id], plant.Id));
        for (int i = 0; i < 20 * LogicTime.FramesPerSecond && plant.Owner != game.Players.Find("Alice"); i++)
        {
            game.Update();
            if (i % 60 == 0) output.WriteLine($"{i}: engineer at {engineer.Position} plant {plant.Position} ai {engineer.AI?.State} target {job?.Target} dest {engineer.AI?.Destination} moving {engineer.AI?.IsMoving}");
        }
        Assert.Same(game.Players.Find("Alice"), plant.Owner);
        Assert.True(engineer.IsDestroyed);

        var hand = Spawn(game, "TDNOD_HAND", "Bob", mid + new System.Numerics.Vector3(200, 0, 0));
        game.TreatAsBuilt(hand);
        var commando = Spawn(game, "TDGDI_RMBO", "Alice", mid + new System.Numerics.Vector3(80, -120, 0));
        game.Commands.Enqueue(new Logic.Commands.TdInfiltrateCommand(game.Players.Find("Alice")!.Index, [commando.Id], hand.Id));
        for (int i = 0; i < 20 * LogicTime.FramesPerSecond && !hand.IsEffectivelyDead; i++)
            game.Update();
        Assert.True(hand.IsEffectivelyDead);
        Assert.False(commando.IsDestroyed);
    }

    [Fact]
    public void RepairButtonMendsForMoneyAndSilosCapHarvests()
    {
        if (!content.Available) return;
        var game = Match();
        var alice = game.Players.Find("Alice")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var plant = Spawn(game, "TDGDI_NUKE", "Alice", mid);
        game.TreatAsBuilt(plant);
        plant.Body!.SetHealth(plant.MaxHealth / 2);
        alice.Money.Deposit(1000);
        int before = alice.Money.Amount;
        game.Commands.Enqueue(new Logic.Commands.ToggleTdRepairCommand(alice.Index, plant.Id));
        for (int i = 0; i < 10 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"health {plant.Health}/{plant.MaxHealth}, money {before} -> {alice.Money.Amount}");
        Assert.True(plant.Health > plant.MaxHealth / 2);
        Assert.True(alice.Money.Amount < before);

        // Storage: harvested money only up to the silo space.
        Assert.Equal(300, alice.Money.DepositHarvested(500, 300));
        Assert.Equal(0, alice.Money.DepositHarvested(100, 300));
    }

    [Fact]
    public void ApcCarriesInfantry()
    {
        if (!content.Available) return;
        var game = Match();
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var apc = Spawn(game, "TDGDI_APC", "Alice", mid);
        var soldier = Spawn(game, "TDGDI_E1", "Alice", mid + new System.Numerics.Vector3(-60, 0, 0));
        game.Commands.Enqueue(new Logic.Commands.EnterCommand(game.Players.Find("Alice")!.Index, [soldier.Id], apc.Id));
        output.WriteLine($"apc contain {apc.Contain != null} valid {apc.Contain?.IsValidContainerFor(soldier, true)} type {apc.Contain?.GetType().Name} max {apc.Contain?.ContainMax} soldier kinds {string.Join(" ", soldier.Template.KindOf)} owners {soldier.Owner.Name}/{apc.Owner.Name} hasInf {soldier.KindOf.HasAny(Logic.Core.Flags.KindOf.Parse("INFANTRY"))} isInf {soldier.IsKindOf("INFANTRY")} rel {soldier.Owner.RelationshipTo(apc.Owner)} soldier ai {soldier.AI?.GetType().Name} can move {soldier.AI?.CanMove}");
        for (int i = 0; i < 10 * LogicTime.FramesPerSecond && !soldier.IsContained; i++)
        {
            game.Update();
            if (i % 60 == 0) output.WriteLine($"{i}: soldier at {soldier.Position} apc {apc.Position} ai {soldier.AI?.State}");
        }
        Assert.True(soldier.IsContained);
    }
}

/// <summary>TD's Ion Cannon and nuke, Dune 2000's palace powers, as Zero Hour special powers.</summary>
public class TdPowerTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic Match(string a, string b)
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", a, IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", b, IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    [Fact]
    public void OrcaRunsDryAndRearmsAtTheHelipad()
    {
        if (!content.Available) return;
        var game = Match("FactionTDGDI", "FactionTDNod");
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var pad = game.CreateObject("TDGDI_HPAD", alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        game.TreatAsBuilt(pad);
        var plantAt = mid + new System.Numerics.Vector3(350, 0, 0);
        var plant = game.CreateObject("TDNOD_NUKE", bob, plantAt with { Z = game.Terrain.GroundHeight(plantAt) })!;
        game.TreatAsBuilt(plant);
        var orca = game.CreateObject("TDGDI_ORCA", alice, mid + new System.Numerics.Vector3(0, 80, 60))!;
        var ammo = orca.FindModule<OpenWestwood.Logic.Modules.Behaviors.TdAircraftAmmo>()!;
        orca.AI!.AttackObject(plant);
        int frames = 0;
        for (; frames < 60 * LogicTime.FramesPerSecond && ammo.Ammo != 0; frames++)
            game.Update();
        output.WriteLine($"out of ammo after {frames / LogicTime.FramesPerSecond}s; plant health {plant.Body?.Health}");
        Assert.Equal(0, ammo.Ammo);
        for (int i = 0; i < 3 * LogicTime.FramesPerSecond; i++)
            game.Update(); // the last missile lands
        float healthWhenDry = plant.Body!.Health;
        for (int i = 0; i < 30 * LogicTime.FramesPerSecond && ammo.Ammo < 6; i++)
            game.Update();
        output.WriteLine($"ammo {ammo.Ammo}; {System.Numerics.Vector2.Distance(new(orca.Position.X, orca.Position.Y), new(pad.Position.X, pad.Position.Y))} from the pad");
        Assert.Equal(healthWhenDry, plant.Body!.Health); // no shots while dry
        Assert.Equal(6, ammo.Ammo);
    }

    [Fact]
    public void AtreidesAirstrikeNeedsTheUpgradeThenBombs()
    {
        if (!content.Available) return;
        var game = Match("FactionD2KAtreides", "FactionTDNod");
        var alice = game.Players.Find("Alice")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var factory = game.CreateObject("D2KATR_HIGH_TECH_FACTORY", alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        game.TreatAsBuilt(factory);
        var power = factory.FindModules<Logic.Modules.SpecialPowers.OCLSpecialPower>().First(p => p.Power?.Name.EndsWith("Airstrike") == true);
        Assert.False(Logic.Modules.SpecialPowers.SpecialPowerRules.CanUseSpecialPower(factory, power.Power!));
        alice.CompleteUpgrade("Upgrade_D2K_hightech");
        Assert.True(Logic.Modules.SpecialPowers.SpecialPowerRules.CanUseSpecialPower(factory, power.Power!));
        var targetAt = mid + new System.Numerics.Vector3(400, 0, 0);
        var victim = game.CreateObject("TDNOD_NUKE", game.Players.Find("Bob")!, targetAt with { Z = game.Terrain.GroundHeight(targetAt) })!;
        game.TreatAsBuilt(victim);
        power.DoSpecialPowerAtLocation(victim.Position, float.NaN, 0);
        int planes = 0;
        for (int i = 0; i < 40 * LogicTime.FramesPerSecond && victim.Health >= victim.MaxHealth; i++)
        {
            game.Update();
            planes = Math.Max(planes, game.Objects.Count(o => o.Template.Name == "D2KATR_ORNITHOPTER_STRIKE" && !o.IsDestroyed));
        }
        output.WriteLine($"ornithopters {planes}; victim {victim.Health}/{victim.MaxHealth}");
        Assert.Equal(3, planes);
        Assert.True(victim.Health < victim.MaxHealth);
    }

    [Theory]
    [InlineData("FactionTDGDI", "TDGDI_EYE", true)]
    [InlineData("FactionTDNod", "TDNOD_TMPL", true)]
    [InlineData("FactionD2KHarkonnen", "D2KHAR_PALACE", true)]
    [InlineData("FactionD2KAtreides", "D2KATR_PALACE", false)]
    [InlineData("FactionD2KOrdos", "D2KORD_PALACE", false)]
    public void PowerFires(string faction, string building, bool targeted)
    {
        if (!content.Available) return;
        var game = Match(faction, "FactionTDNod");
        var alice = game.Players.Find("Alice")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var source = game.CreateObject(building, alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        game.TreatAsBuilt(source);
        var power = source.FindModule<Logic.Modules.SpecialPowers.OCLSpecialPower>();
        Assert.NotNull(power);
        var targetAt = mid + new System.Numerics.Vector3(300, 0, 0);
        var victim = game.CreateObject("TDNOD_NUKE", game.Players.Find("Bob")!, targetAt with { Z = game.Terrain.GroundHeight(targetAt) })!;
        game.TreatAsBuilt(victim);
        int before = game.ObjectsOwnedBy(alice).Count();
        if (targeted)
            power!.DoSpecialPowerAtLocation(victim.Position, float.NaN, 0);
        else
            power!.DoSpecialPower(0);
        for (int i = 0; i < 10 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"{building}: victim {victim.Health}/{victim.MaxHealth}, own objects {before} -> {game.ObjectsOwnedBy(alice).Count()}");
        if (targeted)
            Assert.True(victim.Health < victim.MaxHealth || victim.IsEffectivelyDead);
        else
            Assert.True(game.ObjectsOwnedBy(alice).Count() > before);
    }
}

public class TdTiberiumTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void TiberiumGrowsSpreadsAndPoisons()
    {
        if (!content.Available || OpenWestwood.Client.TiberianDawnContent.Get() == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "td:SCM01EA");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 5,
        [
            new PlayerSlot("Alice", "FactionTDGDI", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        (int Patches, int Boxes) Census() => (game.Objects.Count(o => o.Template.Name.StartsWith("TD_Tiberium")),
            game.Objects.Sum(o => o.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>()?.Boxes ?? 0));
        var before = Census();
        var patch = game.Objects.First(o => o.Template.Name.StartsWith("TD_Tiberium"));
        var soldier = game.CreateObject("TDGDI_E1", game.Players.Find("Alice")!, patch.Position + new System.Numerics.Vector3(-30, 0, 0))!;
        float health = soldier.Health;
        for (int i = 0; i < 4; i++)
        {
            soldier.AI!.MoveTo(patch.Position + new System.Numerics.Vector3(i % 2 == 0 ? 30 : -30, 0, 0), Logic.Commands.CommandSource.Player);
            for (int f = 0; f < 3 * LogicTime.FramesPerSecond; f++)
                game.Update();
        }
        for (int i = 0; i < 5 * 60 * LogicTime.FramesPerSecond; i++)
            game.Update();
        var after = Census();
        output.WriteLine($"patches {before.Patches} -> {after.Patches}, boxes {before.Boxes} -> {after.Boxes}; soldier {health} -> {soldier.Health}");
        Assert.True(after.Boxes > before.Boxes, "Tiberium grows");
        Assert.True(after.Patches > before.Patches, "Tiberium spreads");
        Assert.True(soldier.Health < health || soldier.IsEffectivelyDead, "Tiberium hurts infantry");
    }
}

public class D2kWormTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic Match(string map) =>
        GameSetup.FromMap(OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, map), content.Rules!, seed: 5,
        [
            new PlayerSlot("Alice", "FactionD2KAtreides", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);

    [Fact]
    public void WormSwallowsAHarvesterOnTheSand()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var game = Match("d2k:habbanya-ridge");
        Assert.True(game.Terrain.HasSand);
        // A sand spot well away from rock.
        System.Numerics.Vector3 spot = default;
        for (float x = 200; x < game.Terrain.WorldWidth - 200 && spot == default; x += 40)
            for (float y = 200; y < game.Terrain.WorldHeight - 200 && spot == default; y += 40)
                if (Enumerable.Range(0, 9).All(i => game.Terrain.IsSand(x + (i % 3 - 1) * 60, y + (i / 3 - 1) * 60)))
                    spot = new System.Numerics.Vector3(x, y, 0);
        Assert.NotEqual(default, spot);
        var harvester = game.CreateObject("D2KATR_HARVESTER", game.Players.Find("Alice")!, spot with { Z = game.Terrain.GroundHeight(spot) })!;
        var wormAt = spot + new System.Numerics.Vector3(90, 0, 0);
        var worm = game.CreateObject("D2K_Sandworm", game.Players.Neutral, wormAt with { Z = game.Terrain.GroundHeight(wormAt) })!;
        for (int i = 0; i < 30 * LogicTime.FramesPerSecond && !harvester.IsDestroyed; i++)
            game.Update();
        output.WriteLine($"harvester destroyed {harvester.IsDestroyed}; worm at {worm.Position}");
        Assert.True(harvester.IsDestroyed);
    }

    [Fact]
    public void DeviatorTurnsAVehicleForAWhile()
    {
        if (!content.Available) return;
        var game = GameSetup.FromMap(OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "d2k:habbanya-ridge"), content.Rules!, seed: 5,
        [
            new PlayerSlot("Alice", "FactionD2KOrdos", IsHuman: true, StartPosition: 1, Color: 0x00FF00),
            new PlayerSlot("Bob", "FactionD2KHarkonnen", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var at = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var deviator = game.CreateObject("D2KORD_DEVIATOR", alice, at with { Z = game.Terrain.GroundHeight(at) })!;
        var tankAt = at + new System.Numerics.Vector3(600, 0, 0); // out of the deviator's reach: it would turn it again
        var tank = game.CreateObject("D2KHAR_QUAD", bob, tankAt with { Z = game.Terrain.GroundHeight(tankAt) })!;
        var weapon = game.Catalog.Weapon("D2K_Weapon_DeviatorMissile")!;
        game.Damage.DealWeaponDamage(weapon, deviator, tank, tank.Position, OpenWestwood.Logic.Combat.WeaponBonus.None);
        Assert.Same(alice, tank.Owner);
        output.WriteLine($"tank health after the hit: {tank.Body?.Health}");
        for (int i = 0; i < 10 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % LogicTime.FramesPerSecond == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: owner {tank.Owner.Name} health {tank.Body?.Health} deviator health {deviator.Body?.Health}");
        }
        Assert.Same(alice, tank.Owner);
        for (int i = 0; i < 8 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"tank owner after 18 s: {tank.Owner.Name}, health {tank.Body?.Health}");
        Assert.Same(bob, tank.Owner);
    }

    static System.Numerics.Vector3 SandSpot(GameLogic game)
    {
        for (float x = 200; x < game.Terrain.WorldWidth - 200; x += 40)
            for (float y = 200; y < game.Terrain.WorldHeight - 200; y += 40)
                if (Enumerable.Range(0, 25).All(i => game.Terrain.IsSand(x + (i % 5 - 2) * 70, y + (i / 5 - 2) * 70))
                    && !game.ObjectsInRadius(new(x, y, 0), 250).Any(o => o.Template.Name.StartsWith("D2K_Spice", StringComparison.Ordinal)))
                    return new System.Numerics.Vector3(x, y, game.Terrain.GroundHeight(new(x, y, 0)));
        throw new InvalidOperationException("no open sand");
    }

    [Fact]
    public void DeployedThumperDrawsAWorm()
    {
        if (!content.Available) return;
        var game = Match("d2k:habbanya-ridge");
        var spot = SandSpot(game);
        var thumper = game.CreateObject("D2KATR_THUMPER", game.Players.Find("Alice")!, spot)!;
        var wormAt = spot + new System.Numerics.Vector3(300, 0, 0);
        var worm = game.CreateObject("D2K_Sandworm", game.Players.Neutral, wormAt with { Z = game.Terrain.GroundHeight(wormAt) })!;
        thumper.FindModule<OpenWestwood.Logic.Modules.Behaviors.D2kThumper>()!.ToggleDeploy();
        float start = System.Numerics.Vector3.Distance(worm.Position, spot), closest = start;
        for (int i = 0; i < 20 * LogicTime.FramesPerSecond && !thumper.IsDestroyed; i++)
        {
            game.Update();
            if (!worm.IsDestroyed)
                closest = Math.Min(closest, System.Numerics.Vector3.Distance(worm.Position, spot));
        }
        output.WriteLine($"worm from {start} to {closest}; thumper eaten {thumper.IsDestroyed}");
        Assert.True(closest < start / 2);
    }

    [Fact]
    public void SpiceBloomGrowsAndBursts()
    {
        if (!content.Available) return;
        var game = Match("d2k:habbanya-ridge");
        var spot = SandSpot(game);
        var bloom = game.CreateObject("D2K_SpiceBloomPoint", game.Players.Neutral, spot)!;
        var module = bloom.FindModule<OpenWestwood.Logic.Modules.Behaviors.D2kSpiceBloom>()!;
        int Spice() => game.ObjectsInRadius(spot, 6 * OpenWestwood.Logic.Modules.Behaviors.TdScale.Cell).Count(o => o.Template.Name.StartsWith("D2K_Spice", StringComparison.Ordinal) && o != bloom);
        int before = Spice(), maxStage = -1;
        // At most 3250 + 3000 ticks (250 s).
        for (int i = 0; i < 260 * LogicTime.FramesPerSecond && Spice() == before; i++)
        {
            game.Update();
            maxStage = Math.Max(maxStage, module.Stage);
        }
        output.WriteLine($"stage reached {maxStage}; spice piles {before} -> {Spice()}");
        Assert.True(maxStage >= 2);
        Assert.True(Spice() > before);
    }

    [Fact]
    public void StarportDeliversOrdersTogetherAfterAMinute()
    {
        if (!content.Available) return;
        var game = Match("d2k:habbanya-ridge");
        var alice = game.Players.Find("Alice")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var port = game.CreateObject("D2KATR_STARPORT", alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        game.TreatAsBuilt(port);
        alice.Money.Deposit(5000);
        int moneyBefore = alice.Money.Amount;
        var production = port.FindModule<OpenWestwood.Logic.Modules.Production.ProductionUpdate>()!;
        var trike = game.Templates.Find("D2KATR_TRIKE_STARPORT")!;
        Assert.NotNull(production.QueueUnit(trike));
        Assert.NotNull(production.QueueUnit(trike));
        int Trikes() => game.Objects.Count(o => o.Template.Name == "D2KATR_TRIKE_STARPORT" && !o.IsDestroyed);
        for (int i = 0; i < 30 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"after 30 s: {Trikes()} trikes, spent {moneyBefore - alice.Money.Amount}");
        Assert.Equal(0, Trikes());
        Assert.Equal(2 * trike.BuildCost, moneyBefore - alice.Money.Amount);
        for (int i = 0; i < 32 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"after 62 s: {Trikes()} trikes");
        Assert.Equal(2, Trikes());
    }

    [Fact]
    public void CarryallFerriesAVehicleWithFarToGo()
    {
        if (!content.Available) return;
        var game = Match("d2k:habbanya-ridge");
        var alice = game.Players.Find("Alice")!;
        var from = SandSpot(game);
        var tank = game.CreateObject("D2KATR_COMBAT_TANK_A", alice, from) ?? game.CreateObject("D2KATR_QUAD", alice, from)!;
        var carryAt = from + new System.Numerics.Vector3(-150, 0, 60);
        var carryall = game.CreateObject("D2KATR_CARRYALL", alice, carryAt)!;
        var module = carryall.FindModule<OpenWestwood.Logic.Modules.Behaviors.D2kCarryall>()!;
        var to = from + new System.Numerics.Vector3(0, 0, 0);
        // Open sand 12+ cells off.
        for (float d = 600; d >= 420 && to == from; d -= 60)
            foreach (var dir in new[] { new System.Numerics.Vector3(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0) })
            {
                var p = from + dir * d;
                if (game.Terrain.IsInsidePlayableArea(p.X, p.Y) && Enumerable.Range(0, 9).All(k => game.Terrain.IsSand(p.X + (k % 3 - 1) * 40, p.Y + (k / 3 - 1) * 40)))
                {
                    to = p with { Z = game.Terrain.GroundHeight(p) };
                    break;
                }
            }
        output.WriteLine($"from {from} to {to}");
        tank.AI!.MoveTo(to);
        game.Update();
        output.WriteLine($"tank {tank.Template.Name} carryable {tank.FindModule<OpenWestwood.Logic.Modules.Behaviors.D2kCarryable>()?.WantsLift} moving {tank.AI.IsMoving} dest {tank.AI.Destination} carryall ai {carryall.AI != null} pos {carryall.Position}");
        bool lifted = false;
        int frames = 0;
        for (; frames < 60 * LogicTime.FramesPerSecond; frames++)
        {
            game.Update();
            lifted |= module.IsCarrying;
            if (lifted && !module.IsCarrying && !tank.IsDisabledBy("DISABLED_HELD"))
                break;
        }
        float left = System.Numerics.Vector2.Distance(new(tank.Position.X, tank.Position.Y), new(to.X, to.Y));
        output.WriteLine($"lifted {lifted}; set down after {frames / (float)LogicTime.FramesPerSecond:0.0}s, {left:0} from the goal");
        Assert.True(lifted);
        Assert.True(left < 2 * OpenWestwood.Logic.Modules.Behaviors.TdScale.Cell);
    }

    [Fact]
    public void BuildingsOffConcreteWeakenAndSandIsNoFoundation()
    {
        if (!content.Available) return;
        var game = Match("d2k:habbanya-ridge");
        var alice = game.Players.Find("Alice")!;
        // Two rock spots: no sand or cliff within 2 cells.
        var spots = new List<System.Numerics.Vector3>();
        for (float x = 200; x < game.Terrain.WorldWidth - 200 && spots.Count < 2; x += 37)
            for (float y = 200; y < game.Terrain.WorldHeight - 200 && spots.Count < 2; y += 37)
            {
                bool rock = true;
                for (int i = -2; i <= 2 && rock; i++)
                    for (int j = -2; j <= 2 && rock; j++)
                    {
                        float px = x + i * 33, py = y + j * 33;
                        var (cx, cy) = game.Terrain.CellAt(px, py);
                        rock = !game.Terrain.IsSand(px, py) && !game.Terrain.IsCliffCell(cx, cy);
                    }
                if (rock && spots.All(s => System.Numerics.Vector2.Distance(new(s.X, s.Y), new(x, y)) > 300))
                    spots.Add(new System.Numerics.Vector3(x, y, game.Terrain.GroundHeight(new(x, y, 0))));
            }
        Assert.Equal(2, spots.Count);
        var slab = game.CreateObject("D2KATR_CONCRETEB", alice, spots[1])!;
        game.TreatAsBuilt(slab);
        game.Update();
        game.Update();
        Assert.True(slab.IsDestroyed);
        Assert.Equal(9, OpenWestwood.Logic.Modules.Behaviors.D2kConcreteLayer.Cells(game).Count);
        var bare = game.CreateObject("D2KATR_WIND_TRAP", alice, spots[0])!;
        game.TreatAsBuilt(bare);
        var paved = game.CreateObject("D2KATR_WIND_TRAP", alice, spots[1] + new System.Numerics.Vector3(16.2f, 16.2f, 0))!; // a 2x2 on the grid the 3x3 slab is on
        game.TreatAsBuilt(paved);
        for (int i = 0; i < 2 * LogicTime.FramesPerSecond; i++)
            game.Update();
        output.WriteLine($"on rock {bare.Health}/{bare.MaxHealth}; on concrete {paved.Health}/{paved.MaxHealth}");
        Assert.Equal(bare.MaxHealth / 2, bare.Health, 1f);
        Assert.Equal(paved.MaxHealth, paved.Health);
        // Sand is no place for a building.
        var sand = SandSpot(game);
        var trap = game.Templates.Find("D2KATR_WIND_TRAP")!;
        game.TreatAsBuilt(game.CreateObject("D2KATR_WIND_TRAP", alice, sand + new System.Numerics.Vector3(80, 0, 0))!); // a base next to it
        Assert.Equal(OpenWestwood.Logic.Construction.BuildLocation.RestrictedTerrain, game.Building.CheckLocation(trap, sand, 0, alice));
    }

    [Fact]
    public void NoWormsOffDuneMaps()
    {
        if (!content.Available) return;
        var game = Match("Maps/Tournament Desert/Tournament Desert.map");
        for (int i = 0; i < 8 * 60 * LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.DoesNotContain(game.Objects, o => o.Template.Name == "D2K_Sandworm");
    }
}

public class TdD2kSpeechTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void EveryAnnouncerLineDecodes()
    {
        if (!content.Available) return;
        var bad = new List<string>();
        int checkedFiles = 0;
        foreach (var (name, node) in content.Rules!.Section("DialogEvents").Entries)
        {
            string file = node.GetString("Filename");
            if (!file.StartsWith('/'))
                continue;
            string path = file[1..];
            if (!content.FileSystem!.Exists(path))
            {
                bad.Add($"{name}: {path} missing");
                continue;
            }
            var bytes = content.FileSystem.ReadAllBytes(path);
            var pcm = OpenWestwood.Formats.Audio.AudFile.IsAud(bytes) ? OpenWestwood.Formats.Audio.AudFile.Decode(bytes) : OpenWestwood.Formats.Audio.WavFile.Decode(bytes);
            checkedFiles++;
            double meanAbs = pcm.Samples.Average(s => Math.Abs((int)s));
            output.WriteLine($"{name}: {pcm.DurationSeconds:0.00}s at {pcm.SampleRate} Hz, mean level {meanAbs:0}");
            if (pcm.DurationSeconds is < 0.2 or > 15 || meanAbs is < 50 or > 16000)
                bad.Add($"{name}: {pcm.DurationSeconds:0.00}s level {meanAbs:0}");
        }
        // TD weapon sounds (AudioEvents with "/NAME.AUD" sounds).
        foreach (var (name, node) in content.Rules!.Section("AudioEvents").Entries)
            foreach (string sound in OpenWestwood.Core.Rules.RuleValue.Words(node.GetString("Sounds")).Where(w => w.StartsWith('/') && !w.StartsWith("/RA2SFX/", StringComparison.OrdinalIgnoreCase)))
            {
                if (!content.FileSystem!.Exists(sound[1..]))
                    bad.Add($"{name}: {sound} missing");
                else if (content.FileSystem.ReadAllBytes(sound[1..]) is var bytes && (OpenWestwood.Formats.Audio.AudFile.IsAud(bytes) ? OpenWestwood.Formats.Audio.AudFile.Decode(bytes) : OpenWestwood.Formats.Audio.WavFile.Decode(bytes)).DurationSeconds < 0.05)
                    bad.Add($"{name}: {sound} empty");
                else
                    checkedFiles++;
            }
        Assert.Empty(bad);
        Assert.True(checkedFiles >= 60 + 15, $"{checkedFiles} files");
    }
}

public class MapVisibilityTests(BaseGameFixture content) : IClassFixture<BaseGameFixture>
{
    GameLogic Match() => GameSetup.FromMap(MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map")), content.Rules!, seed: 3,
    [
        new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
        new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
    ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);

    [Theory]
    [InlineData(false, false, OpenWestwood.Logic.Vision.CellShroud.Shrouded)]
    [InlineData(true, false, OpenWestwood.Logic.Vision.CellShroud.Fogged)]
    [InlineData(false, true, OpenWestwood.Logic.Vision.CellShroud.Clear)]
    public void FarCornerFromAlice(bool explored, bool noFog, OpenWestwood.Logic.Vision.CellShroud expected)
    {
        if (!content.Available) return;
        var game = Match();
        GameSetup.ApplyVisibility(game, explored, noFog);
        for (int i = 0; i < 30; i++)
            game.Update();
        var alice = game.Players.Find("Alice")!;
        var bobBase = game.Objects.First(o => o.Owner == game.Players.Find("Bob") && o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure));
        Assert.Equal(expected, game.Shroud.StatusAt(alice, bobBase.Position));
    }
}

public class RaMixTests(ITestOutputHelper output)
{
    [Fact]
    public void RedAlertMixDecryptsAndNests()
    {
        string path = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "redalert", "REDALERT.MIX");
        if (!File.Exists(path)) return;
        string[] names = ["LOCAL.MIX", "HIRES.MIX", "LORES.MIX", "SPEECH.MIX", "NCHIRES.MIX", "RULES.INI", "AFTRMATH.INI", "SIDE1NA.SHP", "STRIP.SHP"];
        using var mix = new OpenWestwood.Core.FileSystem.MixArchive(path, names);
        output.WriteLine($"{mix.Name}: {mix.Count} entries; {string.Join(", ", mix.FilePaths)}");
        using var local = mix.OpenNested("LOCAL.MIX");
        Assert.NotNull(local);
        output.WriteLine($"LOCAL.MIX: {local!.Count} entries; {string.Join(", ", local.FilePaths.Take(12))}");
        using var rules = new StreamReader(local.Open("RULES.INI"));
        string head = rules.ReadToEnd();
        output.WriteLine(head[..300]);
        Assert.Contains("[General]", head);
    }
}

public class RaMixCensus(ITestOutputHelper output)
{
    [Fact]
    public void ListEveryRedAlertMix()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        string folder = Path.Combine(root, "content", "redalert");
        if (!Directory.Exists(folder)) return;
        var names = File.ReadAllLines(Path.Combine(root, "mods", "redalert", "filenames.txt"));
        void Walk(OpenWestwood.Core.FileSystem.MixArchive mix, string indent)
        {
            output.WriteLine($"{indent}{mix.Name}: {mix.Count} entries, {mix.UnknownCrcs.Count()} unknown");
            foreach (string inner in mix.FilePaths.Where(p => p.EndsWith(".MIX", StringComparison.OrdinalIgnoreCase)).ToList())
                if (mix.OpenNested(inner) is { } nested)
                    using (nested)
                        Walk(nested, indent + "  ");
        }
        foreach (string file in Directory.GetFiles(folder, "*.MIX"))
            using (var mix = new OpenWestwood.Core.FileSystem.MixArchive(file, names))
                Walk(mix, "");
    }
}

public class RaContentTests(ITestOutputHelper output)
{
    [Fact]
    public void RedAlertModLoadsArtAndRules()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        if (!File.Exists(Path.Combine(root, "content", "redalert", "REDALERT.MIX"))) return;
        using var ra = OpenWestwood.TiberianDawn.TdContent.Load(root, "redalert");
        Assert.True(ra.Files.Exists("RULES.INI"));
        foreach (string name in new[] { "TEMPERAT.PAL", "E1.SHP", "1TNK.SHP", "MCV.SHP", "FACT.TEM", "E1ICON.SHP", "SIDE1.SHP", "STRIP.SHP", "AFTRMATH.INI" })
        {
            bool exists = ra.Files.Exists(name);
            string info = "";
            if (exists && name.EndsWith(".SHP") || name.EndsWith(".TEM"))
            {
                try { var shape = new OpenWestwood.Formats.Westwood.KeyframeShape(ra.Files.ReadAllBytes(name)); info = $"{shape.FrameCount} frames {shape.Width}x{shape.Height}"; }
                catch (Exception e) { info = e.GetType().Name + ": " + e.Message; }
            }
            output.WriteLine($"{name}: {(exists ? "yes" : "no")} {info}");
        }
        Assert.True(ra.Files.Exists("E1.SHP"));
    }
}

public class RaDump
{
    [Fact]
    public void DumpRules()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        string outDir = Environment.GetEnvironmentVariable("RA_DUMP") ?? "";
        if (outDir.Length == 0 || !File.Exists(Path.Combine(root, "content", "redalert", "REDALERT.MIX"))) return;
        using var ra = OpenWestwood.TiberianDawn.TdContent.Load(root, "redalert");
        foreach (string f in new[] { "RULES.INI", "AFTRMATH.INI", "MPLAYER.INI" })
            if (ra.Files.Exists(f))
                File.WriteAllBytes(Path.Combine(outDir, f), ra.Files.ReadAllBytes(f));
    }
}

public class RaNameProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListInterfaceArt()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        if (!File.Exists(Path.Combine(root, "content", "redalert", "REDALERT.MIX"))) return;
        using var ra = OpenWestwood.TiberianDawn.TdContent.Load(root, "redalert");
        var hits = ra.Files.Packages.SelectMany(p => p.FilePaths).Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), "^(SIDE|STRIP|TABS|POWER|PWR|REPAIR|SELL|MAP|RADAR|NRADAR|URADAR|ARADAR|CLOCK|HSIDE|ULOGO|ALOGO|LOGO|SPWR|PIPS|BTN)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Distinct().OrderBy(p => p);
        output.WriteLine(string.Join(" ", hits));
    }
}

public class RaFactionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void AlliedAndSovietTanksFightItOut()
    {
        if (!content.Available || content.Rules!.Section("Objects")["RAALL_2TNK"] == null) return;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionRAEngland", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionRARussia", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var mine = new List<OpenWestwood.Logic.Objects.GameObject>();
        var theirs = new List<OpenWestwood.Logic.Objects.GameObject>();
        for (int i = 0; i < 3; i++)
        {
            var a = mid + new System.Numerics.Vector3(-70, (i - 1) * 40, 0);
            var b = mid + new System.Numerics.Vector3(70, (i - 1) * 40, 0);
            mine.Add(game.CreateObject("RAALL_2TNK", game.Players.Find("Alice")!, a with { Z = game.Terrain.GroundHeight(a) })!);
            theirs.Add(game.CreateObject("RASOV_3TNK", game.Players.Find("Bob")!, b with { Z = game.Terrain.GroundHeight(b) })!);
        }
        int frames = 0;
        for (; frames < 90 * LogicTime.FramesPerSecond && mine.Any(o => !o.IsEffectivelyDead) && theirs.Any(o => !o.IsEffectivelyDead); frames++)
            game.Update();
        output.WriteLine($"after {frames / LogicTime.FramesPerSecond}s: Allied tanks left {mine.Count(o => !o.IsEffectivelyDead)}, Soviet {theirs.Count(o => !o.IsEffectivelyDead)}");
        Assert.True(mine.All(o => o.IsEffectivelyDead) || theirs.All(o => o.IsEffectivelyDead));
    }
}

public class RaPowerTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic Match()
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionRAEngland", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionRARussia", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    static System.Numerics.Vector3 At(GameLogic game, float dx, float dy)
    {
        var p = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2 + dx, game.Terrain.WorldHeight / 2 + dy, 0);
        return p with { Z = game.Terrain.GroundHeight(p) };
    }

    static T Power<T>(GameLogic game, string building, string owner, string templateEnd) where T : Logic.Modules.SpecialPowers.SpecialPowerModule
    {
        var source = game.CreateObject(building, game.Players.Find(owner)!, At(game, -400, 0))!;
        game.TreatAsBuilt(source);
        var module = source.FindModules<T>().First(m => m.Power!.Name.EndsWith(templateEnd));
        module.SetReadyFrame(0); // charged
        return module;
    }

    [Fact]
    public void NukeParatroopersAndGps()
    {
        if (!content.Available || content.Rules!.Section("Objects")["RASOV_MSLO"] == null) return;
        var game = Match();
        var target = game.CreateObject("RAALL_POWR", game.Players.Find("Alice")!, At(game, 200, 0))!;
        game.TreatAsBuilt(target);
        Power<Logic.Modules.SpecialPowers.OCLSpecialPower>(game, "RASOV_MSLO", "Bob", "Nuke").DoSpecialPowerAtLocation(target.Position, float.NaN, 0);
        int bobBefore = game.ObjectsOwnedBy(game.Players.Find("Bob")!).Count(o => o.Template.Name == "RASOV_E1");
        Power<Logic.Modules.SpecialPowers.OCLSpecialPower>(game, "RASOV_AFLD", "Bob", "Paratroopers").DoSpecialPowerAtLocation(At(game, 0, 200), float.NaN, 0);
        var gps = Power<Logic.Modules.Behaviors.RaSuperweapon>(game, "RAALL_ATEK", "Alice", "GPS");
        gps.DoSpecialPower(0);
        for (int i = 0; i < 5 * LogicTime.FramesPerSecond; i++)
            game.Update();
        int bobAfter = game.ObjectsOwnedBy(game.Players.Find("Bob")!).Count(o => o.Template.Name == "RASOV_E1");
        output.WriteLine($"power plant {target.Health}/{target.MaxHealth} dead {target.IsEffectivelyDead}; Bob {bobBefore} -> {bobAfter}");
        Assert.True(target.IsEffectivelyDead || target.Health < target.MaxHealth);
        Assert.Equal(bobBefore + 5, bobAfter);
        var bobBase = game.Objects.First(o => o.Owner == game.Players.Find("Bob") && o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure));
        Assert.Equal(OpenWestwood.Logic.Vision.CellShroud.Clear, game.Shroud.StatusAt(game.Players.Find("Alice")!, bobBase.Position));
    }

    [Fact]
    public void IronCurtainAndChronosphere()
    {
        if (!content.Available || content.Rules!.Section("Objects")["RASOV_IRON"] == null) return;
        var game = Match();
        var tank = game.CreateObject("RASOV_3TNK", game.Players.Find("Bob")!, At(game, 0, 0))!;
        var curtain = Power<Logic.Modules.Behaviors.RaSuperweapon>(game, "RASOV_IRON", "Bob", "IronCurtain");
        curtain.DoSpecialPowerAtObject(tank, 0);
        tank.Damage(new OpenWestwood.Logic.Combat.DamageInfo { Amount = 5000, DamageType = "ARMOR_PIERCING", DeathType = "NORMAL" });
        Assert.False(tank.IsEffectivelyDead);
        Assert.Equal(tank.MaxHealth, tank.Health);

        var jeep = game.CreateObject("RAALL_2TNK", game.Players.Find("Alice")!, At(game, -100, 100))!;
        var home = jeep.Position;
        var chrono = Power<Logic.Modules.Behaviors.RaSuperweapon>(game, "RAALL_PDOX", "Alice", "Chronosphere");
        chrono.DoSpecialPowerAtLocation(jeep.Position, float.NaN, 0); // pick the tank
        Assert.True(chrono.IsReady);
        var there = At(game, 300, -200);
        chrono.DoSpecialPowerAtLocation(there, float.NaN, 0);         // and where it goes
        game.Update();
        float away = System.Numerics.Vector2.Distance(new(jeep.Position.X, jeep.Position.Y), new(there.X, there.Y));
        output.WriteLine($"shifted to within {away:0} of the target");
        Assert.True(away < 40);
        Assert.False(chrono.IsReady);
        for (int i = 0; i < 181 * LogicTime.FramesPerSecond; i++)
            game.Update();
        float back = System.Numerics.Vector2.Distance(new(jeep.Position.X, jeep.Position.Y), new(home.X, home.Y));
        output.WriteLine($"after 3 minutes, {back:0} from home");
        Assert.True(back < 40);
    }

    [Fact]
    public void PowerIconsExist()
    {
        if (OpenWestwood.Client.RedAlertContent.Art is not { } art) return;
        string file = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "mods", "zh", "rules", "ra", "command-buttons.yaml");
        var names = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"RACAMEO:(\S+)").Select(m => m.Groups[1].Value).Distinct();
        var missing = names.Where(n => art.Cameo(n) == null).ToList();
        output.WriteLine("missing: " + string.Join(", ", missing));
        Assert.Empty(missing);
    }
}

public class WaterMapProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void FindMapsWithWater()
    {
        if (!content.Available || Environment.GetEnvironmentVariable("WATER_PROBE") == null) return;
        foreach (string path in content.FileSystem!.List("Maps", ".map", recursive: true).Take(80))
        {
            var terrain = new OpenWestwood.Logic.Terrain.TerrainLogic(MapFile.Read(content.FileSystem.ReadAllBytes(path)));
            int water = 0, total = 0;
            for (float x = 50; x < terrain.WorldWidth; x += 50)
                for (float y = 50; y < terrain.WorldHeight; y += 50, total++)
                    if (terrain.IsUnderwater(x, y, out _))
                        water++;
            if (water > total / 20)
                output.WriteLine($"{path}: {100 * water / Math.Max(total, 1)}% water");
        }
    }
}

public class RaNavalTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void ShipyardBuildsShipsThatSailAndFight()
    {
        if (!content.Available || content.Rules!.Section("Objects")["RAALL_DD"] == null) return;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Dark Mountain/Dark Mountain.map"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionRAEngland", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionRARussia", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        bool OpenWater(float x, float y, float r) => Enumerable.Range(0, 25).All(i => game.Terrain.IsUnderwater(x + (i % 5 - 2) * r / 2, y + (i / 5 - 2) * r / 2, out _));
        System.Numerics.Vector3 spot = default;
        for (float x = 200; x < game.Terrain.WorldWidth - 200 && spot == default; x += 30)
            for (float y = 200; y < game.Terrain.WorldHeight - 200 && spot == default; y += 30)
                if (OpenWater(x, y, 120) && OpenWater(x + 300, y, 80))
                    spot = new System.Numerics.Vector3(x, y, 0);
        Assert.NotEqual(default, spot);
        var alice = game.Players.Find("Alice")!;
        var yard = game.CreateObject("RAALL_SYRD", alice, spot)!;
        game.TreatAsBuilt(yard);
        alice.Money.Deposit(5000);
        var production = yard.FindModule<OpenWestwood.Logic.Modules.Production.ProductionUpdate>()!;
        Assert.NotNull(production.QueueUnit(game.Templates.Find("RAALL_PT")!));
        OpenWestwood.Logic.Objects.GameObject? boat = null;
        for (int i = 0; i < 60 * LogicTime.FramesPerSecond && boat == null; i++)
        {
            game.Update();
            boat = game.Objects.FirstOrDefault(o => o.Template.Name == "RAALL_PT");
        }
        Assert.NotNull(boat);
        Assert.True(game.Terrain.IsUnderwater(boat!.Position.X, boat.Position.Y, out _));
        // A Soviet submarine's victim: the gunboat goes after it (depth charges).
        var subAt = spot + new System.Numerics.Vector3(300, 0, 0);
        var sub = game.CreateObject("RASOV_SS", game.Players.Find("Bob")!, subAt)!;
        var start = boat.Position;
        boat.AI!.AttackObject(sub);
        for (int i = 0; i < 60 * LogicTime.FramesPerSecond && !sub.IsEffectivelyDead && !boat.IsEffectivelyDead; i++)
            game.Update();
        output.WriteLine($"gunboat moved {System.Numerics.Vector3.Distance(start, boat.Position):0}; sub {sub.Health}/{sub.MaxHealth}, gunboat {boat.Health}/{boat.MaxHealth}");
        Assert.True(sub.Health < sub.MaxHealth || boat.Health < boat.MaxHealth);
    }
}

public class RaScenarioProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListScenarios()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        string dump = Environment.GetEnvironmentVariable("RA_DUMP") ?? "";
        if (!File.Exists(Path.Combine(root, "content", "redalert", "REDALERT.MIX"))) return;
        using var ra = OpenWestwood.TiberianDawn.TdContent.Load(root, "redalert");
        var names = ra.Files.Packages.SelectMany(p => p.FilePaths).Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^SC[A-Z][0-9]{2}[A-Z][A-Z]\.INI$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Distinct().OrderBy(p => p).ToList();
        output.WriteLine(string.Join(" ", names));
        if (dump.Length > 0 && names.FirstOrDefault(n => n.StartsWith("SCM", StringComparison.OrdinalIgnoreCase)) is { } first)
            File.WriteAllBytes(Path.Combine(dump, first), ra.Files.ReadAllBytes(first));
    }
}

public class RaMapRenderProbe
{
    [Fact]
    public void RenderCoastalInfluence()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        string outDir = Environment.GetEnvironmentVariable("RA_DUMP") ?? "";
        if (outDir.Length == 0 || !File.Exists(Path.Combine(root, "content", "redalert", "REDALERT.MIX"))) return;
        using var ra = OpenWestwood.TiberianDawn.TdContent.Load(root, "redalert");
        var overlays = ra.Rules.Section("OverlayTypeClass").Entries.Concat(ra.Rules.Section("Overlays").Entries)
            .OrderBy(e => OpenWestwood.Core.Rules.RuleValue.Int(e.Value.GetString("ID"))).Select(e => e.Value.GetString("IniName")).ToList();
        var s = OpenWestwood.TiberianDawn.Data.TdScenario.Load(ra.Files, "SCM01EA", overlays)!;
        Assert.True(s.IsRedAlert);
        var image = new OpenWestwood.TiberianDawn.Art.TdMapRenderer(ra).Render(s);
        OpenWestwood.Formats.Images.PngWriter.Save(image, Path.Combine(outDir, "scm01ea.png"));
    }
}

public class RaMapTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void CoastalInfluencePlays()
    {
        if (!content.Available || !OpenWestwood.Client.RedAlertContent.Available) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "ra:SCM01EA");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionRAEngland", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionRARussia", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        int ore = game.Objects.Count(o => o.Template.Name.StartsWith("RA_Ore")), gems = game.Objects.Count(o => o.Template.Name.StartsWith("RA_Gem")), trees = game.Objects.Count(o => o.Template.Name.StartsWith("RA_Terrain_"));
        output.WriteLine($"ore {ore}, gems {gems}, trees {trees}, world {game.Terrain.WorldWidth}x{game.Terrain.WorldHeight}");
        Assert.True(ore > 100 && gems > 0 && trees > 50);
        var bob = game.Players.Find("Bob")!;
        for (int i = 0; i < 180 * LogicTime.FramesPerSecond; i++)
            game.Update();
        int bobBuildings = game.ObjectsOwnedBy(bob).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure) && !o.IsEffectivelyDead);
        output.WriteLine($"after 3 minutes the Soviet AI has {bobBuildings} buildings and ${bob.Money.Amount}");
        Assert.True(bobBuildings >= 3);
    }
}

public class Ra2MatchTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("FactionRA2Russians")]
    [InlineData("FactionRA2Americans")]
    [InlineData("FactionRA2YuriCountry")]
    public void Ra2AiBuildsABase(string faction)
    {
        if (!content.Available || OpenWestwood.Client.RedAlert2.Ra2Art.Get() == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "ra:SCM01EA");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 5,
        [
            new PlayerSlot("Alice", "FactionRAEngland", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", faction, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        for (int i = 0; i < 180 * LogicTime.FramesPerSecond; i++)
            game.Update();
        var mine = game.ObjectsOwnedBy(bob).Where(o => !o.IsEffectivelyDead).ToList();
        int buildings = mine.Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure));
        output.WriteLine($"{faction}: {buildings} buildings, {mine.Count - buildings} units, ${bob.Money.Amount}: " + string.Join(", ", mine.Select(o => o.Template.Name).Distinct()));
        Assert.True(buildings >= 3);
    }
}

public class Ra2MixCensus(ITestOutputHelper output)
{
    [Fact]
    public void ListEveryRedAlert2Mix()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        string folder = Path.Combine(root, "content", "redalert2");
        if (!Directory.Exists(folder) || Environment.GetEnvironmentVariable("RA2_CENSUS") == null) return;
        var names = File.ReadAllLines(Path.Combine(root, "mods", "redalert2", "filenames.txt"));
        void Walk(OpenWestwood.Core.FileSystem.MixArchive mix, string indent)
        {
            output.WriteLine($"{indent}{mix.Name}: {mix.Count} entries, {mix.FilePaths.Count(p => p.EndsWith(".unknown"))} unknown");
            foreach (string inner in mix.FilePaths.Where(p => p.EndsWith(".MIX", StringComparison.OrdinalIgnoreCase)).ToList())
                if (mix.OpenNested(inner) is { } nested)
                    using (nested)
                        Walk(nested, indent + "  ");
        }
        foreach (string file in Directory.GetFiles(folder, "*.mix"))
            using (var mix = new OpenWestwood.Core.FileSystem.MixArchive(file, names, crc32Ids: true))
                Walk(mix, "");
    }
}

public class Ra2IniDump
{
    [Fact]
    public void DumpInis()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        string outDir = Environment.GetEnvironmentVariable("RA2_DUMP") ?? "";
        if (outDir.Length == 0) return;
        var names = File.ReadAllLines(Path.Combine(root, "mods", "redalert2", "filenames.txt"));
        void Walk(OpenWestwood.Core.FileSystem.MixArchive mix)
        {
            foreach (string p in mix.FilePaths.ToList())
            {
                if (p.EndsWith(".INI", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".CSF", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".PKT", StringComparison.OrdinalIgnoreCase))
                {
                    using var s = mix.Open(p);
                    using var f = File.Create(Path.Combine(outDir, mix.Name.Replace('/', '_') + "__" + p));
                    s.CopyTo(f);
                }
                if (p.EndsWith(".MIX", StringComparison.OrdinalIgnoreCase) && mix.OpenNested(p) is { } nested)
                    using (nested)
                        Walk(nested);
            }
        }
        foreach (string file in Directory.GetFiles(Path.Combine(root, "content", "redalert2"), "*.mix"))
            using (var mix = new OpenWestwood.Core.FileSystem.MixArchive(file, names, crc32Ids: true))
                Walk(mix);
    }
}

public class Ra2FormatTests(ITestOutputHelper output)
{
    [Fact]
    public void ShapesVoxelsAndStringsDecode()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        if (!File.Exists(Path.Combine(root, "content", "redalert2", "ra2md.mix"))) return;
        using var ra2 = OpenWestwood.TiberianDawn.TdContent.Load(root, "redalert2");
        foreach (string shp in new[] { "GTCNST.SHP", "GACNST.SHP", "GI.SHP", "E1.SHP", "HTNKICON.SHP" })
            if (ra2.Files.Exists(shp))
            {
                var s = new OpenWestwood.Formats.Westwood.TsShape(ra2.Files.ReadAllBytes(shp));
                int opaque = s.Frame(0).Count(b => b != 0);
                output.WriteLine($"{shp}: {s.FrameCount} frames {s.Width}x{s.Height}, frame 0 has {opaque} pixels");
            }
            else output.WriteLine($"{shp}: missing");
        var vxl = new OpenWestwood.Formats.Westwood.VoxelModel(ra2.Files.ReadAllBytes("HTNK.VXL"));
        output.WriteLine($"HTNK.VXL: {vxl.Limbs.Count} limbs, {string.Join(", ", vxl.Limbs.Select(l => $"{l.Name} {l.SizeX}x{l.SizeY}x{l.SizeZ} {l.Voxels.Count} voxels"))}");
        Assert.True(vxl.Limbs[0].Voxels.Count > 100);
        var hva = new OpenWestwood.Formats.Westwood.HvaAnimation(ra2.Files.ReadAllBytes("HTNK.HVA"));
        output.WriteLine($"HTNK.HVA: {hva.Frames} frames, limbs {string.Join(",", hva.Limbs)}");
        var csf = OpenWestwood.Formats.Westwood.CsfFile.Read(ra2.Files.ReadAllBytes("RA2MD.CSF"));
        output.WriteLine($"RA2MD.CSF: {csf.Count} strings; Name:HTNK = {csf.GetValueOrDefault("Name:HTNK")}; Name:GI = {csf.GetValueOrDefault("Name:E1")}");
        Assert.True(csf.Count > 1000);
    }
}

public class Ra2ArtProbe
{
    [Fact]
    public void DrawSamples()
    {
        string outDir = Environment.GetEnvironmentVariable("RA2_DUMP") ?? "";
        if (outDir.Length == 0 || OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        for (int f = 0; f < 32; f += 8)
            if (art.VoxelImage("HTNK", f, f, 0xD02020) is { } img)
                OpenWestwood.Formats.Images.PngWriter.Save(img, Path.Combine(outDir, $"htnk_{f}.png"));
        if (art.VoxelImage("MTNK", 4, 4, 0x2050FF) is { } grizzly)
            OpenWestwood.Formats.Images.PngWriter.Save(grizzly, Path.Combine(outDir, "mtnk.png"));
        if (art.BuildingShape("GACNST") is { } yard && art.Frame(yard, 0, art.UnitPalette, (32, 80, 255)) is { } y)
            OpenWestwood.Formats.Images.PngWriter.Save(y, Path.Combine(outDir, "gacnst.png"));
        if (art.Shape("GI") is { } gi && art.Frame(gi, 8, art.UnitPalette, (32, 80, 255)) is { } g)
            OpenWestwood.Formats.Images.PngWriter.Save(g, Path.Combine(outDir, "gi.png"));
        if (art.Cameo("HTNK") is { } c)
            OpenWestwood.Formats.Images.PngWriter.Save(c, Path.Combine(outDir, "htnkicon.png"));
    }
}

public class Ra2NameProbe(ITestOutputHelper output)
{
    [Fact]
    public void FindConyards()
    {
        if (Environment.GetEnvironmentVariable("RA2_DUMP") == null || OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        foreach (var p in art.Content.Files.Packages)
            foreach (var f in p.FilePaths.Where(f => f.Contains("CNST", StringComparison.OrdinalIgnoreCase) || f.Contains("POWR", StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"{p.Name}: {f}");
    }
}

public class InfantryAnimationProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void EveryInfantryStateHasItsAnimations()
    {
        if (!content.Available || content.Models == null) return;
        var game = GameSetup.FromMap(MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map")), content.Rules!, seed: 3,
            [new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF)], models: content.Models);
        var missing = new List<string>();
        foreach (var template in game.Templates.ObjectNames.Select(n => game.Templates.Find(n)).OfType<OpenWestwood.Logic.Templates.ObjectTemplate>().Where(t => t.Is("INFANTRY")))
            foreach (var module in template.ModulesOfType("Draw").Where(m => m.ClassName.Contains("ModelDraw")))
            {
                var data = OpenWestwood.Logic.Art.ModelDrawData.For(template, module);
                foreach (var state in data.States)
                    foreach (var anim in state.Animations.Concat(state.IdleAnimations))
                        if (content.Models.GetAnimation(anim.Name) == null)
                            missing.Add($"{template.Name} [{state.Name}] {anim.Name}");
                if (template.Name is "ChinaInfantryRedguard" or "AmericaInfantryRanger")
                    foreach (var state in data.States)
                        output.WriteLine($"{template.Name} [{state.Name}] anims {string.Join(",", state.Animations.Select(a => a.Name))} idle {string.Join(",", state.IdleAnimations.Select(a => a.Name))} mode {state.AnimationMode}");
            }
        output.WriteLine($"{missing.Count} missing: " + string.Join("; ", missing.Take(60)));
    }
}

public class InfantryDeathStateProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void DyingRangersPickAnimatedStates()
    {
        if (!content.Available) return;
        var game = GameSetup.FromMap(MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map")), content.Rules!, seed: 3,
            [new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF)], models: content.Models);
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var bad = new List<string>();
        foreach (string unit in new[] { "AmericaInfantryRanger", "ChinaInfantryRedguard", "GLAInfantryRebel" })
            foreach (string death in new[] { "NORMAL", "EXPLODED", "BURNED", "POISONED", "LASERED", "CRUSHED", "SUICIDED", "FLOODED" })
            {
                var at = mid + new System.Numerics.Vector3(Random.Shared.Next(-200, 200), Random.Shared.Next(-200, 200), 0);
                var obj = game.CreateObject(unit, game.Players.Find("Alice")!, at with { Z = game.Terrain.GroundHeight(at) })!;
                game.Update();
                obj.Kill(death);
                for (int f = 0; f < 15; f++)
                {
                    game.Update();
                    var module = obj.Template.ModulesOfType("Draw").First(m => m.ClassName.Contains("ModelDraw"));
                    var state = OpenWestwood.Logic.Art.ModelDrawData.For(obj.Template, module).StateFor(obj.ModelConditions);
                    if (f is 1 or 14)
                    {
                        string anim = state?.Animations.FirstOrDefault()?.Name ?? state?.IdleAnimations.FirstOrDefault()?.Name ?? "(none)";
                        output.WriteLine($"{unit} {death} f{f}: [{state?.Name}] {anim} conditions {obj.ModelConditions}");
                        if (state?.Name is not { } n || !n.Contains("DYING"))
                            bad.Add($"{unit} {death}: {state?.Name}");
                    }
                }
            }
        output.WriteLine("not dying: " + string.Join("; ", bad));
    }
}

public class FlungInfantryProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void FlungRangerComesDown()
    {
        if (!content.Available) return;
        var game = GameSetup.FromMap(MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map")), content.Rules!, seed: 3,
            [new PlayerSlot("Alice", "FactionAmerica", IsHuman: true, StartPosition: 1, Color: 0x0000FF)], models: content.Models);
        var at = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var obj = game.CreateObject("AmericaInfantryRanger", game.Players.Find("Alice")!, at with { Z = game.Terrain.GroundHeight(at) })!;
        game.Update();
        var start = obj.Position;
        obj.Kill("EXPLODED");
        for (int f = 0; f < 150; f++)
        {
            game.Update();
            if (f % 10 == 0)
                output.WriteLine($"f{f}: height {obj.HeightAboveTerrain:0.0}, moved {System.Numerics.Vector2.Distance(new(obj.Position.X, obj.Position.Y), new(start.X, start.Y)):0.0}, destroyed {obj.IsDestroyed}");
        }
    }
}

public class InfantryConditionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void FiringAndStoppingShowTheRightStates()
    {
        if (!content.Available) return;
        var game = GameSetup.FromMap(MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map")), content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionChina", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionAmerica", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models);
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var guard = game.CreateObject("ChinaInfantryRedguard", game.Players.Find("Alice")!, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        var targetAt = mid + new System.Numerics.Vector3(60, 0, 0);
        var target = game.CreateObject("AmericaVehicleHumvee", game.Players.Find("Bob")!, targetAt with { Z = game.Terrain.GroundHeight(targetAt) })!;
        guard.AI!.AttackObject(target);
        var draw = guard.Template.ModulesOfType("Draw").First(m => m.ClassName.Contains("ModelDraw"));
        var states = new HashSet<string>();
        for (int i = 0; i < 4 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            states.Add(OpenWestwood.Logic.Art.ModelDrawData.For(guard.Template, draw).StateFor(guard.ModelConditions)?.Name ?? "");
        }
        output.WriteLine("Red Guard states while attacking: " + string.Join(", ", states));
        Assert.Contains("USING_WEAPON_A", states);

        var ranger = game.CreateObject("AmericaInfantryRanger", game.Players.Find("Bob")!, (mid + new System.Numerics.Vector3(0, 200, 0)) with { Z = game.Terrain.GroundHeight(mid) })!;
        ranger.AI!.MoveTo(mid + new System.Numerics.Vector3(0, 400, 0));
        for (int i = 0; i < LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.True(ranger.HasCondition(OpenWestwood.Logic.Core.Condition.Moving));
        ranger.AI.Stop();
        for (int i = 0; i < LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.False(ranger.HasCondition(OpenWestwood.Logic.Core.Condition.Moving));
    }
}

public class RaInfantryAnimationTests
{
    [Fact]
    public void RedAlertRiflemanWalks()
    {
        if (OpenWestwood.Client.RedAlertContent.Types is not { } types) return;
        var e1 = types.FromName<OpenWestwood.TiberianDawn.Logic.Types.InfantryTypeClass>("E1")!;
        Assert.Equal((16, 6, 6), e1.DoControl(OpenWestwood.TiberianDawn.Logic.DoType.DO_WALK));
        Assert.Equal((64, 8, 8), e1.DoControl(OpenWestwood.TiberianDawn.Logic.DoType.DO_FIRE_WEAPON));
    }
}

public class RaWeapProbe(ITestOutputHelper output)
{
    [Fact]
    public void DumpWarFactory()
    {
        string outDir = Environment.GetEnvironmentVariable("RA_DUMP") ?? "";
        if (outDir.Length == 0 || OpenWestwood.Client.RedAlertContent.Art is not { } art) return;
        foreach (string name in new[] { "WEAP", "WEAP2", "WEAPMAKE", "FACT" })
        {
            if (art.Shape(name) is not { } shape) { output.WriteLine($"{name}: missing"); continue; }
            output.WriteLine($"{name}: {shape.FrameCount} frames {shape.Width}x{shape.Height}");
            for (int f = 0; f < Math.Min(shape.FrameCount, 12); f++)
                if (art.Frame(shape, f, (255, 0, 0)) is { } img)
                    OpenWestwood.Formats.Images.PngWriter.Save(img, Path.Combine(outDir, $"{name}_{f}.png"));
        }
    }
}

public class Ra2ArtDescribe(ITestOutputHelper output)
{
    [Fact]
    public void DescribeArt()
    {
        string names = Environment.GetEnvironmentVariable("RA2_ART") ?? "";
        if (names.Length == 0 || OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        foreach (string name in names.Split(','))
        {
            output.WriteLine($"[{name}] art: " + string.Join("; ", art.Art.Section(name).Select(kv => kv.Key + "=" + kv.Value)));
            output.WriteLine($"[{name}] rules: " + string.Join("; ", art.Rules.Section(name).Select(kv => kv.Key + "=" + kv.Value)));
            var shape = art.BuildingShape(name) ?? art.Shape(name);
            output.WriteLine($"[{name}] shape: {(shape == null ? "none" : $"{shape.FrameCount} frames {shape.Width}x{shape.Height}")}");
        }
    }
}

public class D2kSpiceProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void WhereIsTheSpice()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var map = OpenWestwood.Client.Dune2000.D2kMaps.Convert("d2k:TournamentSpice-ver3", null);
        output.WriteLine("with data: " + map.Objects.Count(o => o.Properties.ContainsKey("objectMapData")) + " of " + map.Objects.Count);
        var piles = map.Objects.Where(o => o.TemplateName.StartsWith("D2K_Spice") && char.IsDigit(o.TemplateName[9])).OrderBy(o => System.Numerics.Vector3.Distance(o.Position, new System.Numerics.Vector3(923, 340, 0))).ToList();
        output.WriteLine($"{piles.Count} piles; first: " + string.Join(" ", piles.Take(5).Select(p => $"({p.Position.X:0},{p.Position.Y:0}) [{p.Properties.GetValueOrDefault("objectMapData")}]")));
        var start = map.Objects.Where(o => o.IsWaypoint && o.Properties.GetValueOrDefault("waypointName") is string n && n.Contains("Start")).Select(o => $"({o.Position.X:0},{o.Position.Y:0})");
        output.WriteLine("starts " + string.Join(" ", start) + " keys: " + string.Join(",", piles[0].Properties.Keys) + " type " + piles[0].GetType().Assembly.Location);
        Assert.True(piles.Count > 0 && piles.All(p => p.Properties.ContainsKey("objectMapData")));
    }
}

public class Ra2SuperweaponTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("FactionRA2Russians")]
    [InlineData("FactionRA2Americans")]
    [InlineData("FactionRA2YuriCountry")]
    public void Ra2AiReachesItsSuperweapon(string faction)
    {
        if (!content.Available || OpenWestwood.Client.RedAlert2.Ra2Art.Get() == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "ra2:DRYHEAT.MAP");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", "FactionRAEngland", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", faction, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        int fired = 0, earned = 0, deliveries = 0;
        game.Events.OnMoneyEarned += (o, amount) => { if (o.Owner == bob) { earned += amount; deliveries++; } };
        var wasReady = new HashSet<OpenWestwood.Logic.Modules.SpecialPowers.SpecialPowerModule>();
        for (int i = 0; i < 12 * 60 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i >= 500 * LogicTime.FramesPerSecond && i < 500 * LogicTime.FramesPerSecond + 20 && Environment.GetEnvironmentVariable("TRACE_HARV") != null)
                foreach (var h in game.ObjectsOwnedBy(bob).Where(o => o.Template.Name.EndsWith("HARV")).Take(1))
                    output.WriteLine($"f{i}: {h.Position.X:0.0},{h.Position.Y:0.0} {h.AI?.State} {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(h)?.State} boxes {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(h)?.Boxes} stuck {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(h)?.StuckFramesSoFar} {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(h)?.ResetReason}");
            if (i % (20 * LogicTime.FramesPerSecond) == 0 && Environment.GetEnvironmentVariable("TRACE_HARV") != null)
                foreach (var h in game.ObjectsOwnedBy(bob).Where(o => o.Template.Name.EndsWith("HARV") || o.Template.Name.EndsWith("CMIN")).Take(1))
                {
                    var g = OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(h);
                    output.WriteLine($"t{i / LogicTime.FramesPerSecond}: {h.Position.X:0},{h.Position.Y:0} {h.AI?.State} gatherer {g?.State} boxes {g?.Boxes} goal {h.AI?.Destination.X:0},{h.AI?.Destination.Y:0} speed {h.FindModule<OpenWestwood.Logic.Modules.Updates.PhysicsBehavior>()?.VelocityMagnitude:0.00} stuck {g?.StuckFramesSoFar} giveups {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.GiveUps} dock {h.AI?.DockTargetId}");
                }
            if (i % 15 != 0) continue;
            foreach (var p in game.ObjectsOwnedBy(bob).SelectMany(o => o.FindModules<OpenWestwood.Logic.Modules.SpecialPowers.SpecialPowerModule>()))
            {
                if (p.IsReady) wasReady.Add(p);
                else if (wasReady.Remove(p)) fired++;
            }
        }
        var mine = game.ObjectsOwnedBy(bob).Where(o => !o.IsEffectivelyDead).ToList();
        var powers = mine.SelectMany(o => o.FindModules<OpenWestwood.Logic.Modules.SpecialPowers.SpecialPowerModule>()).ToList();
        output.WriteLine($"{faction}: {mine.Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure))} buildings, {mine.Count} objects, ${bob.Money.Amount}, powers: {string.Join(", ", powers.Select(p => p.Power?.Name))}, fired {fired}, earned ${earned} in {deliveries} deliveries");
        output.WriteLine(string.Join(", ", mine.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        foreach (var yard in mine.Where(o => o.FindModule<OpenWestwood.Logic.Modules.Production.ProductionUpdate>() is { PlacesStructures: true }))
        {
            var pu = yard.FindModule<OpenWestwood.Logic.Modules.Production.ProductionUpdate>()!;
            output.WriteLine($"yard queue: {string.Join(", ", pu.Queue.Select(q => $"{q.Unit?.Name} {q.Progress:0.00}"))}; power {bob.Energy.Production}/{bob.Energy.Consumption}");
            foreach (string t in new[] { "RA2YUR_NAPSIS", "RA2YUR_YAPOWR", "RA2ALL_GATECH", "RA2SOV_NATECH" })
                if (game.Templates.Find(t) is { } tt && tt.Rules.GetString("Side") == bob.Side)
                    output.WriteLine($"  {t}: {pu.CanQueueUnit(tt) ?? "ok"} canBuild {bob.CanBuild(tt)}");
        }
        Assert.NotEmpty(powers);
    }
}

public class Ra2SlaveTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void YuriRefineryKeepsItsSlavesMining()
    {
        if (!content.Available || OpenWestwood.Client.RedAlert2.Ra2Art.Get() == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "ra:SCM01EA");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", "FactionRAEngland", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionRA2YuriCountry", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        var ore = game.Objects.Where(o => o.Template.Name.StartsWith("RA_Ore")).OrderBy(o => MathUtil.Distance2D(o.Position, bob.StartPosition ?? default)).First();
        var spot = game.Pathfinder.NearestPassable(ore.Position + new System.Numerics.Vector3(80, 0, 0), OpenWestwood.Logic.Locomotion.Surfaces.Ground, 4) ?? ore.Position;
        var refinery = game.CreateObject("RA2YUR_YAREFN", bob, spot)!;
        game.TreatAsBuilt(refinery);
        int start = bob.Money.Amount;
        for (int i = 0; i < 90 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (15 * LogicTime.FramesPerSecond) == 0)
                foreach (var s in game.ObjectsOwnedBy(bob).Where(o => o.Template.Name == "RA2YUR_SLAV").Take(2))
                    output.WriteLine($"t{i / LogicTime.FramesPerSecond}: slave {s.Id} at {s.Position.X:0},{s.Position.Y:0} state {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(s)?.State} boxes {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(s)?.Boxes} goal {s.AI?.Destination.X:0},{s.AI?.Destination.Y:0}; refinery {refinery.Position.X:0},{refinery.Position.Y:0}; ore {ore.Position.X:0},{ore.Position.Y:0}");
        }
        var slaves = game.ObjectsOwnedBy(bob).Where(o => o.Template.Name == "RA2YUR_SLAV" && !o.IsEffectivelyDead).ToList();
        output.WriteLine($"{slaves.Count} slaves; money {start} -> {bob.Money.Amount}; slave states: {string.Join(", ", slaves.Select(s => s.AI?.State))}");
        Assert.True(slaves.Count >= 3);
        Assert.True(bob.Money.Amount > start);
    }
}

public class Ra2PowerTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    GameLogic Match(string alice = "FactionRA2Americans", string bob = "FactionRA2Russians")
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", alice, IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", bob, IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    static System.Numerics.Vector3 At(GameLogic game, float dx, float dy)
    {
        var p = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2 + dx, game.Terrain.WorldHeight / 2 + dy, 0);
        return p with { Z = game.Terrain.GroundHeight(p) };
    }

    static T Power<T>(GameLogic game, string building, string owner, string templateEnd) where T : Logic.Modules.SpecialPowers.SpecialPowerModule
    {
        var source = game.CreateObject(building, game.Players.Find(owner)!, At(game, -500, 300))!;
        game.TreatAsBuilt(source);
        var module = source.FindModules<T>().First(m => m.Power!.Name.Contains(templateEnd));
        module.SetReadyFrame(0);
        return module;
    }

    OpenWestwood.Logic.Objects.GameObject Make(GameLogic game, string name, string owner, float dx, float dy)
    {
        var o = game.CreateObject(name, game.Players.Find(owner)!, At(game, dx, dy))!;
        if (o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure)) game.TreatAsBuilt(o);
        return o;
    }

    void Run(GameLogic game, float seconds)
    {
        for (int i = 0; i < seconds * LogicTime.FramesPerSecond; i++)
            game.Update();
    }

    bool Skip => !content.Available || content.Rules!.Section("Objects")["RA2SOV_NAMISL"] == null;

    [Fact]
    public void NukeAndLightningStormHurtBuildings()
    {
        if (Skip) return;
        var game = Match();
        var plant = Make(game, "RA2ALL_GAPOWR", "Alice", 200, 0);
        // A storm's bolts land anywhere in its ten cells: a field of plants under it.
        var stormed = Enumerable.Range(0, 9).Select(i => Make(game, "RA2SOV_NAPOWR", "Bob", 100 + i % 3 * 110, -500 + i / 3 * 110)).ToList();
        Power<Logic.Modules.SpecialPowers.OCLSpecialPower>(game, "RA2SOV_NAMISL", "Bob", "Nuke").DoSpecialPowerAtLocation(plant.Position, float.NaN, 0);
        Power<Logic.Modules.Behaviors.Ra2Superweapon>(game, "RA2ALL_GAWEAT", "Alice", "Lightning").DoSpecialPowerAtLocation(stormed[4].Position, float.NaN, 0);
        Run(game, 40);
        int hit = stormed.Count(p => p.IsEffectivelyDead || p.Health < p.MaxHealth);
        output.WriteLine($"nuked plant {plant.Health}/{plant.MaxHealth}; {hit} of 9 plants struck by the storm");
        Assert.True(plant.IsEffectivelyDead || plant.Health < plant.MaxHealth);
        Assert.True(hit > 0);
    }

    [Fact]
    public void IronCurtainChronosphereDominatorMutator()
    {
        if (Skip) return;
        var game = Match("FactionRA2Americans", "FactionRA2YuriCountry");
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        // The Iron Curtain (taken by Bob for the test? no: a Soviet power) on Alice's tank: can't be hurt.
        var tank = Make(game, "RA2ALL_MTNK", "Alice", 0, 0);
        var curtain = game.CreateObject("RA2SOV_NAIRON", alice, At(game, -500, -300))!;
        game.TreatAsBuilt(curtain);
        var ic = curtain.FindModule<Logic.Modules.Behaviors.Ra2Superweapon>()!;
        ic.SetReadyFrame(0);
        ic.DoSpecialPowerAtLocation(tank.Position, float.NaN, 0);
        Assert.True(tank.Body!.Indestructible);
        // The Chronosphere: Alice's tank to somewhere else.
        var sphere = Power<Logic.Modules.Behaviors.Ra2Superweapon>(game, "RA2ALL_GACSPH", "Alice", "Chrono");
        var from = tank.Position;
        sphere.DoSpecialPowerAtLocation(from, float.NaN, 0);
        sphere.DoSpecialPowerAtLocation(At(game, 300, 300), float.NaN, 0);
        output.WriteLine($"tank {from} -> {tank.Position}");
        Assert.True(OpenWestwood.Logic.Core.MathUtil.Distance2D(tank.Position, At(game, 300, 300)) < 60);
        // The Dominator: Alice's tank is Bob's.
        Power<Logic.Modules.Behaviors.Ra2Superweapon>(game, "RA2YUR_YAPPET", "Bob", "Dominator").DoSpecialPowerAtLocation(tank.Position, float.NaN, 0);
        Assert.Equal(bob, tank.Owner);
        // The Mutator: Alice's GIs become Bob's Brutes.
        var gis = Enumerable.Range(0, 3).Select(i => Make(game, "RA2ALL_E1", "Alice", -200 + i * 12, -200)).ToList();
        int brutes = game.ObjectsOwnedBy(bob).Count(o => o.Template.Name == "RA2YUR_BRUTE");
        Power<Logic.Modules.Behaviors.Ra2Superweapon>(game, "RA2YUR_YAGNTC", "Bob", "Genetic").DoSpecialPowerAtLocation(gis[0].Position, float.NaN, 0);
        Run(game, 1);
        int brutesAfter = game.ObjectsOwnedBy(bob).Count(o => o.Template.Name == "RA2YUR_BRUTE");
        output.WriteLine($"brutes {brutes} -> {brutesAfter}");
        Assert.Equal(brutes + 3, brutesAfter);
    }

    [Fact]
    public void SpyStealsTechAndMoney()
    {
        if (Skip) return;
        var game = Match();
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var lab = Make(game, "RA2SOV_NATECH", "Bob", 0, 0);
        var refinery = Make(game, "RA2SOV_NAREFN", "Bob", 0, 250);
        int aliceMoney = alice.Money.Amount, bobMoney = bob.Money.Amount;
        foreach (var target in new[] { lab, refinery })
        {
            var spy = Make(game, "RA2ALL_SPY", "Alice", 150, target.Position.Y - game.Terrain.WorldHeight / 2);
            spy.FindModule<Logic.Modules.Behaviors.TdInfiltrate>()!.Order(target);
            Run(game, 20);
        }
        output.WriteLine($"stolen tech {alice.HasUpgrade("Upgrade_RA2StolenSovietTech")}; Alice ${aliceMoney} -> {alice.Money.Amount}, Bob ${bobMoney} -> {bob.Money.Amount}");
        Assert.True(alice.HasUpgrade("Upgrade_RA2StolenSovietTech"));
        Assert.True(alice.Money.Amount > aliceMoney && bob.Money.Amount < bobMoney);
    }
}

public class Ra2AudioTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Ra2SoundsPlayFromTheAudioBag()
    {
        if (!content.Available || OpenWestwood.Client.RedAlert2.Ra2Art.Get() == null) return;
        var sounds = content.FileSystem!.Packages.SelectMany(p => p.FilePaths).Where(p => p.StartsWith("ra2sfx/", StringComparison.OrdinalIgnoreCase)).ToList();
        output.WriteLine($"{sounds.Count} RA2 sounds, e.g. {string.Join(" ", sounds.Take(8))}");
        Assert.True(sounds.Count > 1000);
        int decoded = 0;
        foreach (string s in sounds.Take(40))
        {
            var pcm = OpenWestwood.Formats.Audio.WavFile.Decode(content.FileSystem.ReadAllBytes(s));
            if (pcm.Samples.Length > 0) decoded++;
        }
        output.WriteLine($"decoded {decoded} of 40");
        Assert.True(decoded >= 38);
        // Every sound the RA2 rules name is there.
        string root = OpenWestwood.Client.GameContent.FindRoot();
        var named = new[] { "sound-effects.yaml", "eva.yaml" }.SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(root, "mods", "zh", "rules", "ra2", f)), @"/RA2SFX/\S+").Select(m => m.Value[1..])).Distinct().ToList();
        var missing = named.Where(n => !content.FileSystem.Exists(n)).ToList();
        output.WriteLine($"{named.Count} named sounds, {missing.Count} missing: {string.Join(" ", missing.Take(20))}");
        Assert.True(missing.Count <= named.Count / 20);
    }
}

public class Ra2EvaDump(ITestOutputHelper output)
{
    [Fact]
    public void ListEvaEntries()
    {
        if (Environment.GetEnvironmentVariable("RA2_EVA") == null || OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        var eva = new OpenWestwood.Formats.Westwood.Ra2Ini();
        foreach (string f in new[] { "EVA.INI", "EVAMD.INI" })
            if (art.Content.Files.Exists(f))
                eva.Read(System.Text.Encoding.Latin1.GetString(art.Content.Files.ReadAllBytes(f)));
        foreach (string s in eva.Sections.Where(s => s.StartsWith("EVA_")))
            output.WriteLine($"{s}: {string.Join("; ", eva.Section(s).Select(kv => kv.Key + "=" + kv.Value))}");
    }
}

public class Ra2BagProbe(ITestOutputHelper output)
{
    [Fact]
    public void WhereIsAudioBag()
    {
        if (OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        foreach (var p in art.Content.Files.Packages)
            foreach (string f in new[] { "AUDIO.BAG", "AUDIO.IDX", "AUDIO.MIX", "AUDIOMD.BAG" })
                if (p.Contains(f)) output.WriteLine($"{f} in {p.Name}");
    }
}

public class Ra2EvaMixProbe(ITestOutputHelper output)
{
    [Fact]
    public void WhereIsEvaMix()
    {
        if (OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        foreach (var p in art.Content.Files.Packages)
            foreach (string f in new[] { "EVA.MIX", "EVAMD.MIX", "CEVA001.WAV", "CEVA053.WAV" })
                if (p.Contains(f)) output.WriteLine($"{f} in {p.Name}");
    }
}

public class Ra2MapProbe(ITestOutputHelper output)
{
    [Fact]
    public void ListRa2Maps()
    {
        if (OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        foreach (var p in art.Content.Files.Packages)
        {
            var maps = p.FilePaths.Where(f => f.EndsWith(".map", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mmx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yrm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yro", StringComparison.OrdinalIgnoreCase)).ToList();
            if (maps.Count > 0) output.WriteLine($"{p.Name}: {maps.Count}: {string.Join(" ", maps.Take(30))}");
        }
    }
}

public class Ra2MapParseProbe(ITestOutputHelper output)
{
    [Fact]
    public void ParseAMap()
    {
        string name = Environment.GetEnvironmentVariable("RA2_MAP") ?? "";
        if (name.Length == 0 || OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        var files = art.Content.Files;
        var map = new OpenWestwood.Formats.Westwood.IsoMap(System.Text.Encoding.Latin1.GetString(files.ReadAllBytes(name)));
        output.WriteLine($"{name}: theater {map.Theater} size {map.Width}x{map.Height} local {map.Local} cells {map.Cells.Count} waypoints {string.Join(" ", map.Waypoints.OrderBy(w => w.Key).Take(10).Select(w => $"{w.Key}:{w.Value}"))}");
        output.WriteLine($"tiles used: {string.Join(" ", map.Cells.GroupBy(c => c.Tile).OrderByDescending(g => g.Count()).Take(15).Select(g => $"{g.Key}x{g.Count()}"))}; levels {map.Cells.Min(c => c.Level)}-{map.Cells.Max(c => c.Level)}");
        output.WriteLine($"overlay used: {string.Join(" ", map.Overlay.Where(o => o != 0xFF).GroupBy(o => o).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key}x{g.Count()}"))}; terrain {map.Terrain.Count} structures {map.Structures.Count}: {string.Join(" ", map.Structures.Select(s => s.Type).Distinct().Take(15))}");
        string theaterIni = map.Theater switch { "SNOW" => "SNOWMD.INI", "URBAN" => "URBANMD.INI", "NEWURBAN" => "URBANNMD.INI", "DESERT" => "DESERTMD.INI", "LUNAR" => "LUNARMD.INI", _ => "TEMPERATMD.INI" };
        var ini = new OpenWestwood.Formats.Westwood.Ra2Ini();
        ini.Read(System.Text.Encoding.Latin1.GetString(files.ReadAllBytes(theaterIni)));
        int index = 0;
        var sets = new List<string>();
        for (int i = 0; ini.Has($"TileSet{i:0000}"); i++)
        {
            string s = $"TileSet{i:0000}";
            int count = (int)ini.Num(s, "TilesInSet");
            sets.Add($"{index}:{ini.Get(s, "SetName")}[{ini.Get(s, "FileName")}x{count}]");
            index += count;
        }
        output.WriteLine(string.Join(" | ", sets));
        var rules = new OpenWestwood.Formats.Westwood.Ra2Ini();
        rules.Read(System.Text.Encoding.Latin1.GetString(files.ReadAllBytes("RULESMD.INI")));
        var overlays = rules.Values("OverlayTypes").ToList();
        output.WriteLine("overlay types: " + string.Join(" ", overlays.Select((o, i) => $"{i}:{o}").Take(140)));
        var tem = new OpenWestwood.Formats.Westwood.IsoTemplate(files.ReadAllBytes("CLEAR01.TEM"));
        output.WriteLine($"clear01.tem {tem.CellsX}x{tem.CellsY} tiles {tem.TileWidth}x{tem.TileHeight}; first tile terrain {tem.Tiles[0]?.TerrainType} height {tem.Tiles[0]?.Height}");
    }
}

public class Ra2MapTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Ra2MapConvertsAndPlays()
    {
        if (!content.Available || OpenWestwood.Client.RedAlert2.Ra2Art.Get() == null) return;
        string name = Environment.GetEnvironmentVariable("RA2_MAP") ?? "DRYHEAT.MAP";
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "ra2:" + name);
        string dump = Environment.GetEnvironmentVariable("RA_DUMP") ?? "";
        if (dump.Length > 0)
            OpenWestwood.Formats.Images.PngWriter.Save(map.GroundImage!, Path.Combine(dump, name + ".png"));
        var starts = map.Objects.Where(o => o.IsWaypoint).Select(o => $"({o.Position.X:0},{o.Position.Y:0})");
        output.WriteLine($"{name}: ground {map.GroundImage!.Width}x{map.GroundImage.Height}, heights {map.HeightMap.Width}x{map.HeightMap.Height} max {map.HeightMap.Heights.Max()}, objects {map.Objects.Count}: {string.Join(" ", map.Objects.GroupBy(o => o.TemplateName.Split('_')[0] + "_" + (o.TemplateName.Split('_').ElementAtOrDefault(1) ?? "")).Select(g => $"{g.Key}x{g.Count()}").Take(12))}; starts {string.Join(" ", starts)}; water {map.PolygonTriggers.Count}");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 5,
        [
            new PlayerSlot("Carol", "FactionRA2YuriCountry", IsHuman: true, StartPosition: 3, Color: 0x00FF00),
            new PlayerSlot("Alice", "FactionRA2Americans", IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionRA2Russians", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        int missing = map.Objects.Count(o => !o.IsWaypoint && content.Rules!.Section("Objects")[o.TemplateName] == null);
        output.WriteLine($"objects without a template: {missing}: {string.Join(" ", map.Objects.Where(o => !o.IsWaypoint && content.Rules!.Section("Objects")[o.TemplateName] == null).Select(o => o.TemplateName).Distinct().Take(10))}");
        var mcv = game.ObjectsOwnedBy(game.Players.Find("Alice")!).First();
        var yardT = game.Templates.Find("RA2ALL_GACNST")!;
        output.WriteLine($"Alice's MCV deploy check: {game.Building.CheckLocation(yardT, mcv.Position, 0, mcv.Owner, mcv)}; height {game.Terrain.GroundHeight(mcv.Position)}");
        for (int i = 0; i < 180 * LogicTime.FramesPerSecond; i++)
            game.Update();
        foreach (var p in new[] { "Alice", "Bob" })
        {
            var pl = game.Players.Find(p)!;
            output.WriteLine($"{p}: {game.ObjectsOwnedBy(pl).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure) && !o.IsEffectivelyDead)} buildings, ${pl.Money.Amount}; " + string.Join(", ", game.ObjectsOwnedBy(pl).Take(5).Select(o => $"{o.Template.Name}@{o.Position.X:0},{o.Position.Y:0} {o.AI?.State}")));
        }
        Assert.True(game.ObjectsOwnedBy(game.Players.Find("Bob")!).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure)) >= 3);
        Assert.True(game.ObjectsOwnedBy(game.Players.Find("Alice")!).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure)) >= 3);
    }
}

public class Ra2TileProbe(ITestOutputHelper output)
{
    [Fact]
    public void MissingTiles()
    {
        string name = Environment.GetEnvironmentVariable("RA2_MAP") ?? "";
        if (name.Length == 0 || OpenWestwood.Client.RedAlert2.Ra2Art.Get() is not { } art) return;
        var files = art.Content.Files;
        var map = new OpenWestwood.Formats.Westwood.IsoMap(System.Text.Encoding.Latin1.GetString(files.ReadAllBytes(name)));
        var th = OpenWestwood.Client.RedAlert2.Ra2Art.TheaterOf(map.Theater);
        var ini = new OpenWestwood.Formats.Westwood.Ra2Ini();
        ini.Read(System.Text.Encoding.Latin1.GetString(files.ReadAllBytes(th.Ini)));
        var tiles = new List<string>();
        for (int i = 0; ini.Has($"TileSet{i:0000}"); i++)
            for (int k = 1; k <= (int)ini.Num($"TileSet{i:0000}", "TilesInSet"); k++)
                tiles.Add($"{ini.Get($"TileSet{i:0000}", "FileName")}{k:00}.{th.Extension}");
        var bad = new Dictionary<string, int>();
        foreach (var c in map.Cells)
        {
            int t = c.Tile == 0xFFFF ? 0 : c.Tile;
            string file = t < tiles.Count ? tiles[t] : $"#{t}";
            string why;
            if (t >= tiles.Count) why = "index";
            else if (!files.Exists(file)) why = "nofile";
            else
            {
                var tem = new OpenWestwood.Formats.Westwood.IsoTemplate(files.ReadAllBytes(file));
                why = c.SubTile >= tem.Tiles.Length ? $"sub{c.SubTile}/{tem.Tiles.Length}" : tem.Tiles[c.SubTile] == null ? "nulltile" : "";
            }
            if (why.Length > 0) bad[$"{file}:{why}"] = bad.GetValueOrDefault($"{file}:{why}") + 1;
        }
        output.WriteLine($"{tiles.Count} tiles; bad: " + string.Join(" ", bad.OrderByDescending(b => b.Value).Take(30).Select(b => $"{b.Key}x{b.Value}")));
    }
}

public class TsMixCensus(ITestOutputHelper output)
{
    [Fact]
    public void ListEveryTiberianSunMix()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        if (!File.Exists(Path.Combine(root, "content", "tiberiansun", "TIBSUN.MIX"))) return;
        using var ts = OpenWestwood.TiberianDawn.TdContent.Load(root, "tiberiansun");
        foreach (var p in ts.Files.Packages)
        {
            var paths = p.FilePaths.ToList();
            output.WriteLine($"{p.Name}: {paths.Count} files, {paths.Count(f => f.EndsWith(".unknown"))} unknown; mixes: {string.Join(" ", paths.Where(f => f.EndsWith(".MIX", StringComparison.OrdinalIgnoreCase)))}; ini: {string.Join(" ", paths.Where(f => f.EndsWith(".INI", StringComparison.OrdinalIgnoreCase)))}");
        }
        string dump = Environment.GetEnvironmentVariable("TS_DUMP") ?? "";
        if (dump.Length > 0)
            foreach (var f in ts.Files.Packages.SelectMany(p => p.FilePaths).Where(f => f.EndsWith(".INI", StringComparison.OrdinalIgnoreCase)).Distinct())
                File.WriteAllBytes(Path.Combine(dump, f.Replace('/', '_')), ts.Files.ReadAllBytes(f));
    }
}

public class TsMapProbe(ITestOutputHelper output)
{
    [Fact]
    public void FindMultiplayerMaps()
    {
        string root = OpenWestwood.Client.GameContent.FindRoot();
        if (!File.Exists(Path.Combine(root, "content", "tiberiansun", "TIBSUN.MIX"))) return;
        using var ts = OpenWestwood.TiberianDawn.TdContent.Load(root, "tiberiansun");
        var multi = ts.Files.Packages.First(p => p.Name.StartsWith("MULTI"));
        foreach (string f in multi.FilePaths)
        {
            var bytes = ts.Files.ReadAllBytes(f);
            string text = System.Text.Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4000));
            var name = System.Text.RegularExpressions.Regex.Match(text, @"\nName=([^\r\n]*)");
            var players = System.Text.RegularExpressions.Regex.Match(System.Text.Encoding.Latin1.GetString(bytes), @"MaxPlayer=(\d+)");
            output.WriteLine($"{f} {bytes.Length}: {(text.StartsWith("[") || text.Contains("[Basic]") ? "INI" : "bin")} {name.Groups[1].Value} {players.Groups[1].Value}");
        }
    }
}

public class TsMapTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void TsMapConvertsAndPlays()
    {
        if (!content.Available || OpenWestwood.Client.RedAlert2.Ra2Art.GetTiberianSun() == null) return;
        string name = Environment.GetEnvironmentVariable("TS_MAP") ?? "DUEL.MAP";
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "ts:" + name);
        string dump = Environment.GetEnvironmentVariable("RA_DUMP") ?? "";
        if (dump.Length > 0)
            OpenWestwood.Formats.Images.PngWriter.Save(map.GroundImage!, Path.Combine(dump, "TS_" + name + ".png"));
        output.WriteLine("starts " + string.Join(" ", map.Objects.Where(o => o.IsWaypoint).Select(o => $"({o.Position.X:0},{o.Position.Y:0})")));
        output.WriteLine($"{name}: ground {map.GroundImage!.Width}x{map.GroundImage.Height}, objects {map.Objects.Count}: {string.Join(" ", map.Objects.GroupBy(o => o.TemplateName.Length > 9 ? o.TemplateName[..9] : o.TemplateName).Select(g => $"{g.Key}x{g.Count()}").Take(12))}; water {map.PolygonTriggers.Count}");
        int missing = map.Objects.Count(o => !o.IsWaypoint && content.Rules!.Section("Objects")[o.TemplateName] == null);
        output.WriteLine($"objects without a template: {missing}: {string.Join(" ", map.Objects.Where(o => !o.IsWaypoint && content.Rules!.Section("Objects")[o.TemplateName] == null).Select(o => o.TemplateName).Distinct().Take(10))}");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 5,
        [
            new PlayerSlot("Carol", "FactionTSGDI", IsHuman: true, StartPosition: 3, Color: 0x00FF00),
            new PlayerSlot("Alice", "FactionTSGDI", IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionTSNod", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        for (int i = 0; i < 180 * LogicTime.FramesPerSecond; i++)
            game.Update();
        foreach (var p in new[] { "Alice", "Bob" })
        {
            var pl = game.Players.Find(p)!;
            output.WriteLine($"{p}: {game.ObjectsOwnedBy(pl).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure) && !o.IsEffectivelyDead)} buildings, ${pl.Money.Amount}; " + string.Join(", ", game.ObjectsOwnedBy(pl).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}").Take(10)));
        }
        Assert.Equal(0, missing);
        Assert.True(game.ObjectsOwnedBy(game.Players.Find("Bob")!).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure)) >= 3);
        Assert.True(game.ObjectsOwnedBy(game.Players.Find("Alice")!).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure)) >= 3);
    }
}

public class TsAudioTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void TsSoundsAreThere()
    {
        if (!content.Available || OpenWestwood.Client.RedAlert2.Ra2Art.GetTiberianSun() == null) return;
        string root = OpenWestwood.Client.GameContent.FindRoot();
        var named = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(root, "mods", "zh", "rules", "ts", "sound-effects.yaml")), @"/TSSFX/\S+").Select(m => m.Value[1..]).Distinct().ToList();
        var missing = named.Where(n => !content.FileSystem!.Exists(n)).ToList();
        int decoded = 0;
        foreach (string s in named.Except(missing).Take(30))
            if (OpenWestwood.Formats.Audio.AudFile.Decode(content.FileSystem!.ReadAllBytes(s)).Samples.Length > 0) decoded++;
        output.WriteLine($"{named.Count} named TS sounds, {missing.Count} missing ({string.Join(" ", missing.Take(15))}); decoded {decoded} of {Math.Min(30, named.Count - missing.Count)}");
        Assert.True(missing.Count <= named.Count / 10);
        Assert.True(decoded >= Math.Min(30, named.Count - missing.Count) - 2);
    }
}

public class ManyStartsTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    /// <summary>A map with more start points than slots (Dune 2000's Stone Plateaus has 12): random starts still work.</summary>
    [Fact]
    public void SixRandomStartsOnATwelveSpawnMap()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var cache = Logic.Multiplayer.MapCache.Load(content.FileSystem!);
        OpenWestwood.Client.Dune2000.D2kMaps.AddTo(cache);
        var templates = Logic.Multiplayer.GameInfo.PlayerTemplates(content.Rules!);
        int t(string name) => templates.First(x => x.Name == name).Index;
        var info = new Logic.Multiplayer.GameInfo { Map = Logic.Multiplayer.MapCache.Key("d2k:stone-plateaus"), Seed = 9, StartingCash = 50000 };
        string[] factions = ["FactionD2KAtreides", "FactionD2KHarkonnen", "FactionD2KOrdos", "FactionRA2Russians", "FactionTSGDI", "FactionTSNod"];
        for (int i = 0; i < factions.Length; i++)
        {
            info.Slots[i].SetState(i == 0 ? Logic.Multiplayer.SlotState.Player : Logic.Multiplayer.SlotState.EasyAI, $"P{i}");
            info.Slots[i].PlayerTemplate = templates.Any(x => x.Name == factions[i]) ? t(factions[i]) : t("FactionD2KAtreides");
            info.Slots[i].StartPos = i < 3 ? -1 : i;
        }
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "d2k:stone-plateaus");
        var game = GameSetup.FromGameInfo(map, content.Rules!, info, cache, content.Models,
            p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        output.WriteLine(string.Join(", ", info.Slots.Where(s => s.IsOccupied).Select(s => s.StartPos)));
        Assert.Equal(6, game.Players.Active.Count(p => !p.IsNeutral));
        Assert.All(info.Slots.Where(s => s.IsOccupied), s => Assert.InRange(s.StartPos, 0, 7));
    }
}

public class DuneShellTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void ShellBattleHasBasesAndArmiesFighting()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (game, _) = OpenWestwood.Client.Shell.DuneShellBattle.Create(content.Rules!, content.FileSystem!, content.Models, seed: 1)!.Value;
        output.WriteLine($"set up and played ahead in {watch.ElapsedMilliseconds} ms");
        foreach (var p in game.Players.Active.Where(p => p.AI != null))
            output.WriteLine($"{p.Name} {p.FactionName}: {game.ObjectsOwnedBy(p).Count(o => o.IsKindOf(OpenWestwood.Logic.Core.Kind.Structure) && !o.IsEffectivelyDead)} buildings, {game.ObjectsOwnedBy(p).Count(o => o.IsMobile && !o.IsEffectivelyDead)} units");
        int fights = 0, idleCarryallSamples = 0, idleCarryallMoving = 0;
        // (The battle is played only a minute ahead, so the menus show it at once: the bases grow and the fighting starts
        // while the player looks at the menus.)
        for (int i = 0; i < 150 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % 15 == 0)
                foreach (var c in game.Objects.Where(o => o.FindModule<OpenWestwood.Logic.Modules.Behaviors.D2kCarryall>() is { IsCarrying: false, Cargo: null } && !o.IsEffectivelyDead))
                {
                    idleCarryallSamples++;
                    if (c.AI is { IsMoving: true }) idleCarryallMoving++;
                    else if (idleCarryallSamples - idleCarryallMoving < 8) output.WriteLine($"still carryall {c.Template.Name} at {c.Position.X:0},{c.Position.Y:0},{c.Position.Z:0} state {c.AI?.State} carryall {c.FindModule<OpenWestwood.Logic.Modules.Behaviors.D2kCarryall>()!.State} disabled {c.IsDisabled} dest {c.AI?.Destination.X:0},{c.AI?.Destination.Y:0}");
                }
            if (i % LogicTime.FramesPerSecond == 0 && OpenWestwood.Client.Shell.DuneShellBattle.Fighting(game) != null)
                fights++;
        }
        output.WriteLine($"seconds with fighting in the next 150: {fights}; houses left {OpenWestwood.Client.Shell.DuneShellBattle.HousesLeft(game)}; idle carryalls flying {idleCarryallMoving} of {idleCarryallSamples} looks");
        Assert.True(idleCarryallSamples == 0 || idleCarryallMoving >= idleCarryallSamples * 0.8);
        // Units stacked on one another (closer than a third of their sizes): ten units looking like one.
        var units = game.Objects.Where(o => o.IsMobile && !o.IsEffectivelyDead && !o.IsKindOf(OpenWestwood.Logic.Core.Kind.Aircraft) && !o.IsContained && !o.Owner.IsNeutral).ToList();
        int stacked = 0;
        for (int a = 0; a < units.Count; a++)
            for (int b2 = a + 1; b2 < units.Count; b2++)
                if (units[a].Owner == units[b2].Owner && OpenWestwood.Logic.Core.MathUtil.Distance2D(units[a].Position, units[b2].Position) < (units[a].Radius + units[b2].Radius) / 3)
                    stacked++;
        output.WriteLine($"{units.Count} units, {stacked} stacked pairs");
        var stackedUnits = units.Where(u => units.Any(v => v != u && v.Owner == u.Owner && OpenWestwood.Logic.Core.MathUtil.Distance2D(u.Position, v.Position) < (u.Radius + v.Radius) / 3)).ToList();
        output.WriteLine("stacked by kind: " + string.Join(", ", stackedUnits.GroupBy(u => $"{u.Template.Name}/{u.AI?.State}").OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key} x{g.Count()}")));
        output.WriteLine("example spots: " + string.Join(" ", stackedUnits.GroupBy(u => ((int)u.Position.X / 20, (int)u.Position.Y / 20)).OrderByDescending(g => g.Count()).Take(5).Select(g => $"({g.First().Position.X:0},{g.First().Position.Y:0})x{g.Count()} r{g.First().Radius:0.0}")));
        Assert.True(stacked <= units.Count / 10);
        Assert.True(game.Players.Active.Count(p => p.AI != null) >= 4);
        Assert.True(fights >= 10);
        // The theme: the track's file is there and is MP3 the music stream can read.
        string file = content.Rules!.Section("MusicTracks")["ShellDune"]!.GetString("Filename").TrimStart('/');
        Assert.True(content.FileSystem!.Exists(file));
        using var mp3 = new NLayer.MpegFile(new MemoryStream(content.FileSystem.ReadAllBytes(file)));
        output.WriteLine($"theme: {file}, {mp3.Duration.TotalSeconds:0} s at {mp3.SampleRate} Hz");
        Assert.True(mp3.Duration.TotalSeconds > 60);
    }
}

public class D2kSpiceFrameProbe(ITestOutputHelper output)
{
    [Fact]
    public void SpiceFramesAreNotBlack()
    {
        if (OpenWestwood.Client.Dune2000.D2kArt.Get() is not { } d2k) return;
        var bad = new List<string>();
        foreach (var (image, look) in new[] { ("resources", "spicea"), ("resources", "spiceb"), ("resources", "spicec"), ("resources", "spiced"), ("sandcraters", "sandcrater1"), ("sandcraters", "sandcrater2"), ("rockcraters", "rockcrater1"), ("rockcraters", "rockcrater2"), ("concretea", "preview") })
        {
            if (d2k.Sequence(image, look) is not { } seq) { bad.Add($"{look} missing"); continue; }
            var file = d2k.SpriteFile(seq.File)!;
            for (int i = 0; i < seq.Length; i++)
            {
                int frame = seq.FrameIndex(0, i);
                var img = file.ToImage(frame);
                if (img == null) { bad.Add($"{look}[{i}]=#{frame} null"); continue; }
                int opaque = 0, black = 0;
                for (int p = 0; p < img.Pixels.Length; p += 4)
                    if (img.Pixels[p + 3] > 0) { opaque++; if (img.Pixels[p] < 12 && img.Pixels[p + 1] < 12 && img.Pixels[p + 2] < 12) black++; }
                if (opaque > 0 && black > opaque / 2) bad.Add($"{look}[{i}]=#{frame} {black}/{opaque} black");
            }
        }
        output.WriteLine(string.Join(" ", bad));
        Assert.Empty(bad);
    }
}

public class D2kGroundHoleProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void NoMapHasHoles()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        string folder = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "dune2000", "mod", "maps");
        var bad = new List<string>();
        foreach (string f in Directory.GetFiles(folder, "*.oramap"))
        {
            string name = Path.GetFileNameWithoutExtension(f);
            try { GroundHasNoHoles(name); }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException) { bad.Add($"{name}: {e.GetType().Name}"); }
            catch (Xunit.Sdk.XunitException) { bad.Add(name); }
        }
        output.WriteLine("bad: " + string.Join(", ", bad));
        Assert.Empty(bad);
    }

    void GroundHasNoHoles(string name)
    {
        var map = OpenWestwood.Client.Dune2000.D2kMaps.Convert("d2k:" + name, null);
        var img = map.GroundImage!;
        int holes = 0;
        for (int p = 3; p < img.Pixels.Length; p += 4)
            if (img.Pixels[p] == 0) holes++;
        output.WriteLine($"{name}: {holes} empty pixels of {img.Width * img.Height} ({holes / 1024} cells); missing: {string.Join(" | ", OpenWestwood.Client.Dune2000.D2kMaps.MissingTiles.Take(20))}");
        Assert.Equal(0, holes);
    }
}

public class D2kWalkProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void DuneInfantryWalkWhileMoving()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "d2k:red-chasm");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionD2KAtreides", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionD2KHarkonnen", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        var start = alice.StartPosition!.Value;
        var bad = new List<string>();
        int i = 0;
        foreach (string t in new[] { "D2KATR_LIGHT_INF", "D2KATR_TROOPER", "D2KATR_FREMEN", "D2KATR_GRENADIER", "D2KATR_ENGINEER", "D2KATR_THUMPER", "D2KHAR_MPSARDAUKAR", "D2KORD_SABOTEUR" })
        {
            if (game.Templates.Find(t) == null) continue;
            var unit = game.CreateObject(t, game.Templates.Find(t)!.Rules.GetString("Side").Contains("HAR") ? game.Players.Find("Bob")! : alice, start + new System.Numerics.Vector3(60 + i * 15, 0, 0))!;
            i++;
            if (i % 2 == 0)
                unit.AI!.MoveTo(unit.Position + new System.Numerics.Vector3(0, 300, 0), Logic.Commands.CommandSource.Player);
            else
                unit.AI!.AttackMoveTo(unit.Position + new System.Numerics.Vector3(0, 300, 0), Logic.Commands.CommandSource.AI);
            int walking = 0, moved = 0;
            var last = unit.Position;
            for (int f = 0; f < 6 * LogicTime.FramesPerSecond; f++)
            {
                game.Update();
                bool advanced = OpenWestwood.Logic.Core.MathUtil.Distance2D(unit.Position, last) > 0.05f;
                last = unit.Position;
                if (advanced) { moved++; if (unit.AI.IsWalking) walking++; }
            }
            output.WriteLine($"{t}: moved {moved} frames, walking on {walking}; speed {unit.FindModule<Logic.Modules.Updates.PhysicsBehavior>()?.VelocityMagnitude:0.00}");
            if (moved > 0 && walking < moved * 0.8) bad.Add(t);
        }
        Assert.Empty(bad);
    }
}

public class D2kUnloadTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void HarvesterSitsInTheRefineryWhileUnloading()
    {
        if (!content.Available || OpenWestwood.Client.Dune2000.D2kArt.Get() == null) return;
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, "d2k:red-chasm");
        var game = GameSetup.FromMap(map, content.Rules!, seed: 3,
        [
            new PlayerSlot("Alice", "FactionD2KAtreides", IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionD2KHarkonnen", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var bob = game.Players.Find("Bob")!;
        int docked = 0, spells = 0;
        bool was = false;
        for (int i = 0; i < 240 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            var first = game.ObjectsOwnedBy(bob).OrderBy(o => o.Id).FirstOrDefault(o => o.AI is OpenWestwood.Logic.Modules.Supply.SupplyTruckAIUpdate);
            bool now = first != null && first.AI is OpenWestwood.Logic.Modules.Supply.SupplyTruckAIUpdate { IsDocking: true, IsWalking: false } t && first.HasCondition("DOCKING_ACTIVE")
                && game.FindObject(t.DockTargetId)?.FindModule<OpenWestwood.Logic.Modules.Supply.SupplyCenterDockUpdate>() is { } refinery
                && OpenWestwood.Logic.Core.MathUtil.Distance2D(first.Position, refinery.GetDockPosition(first)) < 20;
            if (now) docked++;
            if (now && !was) spells++;
            if (now != was && spells <= 2)
            {
                var h = first!;
                output.WriteLine($"t{i / (float)LogicTime.FramesPerSecond:0.0}: unloading {now}; boxes {OpenWestwood.Logic.Modules.Supply.SupplyGatherer.Of(h)?.Boxes} pos {h.Position.X:0},{h.Position.Y:0} money {bob.Money.Amount}");
            }
            was = now;
        }
        output.WriteLine($"unloading spells {spells}, {docked / (float)LogicTime.FramesPerSecond:0.0} s docked in all");
        Assert.True(spells >= 2);
        Assert.True(docked / (float)spells >= 2 * LogicTime.FramesPerSecond);
    }
}

public class D2kRunFramesDump(ITestOutputHelper output)
{
    [Fact]
    public void DumpRunFrames()
    {
        string dir = Environment.GetEnvironmentVariable("RA_DUMP") ?? "";
        if (dir.Length == 0 || OpenWestwood.Client.Dune2000.D2kArt.Get() is not { } d2k) return;
        var file = d2k.SpriteFile("DATA.R16")!;
        // Light infantry run: start 214, 6 frames, 8 facings. Row A: transposed (214 + step*8), row B: facing-major (214 + step).
        var rows = new[] { Enumerable.Range(0, 6).Select(k => 214 + k * 8).ToArray(), Enumerable.Range(0, 6).Select(k => 214 + k).ToArray() };
        int cell = 48;
        var sheet = new OpenWestwood.Formats.Images.Image(cell * 6, cell * 2);
        for (int r = 0; r < 2; r++)
            for (int k = 0; k < 6; k++)
            {
                var img = file.ToImage(rows[r][k]);
                if (img == null) continue;
                for (int y = 0; y < Math.Min(cell, img.Height); y++)
                    for (int x = 0; x < Math.Min(cell, img.Width); x++)
                    {
                        int s = (y * img.Width + x) * 4, d = ((r * cell + y) * sheet.Width + k * cell + x) * 4;
                        for (int c = 0; c < 4; c++) sheet.Pixels[d + c] = img.Pixels[s + c];
                    }
            }
        OpenWestwood.Formats.Images.PngWriter.Save(sheet, Path.Combine(dir, "d2k_run.png"));
    }
}

public class DuneShellHitchProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void FindSlowFrames()
    {
        if (Environment.GetEnvironmentVariable("SHELL_HITCH") == null || !content.Available) return;
        var (game, _) = OpenWestwood.Client.Shell.DuneShellBattle.Create(content.Rules!, content.FileSystem!, content.Models, seed: 1)!.Value;
        var times = new List<(int Frame, double Ms, string Top)>();
        for (int i = 0; i < 180 * LogicTime.FramesPerSecond; i++)
        {
            Logic.GameProfiler.Reset();
            Logic.GameProfiler.Enabled = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            game.Update();
            double ms = sw.Elapsed.TotalMilliseconds;
            Logic.GameProfiler.Enabled = false;
            string top = string.Join(", ", Logic.GameProfiler.Report().OrderByDescending(r => r.Milliseconds).Take(4).Select(r => $"{r.Name} {r.Milliseconds:0.0}"));
            times.Add((game.Frame, ms, top));
        }
        output.WriteLine($"average {times.Average(t => t.Ms):0.00} ms; frames over 33 ms: {times.Count(t => t.Ms > 33)}; over 100 ms: {times.Count(t => t.Ms > 100)}");
        foreach (var t in times.OrderByDescending(t => t.Ms).Take(8))
            output.WriteLine($"frame {t.Frame}: {t.Ms:0.0} ms — {t.Top}");
    }
}

public class KwBigCensus(ITestOutputHelper output)
{
    [Fact]
    public void ListKanesWrathArchives()
    {
        string root = Environment.GetEnvironmentVariable("KW_ROOT") ?? "";
        if (root.Length == 0) return;
        foreach (string big in Directory.GetFiles(root, "*.big", SearchOption.AllDirectories).Where(f => !f.Contains("Movies")))
        {
            try
            {
                using var arc = new OpenWestwood.Core.FileSystem.BigArchive(big);
                var files = arc.FilePaths.ToList();
                var exts = files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key}x{g.Count()}");
                output.WriteLine($"{big[root.Length..]}: {files.Count} files: {string.Join(" ", exts)}; e.g. {string.Join(" ", files.Take(4))}");
            }
            catch (Exception e) { output.WriteLine($"{big}: {e.Message}"); }
        }
    }
}

public class KwManifestCensus(ITestOutputHelper output)
{
    [Fact]
    public void ListAssetTypes()
    {
        string root = Environment.GetEnvironmentVariable("KW_ROOT") ?? "";
        if (root.Length == 0) return;
        foreach (var (bigPath, manifest) in new[] { (@"Core\1.0\StaticStream.big", "data/static_common.manifest"), (@"Core\1.0\GlobalStream.big", "data/global_common.manifest"), (@"Core\1.2\patch2.big", "data/global_common_2.manifest"), (@"Core\1.0\WBData.big", "data/worldbuilder.manifest") })
        {
            using var big = new OpenWestwood.Core.FileSystem.BigArchive(Path.Combine(root, bigPath));
            using var s = big.Open(manifest);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var m = new OpenWestwood.Formats.Sage.SageManifest(ms.ToArray());
            output.WriteLine($"{manifest}: v{m.Version} linked {m.IsLinked} big-endian {m.IsBigEndian}, {m.Assets.Count} assets; references {string.Join(" ", m.ReferencedManifests)}");
            output.WriteLine("  types: " + string.Join(", ", m.Assets.GroupBy(a => a.TypeName).OrderByDescending(g => g.Count()).Take(40).Select(g => $"{g.Key} {g.Count()}")));
            if (Environment.GetEnvironmentVariable("KW_TYPEGREP") is { } tg) output.WriteLine("  matching: " + string.Join(", ", m.Assets.Where(a => a.TypeName.Contains(tg, StringComparison.OrdinalIgnoreCase) || a.InstanceName.Contains(tg, StringComparison.OrdinalIgnoreCase)).GroupBy(a => a.TypeName).Select(g => $"{g.Key} {g.Count()} ({string.Join(" ", g.Take(6).Select(a => a.InstanceName))})")));
            output.WriteLine("  e.g. " + string.Join(", ", m.Assets.Where(a => a.TypeName == "GameObject").Take(12).Select(a => $"{a.InstanceName}({a.InstanceDataSize}b)")));
        }
    }
}

public class KwManifestHex(ITestOutputHelper output)
{
    [Fact]
    public void DumpHeader()
    {
        string root = Environment.GetEnvironmentVariable("KW_ROOT") ?? "";
        if (root.Length == 0) return;
        using var big = new OpenWestwood.Core.FileSystem.BigArchive(Path.Combine(root, @"Core\1.0\StaticStream.big"));
        using var s = big.Open("data/static_common.manifest");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var b = ms.ToArray();
        output.WriteLine($"size {b.Length}");
        for (int row = 0; row < 8; row++)
            output.WriteLine($"{row * 16:x4}: " + string.Join(" ", Enumerable.Range(row * 16, 16).Select(i => b[i].ToString("x2"))) + "  " + string.Join(" ", Enumerable.Range(0, 4).Select(k => BitConverter.ToUInt32(b, row * 16 + k * 4))));
    }
}

public class KwAssetDump(ITestOutputHelper output)
{
    static byte[] Read(OpenWestwood.Core.FileSystem.BigArchive big, string name)
    {
        using var s = big.Open(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return OpenWestwood.Formats.Sage.SageManifest.Unpack(ms.ToArray());
    }

    [Fact]
    public void DumpSamples()
    {
        string types = Environment.GetEnvironmentVariable("KW_TYPES") ?? "";
        if (types.Length == 0) return;
        string root = Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "content", "kaneswrath");
        using var big = new OpenWestwood.Core.FileSystem.BigArchive(Path.Combine(root, @"Core\1.0\StaticStream.big"));
        var m = new OpenWestwood.Formats.Sage.SageManifest(Read(big, "data/static_common.manifest"));
        var bin = Read(big, "data/static_common.bin");
        var relo = Read(big, "data/static_common.relo");
        var imp = Read(big, "data/static_common.imp");
        output.WriteLine($"bin {bin.Length} (sum {m.Assets.Sum(a => (long)a.InstanceDataSize)}), relo {relo.Length} (sum {m.Assets.Sum(a => (long)a.RelocationDataSize)}), imp {imp.Length} (sum {m.Assets.Sum(a => (long)a.ImportsDataSize)})");
        string want = Environment.GetEnvironmentVariable("KW_NAME") ?? "";
        foreach (string type in types.Split(','))
            foreach (var a in m.Assets.Where(a => a.TypeName == type && (want.Length == 0 || a.InstanceName.Contains(want, StringComparison.OrdinalIgnoreCase))).Take(2))
            {
                output.WriteLine($"== {a.Name} data {a.InstanceDataSize} relo {a.RelocationDataSize} imp {a.ImportsDataSize} refs {a.References.Length} src {a.SourceFile}");
                var data = bin.AsSpan((int)a.InstanceOffset, a.InstanceDataSize).ToArray();
                var relocs = Enumerable.Range(0, a.RelocationDataSize / 4).Select(k => BitConverter.ToUInt32(relo, (int)a.RelocationOffset + k * 4)).ToList();
                output.WriteLine("  relo: " + string.Join(" ", relocs.Take(64)));
                output.WriteLine("  imp: " + string.Join(" ", Enumerable.Range(0, a.ImportsDataSize / 4).Select(k => BitConverter.ToUInt32(imp, (int)a.ImportsOffset + k * 4)).Take(64)));
                for (int row = 0; row < Math.Min(int.TryParse(Environment.GetEnvironmentVariable("KW_ROWS"), out int rows) ? rows : 12, (data.Length + 15) / 16); row++)
                {
                    int o = row * 16;
                    var bytes = data.Skip(o).Take(16).ToArray();
                    output.WriteLine($"  {o:x4}: {string.Join(" ", bytes.Select(x => x.ToString("x2"))),-48} {new string(bytes.Select(x => x is >= 32 and < 127 ? (char)x : '.').ToArray())}");
                }
            }
    }
}
