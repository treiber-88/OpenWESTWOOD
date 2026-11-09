using System.Numerics;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Modules.Production;
using OpenWestwood.Logic.Templates;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Emperor's sub-houses: any house can ally with two of them, whose buildings train their units.</summary>
public class EmperorSubHouseTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Theory]
    [InlineData("FactionEMAtreides", "AT")]
    [InlineData("FactionEMHarkonnen", "HK")]
    [InlineData("FactionEMOrdos", "OR")]
    public void TwoSubHousesAtMost(string faction, string code)
    {
        if (!content.Available || content.Rules!.Section("Objects")["EM_IMBarracks"] == null) return;
        var mapFile = Formats.Maps.MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(mapFile, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", faction, IsHuman: true, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionChina", IsHuman: true, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        var alice = game.Players.Find("Alice")!;
        var start = alice.StartPosition!.Value;
        int n = 0;
        Logic.Objects.GameObject Make(string name)
        {
            var p = start + new Vector3(-300 + 140 * (n % 5), 300 + 140 * (n++ / 5), 0);
            var o = game.CreateObject(name, alice, p with { Z = game.Terrain.GroundHeight(p) })!;
            game.TreatAsBuilt(o);
            return o;
        }
        bool CanBuild(string name) => alice.CanBuild(game.Templates.Find(name)!);
        string[] subs = ["EM_FRCamp", "EM_IMBarracks", "EM_IXResCentre", "EM_TLFleshVat", "EM_GUPalace"];
        output.WriteLine($"with nothing: {string.Join(" ", subs.Select(s => $"{s}={CanBuild(s)}"))}");
        Assert.DoesNotContain(subs, CanBuild);
        Make($"EM_{code}ConYard");
        Make($"EM_{code}Barracks");
        Make($"EM_{code}Factory");
        output.WriteLine($"with barracks and factory: {string.Join(" ", subs.Select(s => $"{s}={CanBuild(s)}"))}");
        Assert.All(subs, s => Assert.True(CanBuild(s), s));
        // The yard offers them.
        var yardButtons = CommandSets.BuildableObjects(game.Catalog, $"EM_{code}ConYardCommandSet", "UNIT_BUILD").ToList();
        Assert.All(subs, s => Assert.Contains(s, yardButtons));

        var corrino = Make("EM_IMBarracks");
        Assert.True(CanBuild("EM_IMSardaukar"), "the Imperial Barracks trains Sardaukar");
        Assert.False(CanBuild("EM_IMADVSardaukar"), "the elite need the barracks' upgrade");
        alice.CompleteUpgrade("Upgrade_EM_IMBarracks");
        Assert.True(CanBuild("EM_IMADVSardaukar"));
        Assert.Contains("EM_IMSardaukar", CommandSets.BuildableObjects(game.Catalog, corrino.CommandSetName, "UNIT_BUILD"));

        Make("EM_FRCamp");
        output.WriteLine($"allied with Corrino and the Fremen: {string.Join(" ", subs.Select(s => $"{s}={CanBuild(s)}"))}");
        Assert.True(CanBuild("EM_FRCamp") && CanBuild("EM_IMBarracks"));
        Assert.False(CanBuild("EM_IXResCentre") || CanBuild("EM_TLFleshVat") || CanBuild("EM_GUPalace"), "a third sub-house is out");
    }
}
