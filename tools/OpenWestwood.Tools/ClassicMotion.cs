using System.Text.RegularExpressions;

namespace OpenWestwood.Tools;

/// <summary>
/// Marks the ground and water locomotors of an exported classic 2D game (Tiberian Dawn, Red Alert, Red Alert 2) with
/// "ClassicMotion: Yes": its units start and stop dead and always go the way they face, as they did in the originals,
/// instead of gliding in and out of stops and drifting through turns with Zero Hour's physics.
/// </summary>
public static class ClassicMotion
{
    public static string Add(string locomotorsYaml) =>
        Regex.Replace(locomotorsYaml, @"^(?<indent>[ ]+)Surfaces: (?<surfaces>[^\r\n]*)(?<nl>\r?\n)", m =>
            m.Groups["surfaces"].Value.Split(' ').Contains("AIR")
                ? m.Value
                : m.Value + m.Groups["indent"].Value + "ClassicMotion: Yes" + m.Groups["nl"].Value, RegexOptions.Multiline);
}
