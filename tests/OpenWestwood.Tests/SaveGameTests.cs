using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Saving;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Saved games: a loaded game is the same game, and goes on exactly as the one that was saved.</summary>
public class SaveGameTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    MapFile Map(string path) => MapFile.Read(content.FileSystem!.ReadAllBytes(path));

    byte[]? ReadFile(string p) => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null;

    GameLogic Fresh(MapFile map) => GameSetup.FromMap(map, content.Rules!, 0, models: content.Models, readFile: ReadFile);

    void SavedGameGoesOnTheSame(GameLogic game, MapFile map, string path, float playSeconds, float afterSeconds)
    {
        for (int i = 0; i < playSeconds * LogicTime.FramesPerSecond; i++)
            game.Update();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = new MemoryStream();
        SaveGame.Write(game, map, new SaveHeader(path, "test", DateTime.Now, 0, new Dictionary<string, string> { ["k"] = "v" }), bytes);
        long saveMs = stopwatch.ElapsedMilliseconds;

        bytes.Position = 0;
        stopwatch.Restart();
        var loaded = Fresh(map);
        var header = SaveGame.Load(bytes, loaded, map);
        long loadMs = stopwatch.ElapsedMilliseconds;
        output.WriteLine($"{path}: {bytes.Length / 1024} KB, saved in {saveMs} ms, loaded in {loadMs} ms, frame {header.Frame}, {game.Objects.Count()} objects");
        Assert.Equal("v", header.Extra("k"));
        Assert.Equal(game.Frame, loaded.Frame);
        Assert.Equal(game.Objects.Count(), loaded.Objects.Count());
        Assert.Equal(game.ComputeChecksum(), loaded.ComputeChecksum());
        // Saved again, the loaded game is the same snapshot, to the byte (scripts, AI memory, everything).
        var header0 = new SaveHeader(path, "again", new DateTime(2000, 1, 1), 0, new Dictionary<string, string>());
        var first = new MemoryStream();
        var second = new MemoryStream();
        SaveGame.Write(game, map, header0, first);
        SaveGame.Write(loaded, map, header0, second);
        Assert.True(first.ToArray().AsSpan().SequenceEqual(second.ToArray()), "the loaded game saves the same as the original");

        // Both go on in step.
        for (int i = 0; i < afterSeconds * LogicTime.FramesPerSecond; i++)
        {
            game.Update();
            loaded.Update();
            if (i % 15 == 0)
                Assert.True(game.ComputeChecksum() == loaded.ComputeChecksum(), $"out of step {i} frames after loading");
        }
        Assert.Equal(game.ComputeChecksum(), loaded.ComputeChecksum());
        output.WriteLine($"in step for {afterSeconds}s after loading; {game.Objects.Count()} objects");
    }

    [Fact]
    public void SkirmishSavesAndLoads()
    {
        if (!content.Available) return;
        const string path = "Maps/Tournament Desert/Tournament Desert.map";
        var map = Map(path);
        var game = GameSetup.FromMap(map, content.Rules!, seed: 11,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionGLA", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: ReadFile);
        SavedGameGoesOnTheSame(game, map, path, 150, 60);
    }

    [Fact]
    public void OtherGamesFactionsSaveAndLoad()
    {
        if (!content.Available || content.Rules!.Section("Objects")["KW_GDIPredator"] == null) return;
        const string path = "Maps/Tournament Desert/Tournament Desert.map";
        var map = Map(path);
        var game = GameSetup.FromMap(map, content.Rules!, seed: 4,
        [
            new PlayerSlot("Alice", "FactionKWScrin", IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionTSNod", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
            new PlayerSlot("Carol", "FactionRA2Russians", IsHuman: false, StartPosition: 3, Color: 0x00FF00),
            new PlayerSlot("Dave", "FactionTDGDI", IsHuman: false, StartPosition: 4, Color: 0xFFFF00),
        ], models: content.Models, readFile: ReadFile);
        SavedGameGoesOnTheSame(game, map, path, 180, 60);
    }

    [Fact]
    public void CampaignMissionSavesAndLoads()
    {
        if (!content.Available) return;
        const string path = "Maps/MD_USA01/MD_USA01.map";
        var map = Map(path);
        var game = GameSetup.FromMap(map, content.Rules!, 5, difficulty: Logic.Scripting.Difficulty.Normal, models: content.Models, readFile: ReadFile);
        SavedGameGoesOnTheSame(game, map, path, 60, 60);
    }

    /// <summary>A Tiberian Dawn mission: its triggers, teams and production carry on after loading (GDI 5 has all three going).</summary>
    [Fact]
    public void TiberianDawnMissionSavesAndLoads()
    {
        if (!content.Available || OpenWestwood.Client.TiberianDawnContent.Get() == null) return;
        const string path = "td:SCG05EA";
        var map = OpenWestwood.Client.TiberianDawn.TdMaps.Read(content.FileSystem!, path);
        var game = Fresh(map);
        Assert.NotNull(game.TdMission);
        SavedGameGoesOnTheSame(game, map, path, 70, 60);
        Assert.NotNull(game.TdMission!.Teams);
    }
}
