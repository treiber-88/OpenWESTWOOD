using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.AI;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Kane's Wrath's factions (mods/zh/rules/kw, from export-kw-factions) on the Zero Hour engine.</summary>
public class KwFactionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    bool Ready => content.Available && content.Rules!.Section("Objects")["KW_GDIPredator"] != null;

    GameLogic NewMatch(string alice, string bob, bool aliceHuman = true)
    {
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        return GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", alice, IsHuman: aliceHuman, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", bob, IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
    }

    [Fact]
    public void KwFactionsLoadWithTheirModels()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWGDI", "FactionKWNod");
        var predator = game.Templates.Find("KW_GDIPredator")!;
        Assert.Equal("Predator Tank", predator.DisplayName);
        Assert.NotNull(content.Models!.GetFile("KW_GUPredTank_SKN"));
        foreach (string f in new[] { "FactionKWGDI", "FactionKWSteelTalons", "FactionKWZOCOM", "FactionKWNod", "FactionKWBlackHand", "FactionKWMarkedOfKane", "FactionKWScrin", "FactionKWReaper17", "FactionKWTraveler59" })
            Assert.NotNull(content.Rules!.Section("PlayerTemplates")[f]);
        // Every exported model and animation should exist in Kane's Wrath's art.
        string yaml = File.ReadAllText(Path.Combine(OpenWestwood.Client.GameContent.FindRoot(), "mods", "zh", "rules", "kw", "objects.yaml"));
        var names = System.Text.RegularExpressions.Regex.Matches(yaml, @"(?:Model|Animation): (KW_\w+)").Select(m => m.Groups[1].Value)
            .Concat(System.Text.RegularExpressions.Regex.Matches(yaml, @"IdleAnimation: \[([^\]]+)\]").SelectMany(m => m.Groups[1].Value.Split(", ")))
            .Distinct().ToList();
        var missing = names.Where(n => content.Models.GetFile(n) == null).ToList();
        output.WriteLine($"{names.Count} models and animations, {missing.Count} missing: {string.Join(", ", missing.Take(30))}");
        Assert.True(missing.Count < names.Count / 20, "nearly every model and animation is found");
    }

    [Fact]
    public void KwSoundsResolveThroughTheGame()
    {
        if (!Ready) return;
        var audio = new OpenWestwood.Client.Audio.AudioManager(content.FileSystem!, content.Rules!, openDevice: false);
        var predator = content.Rules!.Section("Objects")["KW_GDIPredator"]!;
        string select = predator.GetString("VoiceSelect");
        var multi = audio.FindInfo(select)!;
        Assert.NotEmpty(multi.AlsoPlay);
        var voice = audio.FindInfo(multi.AlsoPlay[0])!;
        Assert.NotEmpty(voice.Sounds);
        var files = audio.FindInfo("KwSnd_Geva_LowPower")!.Sounds.Concat(voice.Sounds).ToList();
        var bytes = content.FileSystem!.ReadAllBytes(files[0]);
        Assert.True(OpenWestwood.Formats.Audio.WavFile.Decode(bytes).DurationSeconds > 0.3);
        // Every file a Kane's Wrath sound names is there.
        var kwSounds = content.Rules.Section("AudioEvents").Keys.Where(k => k.StartsWith("KwSnd_")).Select(k => audio.FindInfo(k)!).ToList();
        var missing = kwSounds.SelectMany(i => i.Sounds).Where(f => !content.FileSystem.Exists(f)).Distinct().ToList();
        output.WriteLine($"{kwSounds.Count} sounds, {missing.Count} missing files: {string.Join(" ", missing.Take(10))}");
        Assert.Empty(missing);
        var music = content.Rules.Section("PlayerTemplates")["FactionKWNod"]!.GetString("GameMusic").Split(' ');
        Assert.True(music.Length > 10);
        Assert.True(content.FileSystem.Exists(audio.FindInfo(music[0])!.Filename));
    }

    [Fact]
    public void PredatorsAndScorpionsFightItOut()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWGDI", "FactionKWNod");
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var mine = new List<OpenWestwood.Logic.Objects.GameObject>();
        var theirs = new List<OpenWestwood.Logic.Objects.GameObject>();
        for (int i = 0; i < 3; i++)
        {
            var a = mid + new System.Numerics.Vector3(-150, (i - 1) * 50, 0);
            var b = mid + new System.Numerics.Vector3(150, (i - 1) * 50, 0);
            mine.Add(game.CreateObject("KW_GDIPredator", game.Players.Find("Alice")!, a with { Z = game.Terrain.GroundHeight(a) })!);
            theirs.Add(game.CreateObject("KW_NODScorpionBuggy", game.Players.Find("Bob")!, b with { Z = game.Terrain.GroundHeight(b) })!);
        }
        foreach (var t in theirs)
            t.AI!.AttackMoveTo(mid + new System.Numerics.Vector3(-150, 0, 0), Logic.Commands.CommandSource.AI);
        int frames = 0;
        for (; frames < 90 * LogicTime.FramesPerSecond && mine.Any(o => !o.IsEffectivelyDead) && theirs.Any(o => !o.IsEffectivelyDead); frames++)
            game.Update();
        output.WriteLine($"after {frames / LogicTime.FramesPerSecond}s: Predators left {mine.Count(o => !o.IsEffectivelyDead)}, Scorpions {theirs.Count(o => !o.IsEffectivelyDead)}");
        Assert.True(mine.All(o => o.IsEffectivelyDead) || theirs.All(o => o.IsEffectivelyDead));
    }

    [Fact]
    public void BuzzerHiveSendsItsBuzzers()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWScrin", "FactionKWGDI");
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var hive = game.CreateObject("KW_AlienBuzzerHive", alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        var reactorAt = mid + new System.Numerics.Vector3(-200, 0, 0);
        game.CreateObject("KW_AlienPowerProjector", alice, reactorAt with { Z = game.Terrain.GroundHeight(reactorAt) }); // (the hive needs power)
        for (int i = 0; i < 3 * LogicTime.FramesPerSecond; i++)
            game.Update();
        int buzzers = game.ObjectsOwnedBy(alice).Count(o => o.Template.Name == "KW_AlienBuzzers_Slaved");
        var soldiers = new List<OpenWestwood.Logic.Objects.GameObject>();
        for (int i = 0; i < 3; i++)
        {
            var at = mid + new System.Numerics.Vector3(120, i * 15, 0);
            var s = game.CreateObject("KW_GDIRifleSoldier", bob, at with { Z = game.Terrain.GroundHeight(at) })!;
            s.SetDisabled("DISABLED_HELD");
            soldiers.Add(s);
        }
        float before = soldiers.Sum(s => s.Body!.Health);
        for (int i = 0; i < 20 * LogicTime.FramesPerSecond && soldiers.Any(s => !s.IsEffectivelyDead); i++)
            game.Update();
        float after = soldiers.Sum(s => s.IsEffectivelyDead ? 0 : s.Body!.Health);
        output.WriteLine($"{buzzers} buzzers; soldiers' health {before} -> {after}");
        Assert.True(buzzers > 0, "the hive spawns buzzers");
        Assert.True(after < before * 0.5f, "the buzzers attack");
    }

    [Fact]
    public void OrcasFlyFromTheirPadAttackAndComeBackToRearm()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWGDI", "FactionKWNod");
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        alice.Money.Deposit(10000);
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var site = mid + new System.Numerics.Vector3(-500, 0, 0);
        var pad = game.CreateObject("KW_GDIAirTower", alice, site with { Z = game.Terrain.GroundHeight(site) })!;
        var production = pad.FindModule<OpenWestwood.Logic.Modules.Production.ProductionUpdate>()!;
        Assert.NotNull(production.QueueUnit(game.Templates.Find("KW_GDIOrca")!));
        OpenWestwood.Logic.Objects.GameObject? orca = null;
        for (int i = 0; i < 90 * LogicTime.FramesPerSecond && orca == null; i++)
        {
            game.Update();
            orca = game.ObjectsOwnedBy(alice).FirstOrDefault(o => o.Template.Name == "KW_GDIOrca");
        }
        Assert.NotNull(orca);
        var targetAt = mid + new System.Numerics.Vector3(300, 0, 0);
        var target = game.CreateObject("KW_NODScorpionBuggy", bob, targetAt with { Z = game.Terrain.GroundHeight(targetAt) })!;
        target.Body!.SetMaxHealth(1000000, OpenWestwood.Logic.Modules.Bodies.MaxHealthChange.FullyHeal);
        target.SetDisabled("DISABLED_HELD"); // (a dummy: it mustn't drive off and shoot the Orca on its pad)
        for (int i = 0; i < 2 * LogicTime.FramesPerSecond; i++)
            game.Update();
        orca!.AI!.AttackObject(target, Logic.Commands.CommandSource.Player);
        bool flew = false, emptied = false, reloaded = false;
        for (int i = 0; i < 120 * LogicTime.FramesPerSecond && !reloaded; i++)
        {
            game.Update();
            flew |= orca.Position.Z > game.Terrain.GroundHeight(orca.Position) + 30;
            var weapon = orca.Weapons.All.FirstOrDefault();
            if (weapon == null) continue;
            emptied |= weapon.RemainingAmmo == 0;
            reloaded = emptied && weapon.RemainingAmmo == weapon.ClipSize && System.Numerics.Vector3.Distance(orca.Position with { Z = 0 }, pad.Position with { Z = 0 }) < 120;
        }
        output.WriteLine($"Orca flew {flew}, emptied its clip {emptied}, back on its pad rearmed {reloaded}; target took {target.Body.MaxHealth - target.Body.Health:F0} damage");
        Assert.True(flew && emptied && reloaded);
    }

    [Theory]
    [InlineData("FactionKWGDI", "KW_GDIIonCannonControl")]
    [InlineData("FactionKWNod", "KW_NODTempleOfNOD")]
    [InlineData("FactionKWScrin", "KW_AlienRiftGenerator")]
    public void SuperweaponsDestroyWhatTheyHit(string faction, string building)
    {
        if (!Ready) return;
        var game = NewMatch(faction, "FactionKWGDI");
        var alice = game.Players.Find("Alice")!;
        var bob = game.Players.Find("Bob")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var site = mid + new System.Numerics.Vector3(-400, 0, 0);
        var weapon = game.CreateObject(building, alice, site with { Z = game.Terrain.GroundHeight(site) })!;
        var targets = new List<OpenWestwood.Logic.Objects.GameObject>();
        for (int i = 0; i < 4; i++)
        {
            var at = mid + new System.Numerics.Vector3(400 + i % 2 * 30, i / 2 * 30, 0);
            targets.Add(game.CreateObject("KW_GDIPredator", bob, at with { Z = game.Terrain.GroundHeight(at) })!);
        }
        var power = weapon.FindModule<OpenWestwood.Logic.Modules.SpecialPowers.OCLSpecialPower>()!;
        power.DoSpecialPowerAtLocation(targets[0].Position, float.NaN, 0);
        int frames = 0;
        for (; frames < 20 * LogicTime.FramesPerSecond && targets.Any(o => !o.IsEffectivelyDead); frames++)
            game.Update();
        output.WriteLine($"{building}: after {frames / LogicTime.FramesPerSecond}s {targets.Count(o => o.IsEffectivelyDead)} of 4 Predators destroyed");
        Assert.True(targets.Count(o => o.IsEffectivelyDead) >= 3, "the superweapon destroys what it hits");
    }

    [Theory]
    [InlineData("FactionKWGDI")]
    [InlineData("FactionKWNod")]
    [InlineData("FactionKWScrin")]
    public void KwComputerPlayerBuildsABaseAndAnArmy(string faction)
    {
        if (!Ready) return;
        var game = NewMatch("FactionTDGDI", faction);
        var bob = game.Players.Find("Bob")!;
        Assert.IsType<TdFactionAI>(bob.AI);
        int minutes = int.TryParse(Environment.GetEnvironmentVariable("OPENWESTWOOD_AI_MINUTES"), out int m) ? m : 6;
        for (int i = 0; i < minutes * 60 * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            if (i % (60 * LogicTime.FramesPerSecond) == 0)
                output.WriteLine($"{i / LogicTime.FramesPerSecond}s: Bob ${bob.Money.Amount}, "
                    + string.Join(", ", game.ObjectsOwnedBy(bob).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        }
        var mine = game.ObjectsOwnedBy(bob).ToList();
        output.WriteLine("end: " + string.Join(", ", mine.GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
        Assert.Contains(mine, o => o.IsKindOf("COMMANDCENTER"));
        Assert.True(mine.Count(o => o.IsKindOf(Kind.Structure)) >= 4, "the computer player builds a base");
        Assert.Contains(mine, o => TdFactionAI.TdTypeOf(o.Template) == "STRUCT_REFINERY");
        Assert.True(bob.Score.TotalMoneyEarned > 0, "harvesters bring in money");
        Assert.True(mine.Count(o => o.IsMobile && TdFactionAI.TdTypeOf(o.Template) != "UNIT_HARVESTER") >= 3, "and trains units");
    }

    [Fact]
    public void KwMapsAreListedAndPlayable()
    {
        if (!Ready) return;
        var cache = new OpenWestwood.Logic.Multiplayer.MapCache();
        OpenWestwood.Client.KanesWrath.KwMaps.AddTo(cache);
        output.WriteLine($"{cache.Maps.Count} maps: " + string.Join(", ", cache.Maps.Select(m => $"{m.NameLookupTag} ({m.NumPlayers})")));
        if (cache.Maps.Count == 0) return; // Kane's Wrath's maps.big isn't installed
        Assert.True(cache.Maps.Count >= 20, "Kane's Wrath's multiplayer maps are listed");

        var map = OpenWestwood.Client.KanesWrath.KwMaps.Read("kw:map_mp_2_black1");
        var classes = map.BlendTiles!.TextureClasses;
        var missing = classes.Where(c => content.Rules!.Get("TerrainTypes", c.Name)?.GetString("Texture") is not { } t || !content.FileSystem!.Exists("Art/Terrain/" + t)).Select(c => c.Name).ToList();
        output.WriteLine($"{classes.Count} textures, missing: {string.Join(" ", missing)}; heights up to {map.HeightMap.FineHeights?.Max()}");
        Assert.Empty(missing);
        int crystals = map.Objects.Count(o => o.TemplateName.StartsWith("KW_TiberiumCrystal"));
        output.WriteLine($"{crystals} crystals");
        Assert.True(crystals > 50, "Tiberium fields grow crystals");

        if (Environment.GetEnvironmentVariable("KW_NOPROPS") != null) map.Objects.RemoveAll(o => o.TemplateName.StartsWith("KW_") && !o.TemplateName.StartsWith("KW_Tiberium"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", "FactionKWGDI", IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionKWNod", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        for (int i = 0; i < 4 * 60 * LogicTime.FramesPerSecond; i++)
            game.Update();
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var p = game.Players.Find(name)!;
            output.WriteLine($"{name}: earned ${p.Score.TotalMoneyEarned}, " + string.Join(", ", game.ObjectsOwnedBy(p).GroupBy(o => o.Template.Name).Select(g => $"{g.Key} x{g.Count()}")));
            Assert.True(p.Score.TotalMoneyEarned > 0, $"{name}'s harvesters bring in Tiberium");
            Assert.True(game.ObjectsOwnedBy(p).Count(o => o.IsKindOf(Kind.Structure)) >= 4, $"{name} builds a base");
        }
    }

    [Fact]
    public void ForcefieldGeneratorShieldsScrinVehicles()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWScrin", "FactionKWGDI");
        var alice = game.Players.Find("Alice")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var seeker = game.CreateObject("KW_AlienSeekerTank", alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        var shield = Assert.IsType<OpenWestwood.Logic.Modules.Bodies.ShieldBody>(seeker.Body);
        game.Update();
        Assert.Equal(0, shield.Shield);

        alice.CompleteUpgrade("KW_Upgrade_AlienMinorForcefieldGenerator");
        for (int i = 0; i < 31; i++)
            game.Update();
        Assert.Equal(500, shield.Shield);
        Assert.True(seeker.HasStatus("SHIELDBODY_ENABLED"));

        // A hit smaller than the shield leaves the body untouched; a bigger one breaks through.
        float health = seeker.Health;
        seeker.Damage(new OpenWestwood.Logic.Combat.DamageInfo { Amount = 300, DamageType = "CANNON" });
        Assert.Equal(health, seeker.Health);
        Assert.Equal(200, shield.Shield, 1);
        seeker.Damage(new OpenWestwood.Logic.Combat.DamageInfo { Amount = 400, DamageType = "CANNON" });
        Assert.Equal(0, shield.Shield);
        Assert.True(seeker.Health < health);

        // Left alone long enough, it comes back in full.
        for (int i = 0; i < 101 * LogicTime.FramesPerSecond; i++)
            game.Update();
        Assert.Equal(500, shield.Shield);
    }

    [Fact]
    public void ShockTroopersBlinkOnceTheyHaveBlinkPacks()
    {
        if (!Ready) return;
        var game = NewMatch("FactionKWScrin", "FactionKWGDI");
        var alice = game.Players.Find("Alice")!;
        var mid = new System.Numerics.Vector3(game.Terrain.WorldWidth / 2, game.Terrain.WorldHeight / 2, 0);
        var trooper = game.CreateObject("KW_AlienShockTrooper", alice, mid with { Z = game.Terrain.GroundHeight(mid) })!;
        var power = game.Catalog.SpecialPower("KW_SpecialPower_UseBlinkPack")!;
        Assert.False(OpenWestwood.Logic.Modules.SpecialPowers.SpecialPowerRules.CanUseSpecialPower(trooper, power), "no Blink Packs yet");
        alice.CompleteUpgrade("KW_Upgrade_AlienShockTrooperBlinkPack");
        Assert.True(OpenWestwood.Logic.Modules.SpecialPowers.SpecialPowerRules.CanUseSpecialPower(trooper, power));

        // Blinking farther than 750 stops at 750.
        var far = mid + new System.Numerics.Vector3(1000, 0, 0);
        var module = OpenWestwood.Logic.Modules.SpecialPowers.SpecialPowerRules.ModuleFor(trooper, power)!;
        for (int i = 0; i < 31 * LogicTime.FramesPerSecond && module.PercentReady < 1; i++)
            game.Update();
        module.DoSpecialPowerAtLocation(far, float.NaN, 0);
        for (int i = 0; i < LogicTime.FramesPerSecond; i++)
            game.Update();
        float moved = System.Numerics.Vector2.Distance(new(trooper.Position.X, trooper.Position.Y), new(mid.X, mid.Y));
        output.WriteLine($"blinked {moved:0}");
        Assert.InRange(moved, 650, 850);
        Assert.True(module.PercentReady < 1, "the Blink Pack recharges");
    }
}
