using OpenGZH.TiberianDawn;

namespace OpenGZH.Client;

/// <summary>
/// Red Alert's files (mods/redalert: the RA95 install's archives in content/redalert), loaded once when first
/// needed. Its type tables share Tiberian Dawn's layout, so TD's art and type classes read them.
/// </summary>
public static class RedAlertContent
{
    static TdContent? content;
    static bool tried;
    static OpenGZH.TiberianDawn.Art.TdArt? art;
    static OpenGZH.TiberianDawn.Logic.Types.TdTypes? types;

    /// <summary>Whether the original Red Alert files are installed (content/redalert/REDALERT.MIX).</summary>
    public static bool Available =>
        File.Exists(Path.Combine(GameContent.FindRoot(), "content", "redalert", "REDALERT.MIX"))
        && File.Exists(Path.Combine(GameContent.FindRoot(), "mods", "redalert", "mod.yaml"));

    /// <summary>RA's art with the temperate palette (for RA factions in Zero Hour engine matches).</summary>
    public static OpenGZH.TiberianDawn.Art.TdArt? Art =>
        art ??= Get() is { } c ? new OpenGZH.TiberianDawn.Art.TdArt(c, OpenGZH.TiberianDawn.Logic.TheaterType.THEATER_TEMPERATE) { RemapFirst = 80, RemapLast = 95 } : null;

    /// <summary>RA's object types (its *DATA.CPP tables, as imported to mods/redalert/rules).</summary>
    public static OpenGZH.TiberianDawn.Logic.Types.TdTypes? Types =>
        types ??= Get() is { } c ? new OpenGZH.TiberianDawn.Logic.Types.TdTypes(c.Rules) : null;

    public static TdContent? Get()
    {
        if (!tried)
        {
            tried = true;
            if (Available)
                content = TdContent.Load(GameContent.FindRoot(), "redalert");
        }
        return content;
    }
}
