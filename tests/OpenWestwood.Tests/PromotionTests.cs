using OpenWestwood.Core.Rules;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Network;
using OpenWestwood.Logic.Templates;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>The general's promotions page: the side's own promotions, bought with points.</summary>
public class PromotionTests(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void APlayerBuysPromotionsOfTheirOwnSide()
    {
        if (!content.Available) return;
        var game = content.NewSkirmish();
        var alice = game.Players.Find("Alice")!; // America
        var rank1 = CommandSets.Buttons(game.Catalog, alice.Faction!.GetString("PurchaseScienceCommandSetRank1"));
        output.WriteLine($"rank 1: {string.Join(", ", rank1.Select(b => $"{b.Slot}={b.Button.Science}"))}; points {alice.SciencePurchasePoints}");
        Assert.NotEmpty(rank1);

        // America's promotions are America's; China's aren't shown to an American.
        string usa = RuleValue.Words(rank1[0].Button.Science)[0];
        Assert.True(alice.HasRootPrereqsForScience(usa));
        var china = game.Players.Find("Bob")!;
        string chinese = RuleValue.Words(CommandSets.Buttons(game.Catalog, china.Faction!.GetString("PurchaseScienceCommandSetRank1"))[0].Button.Science)[0];
        Assert.False(alice.HasRootPrereqsForScience(chinese));

        // Buying needs points; the command goes through the network codec like any other order.
        alice.SciencePurchasePoints = 0;
        new PurchaseScienceCommand(alice.Index, usa).Execute(game);
        Assert.False(alice.HasScience(usa));
        alice.SciencePurchasePoints = 3;
        var bytes = new MemoryStream();
        CommandCodec.Write(new BinaryWriter(bytes), new PurchaseScienceCommand(alice.Index, usa));
        bytes.Position = 0;
        CommandCodec.Read(new BinaryReader(bytes)).Execute(game);
        Assert.True(alice.HasScience(usa));
        Assert.Equal(3 - alice.SciencePurchaseCost(usa), alice.SciencePurchasePoints);
    }
}
