using OpenWestwood.Logic;

namespace OpenWestwood.Client;

/// <summary>
/// Reads the other games' art a match needs before it starts, instead of the first time something of theirs is drawn
/// or fires (Tiberian Sun's and Red Alert 2's archives take seconds to open, which froze the game mid-battle).
/// Safe to call from a loading thread: each game's art loads under its own lock.
/// </summary>
public static class ArtPreloader
{
    public static void PreloadFor(GameLogic game, GameContent content, string? mapName = null)
    {
        var sides = game.Players.All.Select(p => p.Side).ToList();
        bool Any(params string[] prefixes) => sides.Any(s => prefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        string map = mapName ?? "";

        if (Any("KW"))
            foreach (var art in content.FileSystem.Packages.OfType<Formats.Sage.SageArtPackage>())
                _ = art.Stream;
        if (Any("RA2") || map.StartsWith("ra2:", StringComparison.OrdinalIgnoreCase))
            _ = RedAlert2.Ra2Art.Get();
        if (Any("TS") || map.StartsWith("ts:", StringComparison.OrdinalIgnoreCase))
            _ = RedAlert2.Ra2Art.GetTiberianSun();
        if (Any("D2K") || map.StartsWith("d2k:", StringComparison.OrdinalIgnoreCase))
            _ = Dune2000.D2kArt.Get();
        if (Any("TD") || map.StartsWith("td:", StringComparison.OrdinalIgnoreCase))
            _ = TiberianDawnContent.Art;
        if (Any("RAAllies", "RASoviet") || map.StartsWith("ra:", StringComparison.OrdinalIgnoreCase))
            _ = RedAlertContent.Art;
    }
}
