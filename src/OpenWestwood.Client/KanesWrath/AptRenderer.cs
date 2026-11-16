using System.Numerics;
using OpenWestwood.Client.Graphics;
using OpenWestwood.Client.UI;
using OpenWestwood.Formats.Sage;

namespace OpenWestwood.Client.KanesWrath;

/// <summary>
/// Draws a Kane's Wrath interface movie (AptMovie) as its frames lay it out: each sprite's display list built up to the
/// frame shown (place / move / remove by depth), shapes as their triangles (textured ones take their texture's pixels
/// through the shape's texture matrix), buttons in their "up" state. Actions (ActionScript) are not run: what the game's
/// scripts would load or switch at run time is left to the caller (named placements, <see cref="Placements"/>).
/// </summary>
public sealed class AptRenderer
{
    readonly AptMovie apt;
    readonly TextureCache textures;
    readonly Dictionary<int, (Texture Texture, Vector2 Size)?> textureById = new();

    /// <summary>Every named placement drawn last time: its path ("main.radar"), where it was (stage to screen) and its character.</summary>
    public Dictionary<string, (Matrix3x2 Transform, AptMovie.Character? Character)> Placements { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which frame a named sprite shows (by its path); otherwise its first.</summary>
    public Func<string, int?>? FrameOf { get; set; }

    /// <summary>Whether to leave out a named placement (and all in it).</summary>
    public Func<string, bool>? Hide { get; set; }

    public AptMovie Movie => apt;

    /// <summary>Leave out the placements' colour transforms (scripts fade many in from nothing).</summary>
    public bool IgnoreTints { get; set; } = true;

    /// <summary>Named placements drawn at full strength whatever their colour transform says (faded in by the scripts).</summary>
    public Func<string, bool>? Opaque { get; set; }

    /// <summary>A frame label to show where a sprite has it (Kane's Wrath's faction frames: "GDI", "NOD", "Alien").</summary>
    public string? Label { get; set; }

    public AptRenderer(AptMovie apt, TextureCache textures)
    {
        this.apt = apt;
        this.textures = textures;
    }

    (Texture Texture, Vector2 Size)? TextureFor(int image)
    {
        int id = apt.ImageTextures.TryGetValue(image, out int t) ? t : image;
        if (textureById.TryGetValue(id, out var cached))
            return cached;
        (Texture, Vector2)? result = null;
        if (apt.Textures.TryGetValue(id, out var dds)
            && textures.GetOrCreate($"apt:{apt.Name}:{id}", () => OpenWestwood.Formats.Images.Image.Load(dds, "apt.dds")) is { } texture)
            result = (texture, new Vector2(texture.Width, texture.Height));
        return textureById[id] = result;
    }

    /// <summary>Draws the movie's frame (the stage's points through <paramref name="stageToScreen"/>).</summary>
    public void Draw(SpriteBatch batch, Matrix3x2 stageToScreen, int frame = 0)
    {
        Placements.Clear();
        DrawPlayable(batch, apt.Movie, stageToScreen, Vector4.One, frame, "", 0);
    }

    /// <summary>A sprite's (or the movie's) display list at a frame: depth -> placement.</summary>
    static SortedDictionary<int, AptMovie.PlaceObject> DisplayList(AptMovie.Playable playable, int frame)
    {
        var list = new SortedDictionary<int, AptMovie.PlaceObject>();
        for (int f = 0; f <= Math.Min(frame, playable.Frames.Count - 1); f++)
        {
            var fr = playable.Frames[f];
            foreach (int depth in fr.Removes)
                list.Remove(depth);
            foreach (var p in fr.Places)
            {
                if (p.IsMove && list.TryGetValue(p.Depth, out var old))
                    list[p.Depth] = old with
                    {
                        Character = p.HasCharacter ? p.Character : old.Character,
                        Matrix = p.Matrix ?? old.Matrix,
                        Tint = p.Tint ?? old.Tint,
                        Add = p.Add ?? old.Add,
                        Name = p.Name ?? old.Name,
                    };
                else if (p.HasCharacter)
                    list[p.Depth] = p;
            }
        }
        return list;
    }

    void DrawPlayable(SpriteBatch batch, AptMovie.Playable playable, Matrix3x2 m, Vector4 tint, int frame, string path, int nesting)
    {
        if (nesting > 24)
            return;
        foreach (var (_, place) in DisplayList(playable, frame))
        {
            var characters = apt.Movie.Characters;
            if (place.Character < 0 || place.Character >= characters.Count || characters[place.Character] is not { } character || ReferenceEquals(character, apt.Movie))
                continue;
            string childPath = place.Name is { Length: > 0 } name ? (path.Length > 0 ? path + "." + name : name) : path;
            var childM = (place.Matrix ?? Matrix3x2.Identity) * m;
            // (A placement's colour transform: its tint's alpha is often 0 in the frames, faded in by the game's scripts.)
            var childTint = place.Tint is { } t && !IgnoreTints ? tint * t : tint;
            if (Opaque?.Invoke(childPath) == true)
                childTint.W = tint.W;
            if (place.Name is { Length: > 0 })
            {
                Placements[childPath] = (childM, character);
                if (Hide?.Invoke(childPath) == true)
                    continue;
            }
            DrawCharacter(batch, character, childM, childTint, childPath, nesting + 1);
        }
    }

    void DrawCharacter(SpriteBatch batch, AptMovie.Character character, Matrix3x2 m, Vector4 tint, string path, int nesting)
    {
        switch (character)
        {
            case AptMovie.Sprite sprite:
                int frame = FrameOf?.Invoke(path) ?? (Label != null ? sprite.Frames.FindIndex(f => f.Labels.Contains(Label, StringComparer.OrdinalIgnoreCase)) : -1);
                DrawPlayable(batch, sprite, m, tint, Math.Max(0, frame), path, nesting);
                break;
            case AptMovie.Shape shape when apt.Geometries.TryGetValue(shape.Geometry, out var parts):
                DrawShape(batch, parts, m, tint);
                break;
            case AptMovie.Button button:
                foreach (var record in button.Records.Where(r => (r.State & 1) != 0).OrderBy(r => r.Depth))
                    if (record.Character >= 0 && record.Character < apt.Movie.Characters.Count && apt.Movie.Characters[record.Character] is { } c && !ReferenceEquals(c, apt.Movie))
                        DrawCharacter(batch, c, record.Matrix * m, tint, path, nesting + 1);
                break;
        }
    }

    void DrawShape(SpriteBatch batch, List<AptMovie.GeometryPart> parts, Matrix3x2 m, Vector4 tint)
    {
        foreach (var part in parts)
        {
            var color = part.Color * tint;
            if (part.Textured && TextureFor(part.Image) is { } tex)
            {
                foreach (var (a, b, c) in part.Triangles)
                {
                    Vector2 Uv(Vector2 p) => Vector2.Transform(p, part.TextureMatrix) / tex.Size;
                    batch.DrawTexturedTriangle(tex.Texture, Vector2.Transform(a, m), Vector2.Transform(b, m), Vector2.Transform(c, m), Uv(a), Uv(b), Uv(c), tint);
                }
            }
            else if (part.Triangles.Count > 0)
                foreach (var (a, b, c) in part.Triangles)
                    batch.FillTriangle(Vector2.Transform(a, m), Vector2.Transform(b, m), Vector2.Transform(c, m), color);
            foreach (var (a, b) in part.Lines)
                batch.DrawLine(Vector2.Transform(a, m), Vector2.Transform(b, m), color, Math.Max(1, part.LineWidth * m.M11));
        }
    }
}
