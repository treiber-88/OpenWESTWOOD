using OpenWestwood.TiberianDawn;

namespace OpenWestwood.Client;

/// <summary>Tiberian Dawn's files, loaded once when first needed (they stay open while the game runs).</summary>
public static class TiberianDawnContent
{
    static TdContent? content;
    static bool tried;
    static readonly object LoadLock = new();

    /// <summary>Whether the original TD files are installed (content/tiberiandawn/conquer.mix).</summary>
    public static bool Available =>
        File.Exists(Path.Combine(GameContent.FindRoot(), "content", "tiberiandawn", "conquer.mix"));

    static OpenWestwood.TiberianDawn.Art.TdArt? art;

    /// <summary>TD's art with the temperate palette (for TD factions in Zero Hour engine matches).</summary>
    public static OpenWestwood.TiberianDawn.Art.TdArt? Art =>
        art ??= Get() is { } c ? new OpenWestwood.TiberianDawn.Art.TdArt(c, OpenWestwood.TiberianDawn.Logic.TheaterType.THEATER_TEMPERATE) : null;

    public static TdContent? Get()
    {
        // Locked: the art may be read ahead on a loading thread while the game asks for it.
        lock (LoadLock)
        {
            if (!tried)
            {
                tried = true;
                if (Available)
                    content = TdContent.Load(GameContent.FindRoot());
            }
        }
        return content;
    }
}
