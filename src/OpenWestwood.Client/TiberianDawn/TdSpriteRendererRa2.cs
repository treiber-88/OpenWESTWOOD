using System.Numerics;
using OpenWestwood.Client.Graphics;
using OpenWestwood.Client.RedAlert2;
using OpenWestwood.Client.World;
using OpenWestwood.Formats.Westwood;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// Red Alert 2 / Yuri's Revenge and Tiberian Sun / Firestorm objects (Draw module RA2SpriteDraw; "Game: TS" for Tiberian
/// Sun's): voxel vehicles drawn at their facing (and their turret's), infantry by their art Sequence (Ready, Walk,
/// FireUp, Die1), buildings with their damaged frame, active animation and build-up, trees, ore and Tiberium lying flat,
/// projectiles, and the warheads' explosion animations ("RA2FX:name", "TSFX:name").
/// </summary>
public sealed partial class TdSpriteRenderer
{
    /// <summary>World units a shape pixel: a cell (36 world units) is 60 pixels wide in Red Alert 2, 48 in Tiberian Sun.</summary>
    static float UnitsPerPixel2(Ra2Art art) => art.IsTiberianSun ? 0.85f * 60 / 48 : 0.85f;
    /// <summary>World units a pixel of a voxel picture (a voxel is about one shape pixel).</summary>
    static float VoxelUnitsPerPixel(Ra2Art art) => UnitsPerPixel2(art) / Ra2Art.PixelsPerVoxel;

    /// <summary>The game is on a Zero Hour (or Kane's Wrath) map, not one converted from a Westwood game.</summary>
    public static bool OnSageMap { get; set; }

    /// <summary>
    /// Tiberian Sun and Red Alert 2 objects are drawn at their original size everywhere. (On Zero Hour maps they were
    /// drawn smaller for a while, TS infantry at 0.8 and RA2 units at 0.7; the user asked for the original size back.)
    /// </summary>
    static float SageMapScale(Ra2Art art, bool infantry) => 1;

    readonly Dictionary<(object Source, int Frame, uint Team, int Mode), Texture?> ra2Textures = new();
    readonly List<(Ra2Art Art, TsShape Shape, Vector3 Position, float Seconds, int Frames, string Name)> ra2Anims = new();

    static bool IsRa2(GameObject obj) =>
        obj.Template.ModulesOfType("Draw").Any(m => m.ClassName.Equals("RA2SpriteDraw", StringComparison.OrdinalIgnoreCase));

    static Logic.Templates.ModuleDefinition Ra2Draw(GameObject obj) =>
        obj.Template.ModulesOfType("Draw").First(m => m.ClassName.Equals("RA2SpriteDraw", StringComparison.OrdinalIgnoreCase));

    Texture? Ra2Texture(Ra2Art art, TsShape shape, int frame, uint team, WestwoodPalette palette, bool shadow)
    {
        var key = ((object)shape, frame, team, (shadow ? 1 : 0) | palette.GetHashCode() << 2);
        if (ra2Textures.TryGetValue(key, out var t))
            return t;
        var image = art.Frame(shape, frame, palette, team == 0xFFFFFFFF ? null : ((byte)(team >> 16), (byte)(team >> 8), (byte)team), shadow);
        t = image != null ? new Texture(gl, image, repeat: false, mipmaps: false, nearest: true) : null;
        return ra2Textures[key] = t;
    }

    Texture? Ra2VoxelTexture(Ra2Art art, string image, int facing, int turret, uint team, int pitch = 0)
    {
        var key = ((object)(art.IsTiberianSun ? "TS:" + image : image), facing | turret << 8 | (pitch + 64) << 16, team, 2);
        if (ra2Textures.TryGetValue(key, out var t))
            return t;
        var picture = art.VoxelImage(image, facing, turret, team, pitch);
        t = picture != null ? new Texture(gl, picture, repeat: false, mipmaps: false, nearest: true) : null;
        return ra2Textures[key] = t;
    }

    /// <summary>An art sequence entry ("start,frames,facing step[,..]") from the infantry's Sequence section.</summary>
    static (int Start, int Count, int Step) Sequence(Ra2Art art, string sequenceSection, string action)
    {
        var values = art.Art.List(sequenceSection, action);
        int Int(int i) => i < values.Length && int.TryParse(values[i], out int v) ? v : 0;
        return values.Length >= 2 ? (Int(0), Math.Max(1, Int(1)), Int(2)) : (0, 1, 1);
    }

    /// <summary>
    /// A soldier burning to death (the rules' [AudioVisual] FlamingInfantry, FLAMEGUY, played for a fire death,
    /// InfDeath=4): its running frames (8 facings of the art's RunningFrames) flail for a moment, then the rest of the
    /// frames burn it down, the last one held. (The second half of the file is the shadow frames.)
    /// </summary>
    static (TsShape Shape, int Frame)? FlamingInfantry(Ra2Art art, float seconds, float facing)
    {
        string name = art.Rules.Get("AudioVisual", "FlamingInfantry", "FLAMEGUY");
        if (art.Shape(name) is not { } shape)
            return null;
        int frames = shape.FrameCount / 2;
        int running = Math.Max(1, (int)art.Art.Num(name, "RunningFrames", 6));
        if (frames <= 8 * running)
            return null;
        const float Fps = 12, RunSeconds = 1.5f;
        int frame = seconds < RunSeconds
            ? (((int)MathF.Round(facing / (MathF.Tau / 8)) % 8 + 8) % 8) * running + (int)(seconds * Fps) % running
            : 8 * running + Math.Min((int)((seconds - RunSeconds) * Fps), Math.Min(frames - 8 * running, 60) - 1);
        return (shape, frame);
    }

    /// <summary>Soldiers already given their electrocution animation.</summary>
    readonly HashSet<uint> electrocuted = new();

    /// <summary>Killed by a tesla weapon (Red Alert's "Super" warhead, Red Alert 2's electric ones: exported as PARTICLE_BEAM).</summary>
    static bool IsTeslaKill(GameObject obj) =>
        string.Equals(obj.Body?.LastDamage?.DamageType, "PARTICLE_BEAM", StringComparison.OrdinalIgnoreCase);

    void AddRa2(GameObject obj, Drawable drawable, Camera camera)
    {
        var draw = Ra2Draw(obj);
        if (Ra2Art.For(draw.Settings.GetString("Game")) is not { } art)
            return;
        float unitsPerPixel = UnitsPerPixel2(art);
        string image = draw.Settings.GetString("Image"), kind = draw.Settings.GetString("Kind");
        var view = camera.View;
        var up = new Vector3(view.M12, view.M22, view.M32);
        float screenUp = MathF.Atan2(camera.ScreenUp.Y, camera.ScreenUp.X);
        uint team = obj.Owner.IsNeutral ? 0xFFFFFFFF : obj.Owner.Color & 0xFFFFFF;
        var anchor = obj.Position;
        float distance = Vector3.DistanceSquared(camera.Position, anchor);
        float opacity = drawable.HiddenByStealth ? 0 : 1;
        float age = createdFrame.TryGetValue(obj.Id, out int born) ? Math.Max(0, obj.Game.Frame - born) / (float)Logic.Core.LogicTime.FramesPerSecond : 99;
        // Facings counter-clockwise from the screen's north: a voxel's from its heading on the ground (it is turned in 3D),
        // a shape's from the way it looks to be heading on screen (its frames are drawn in screen directions).
        float heading = obj.Orientation - screenUp;
        float apparent = camera.ApparentAngle(obj.Orientation) - screenUp;
        int Facing(float angle, int steps) => ((int)MathF.Round(angle / (MathF.Tau / steps)) % steps + steps) % steps;

        void Quad(Texture texture, float width, float height, float pixelUnits, float lift, float extraDistance = 0)
        {
            var center = anchor + up * (lift * pixelUnits);
            quads.Add((distance + extraDistance, quads.Count, texture, center, anchor, width * pixelUnits / 2, height * pixelUnits / 2, new Vector4(1, 1, 1, opacity)));
        }

        if (draw.Settings.GetString("Voxel").Equals("Yes", StringComparison.OrdinalIgnoreCase))
        {
            float turretAngle = obj.AI is { Turrets.Count: > 0 } ai ? obj.Orientation + ai.Turrets[0].Angle - screenUp : heading;
            // A rocket (a V3's, a Dreadnought's) is drawn nosing up as it climbs and down as it dives.
            int pitch = kind == "Bullet" ? Math.Clamp((int)MathF.Round(obj.Pitch / Ra2Art.PitchStep), -6, 6) : 0;
            if (Ra2VoxelTexture(art, image, Facing(heading, 32), Facing(turretAngle, 32), team, pitch) is { } texture)
                Quad(texture, 160, 160, VoxelUnitsPerPixel(art) * SageMapScale(art, false), 160 * 0.1f);
            return;
        }

        switch (kind)
        {
            case "Ore":
                // Lies flat on the ground (RenderRa2Ore, with the spice).
                ra2OrePiles.Add(obj);
                return;
            case "Terrain":
            {
                // A tree: the theater's art (TREE01.TEM), standing on its cell.
                if (art.TheaterShape(image) is not { } tree)
                    return;
                if (Ra2Texture(art, tree, 0, 0xFFFFFFFF, art.TheaterPalette, false) is { } texture)
                    Quad(texture, tree.Width, tree.Height, unitsPerPixel, tree.Height * 0.3f);
                return;
            }
            case "Infantry":
            {
                if (art.Shape(image) is not { } shape)
                    return;
                string sequence = draw.Settings.GetString("Sequence");
                // A tesla kill (Red Alert 2's InfDeath=5: the electric warheads, PARTICLE_BEAM here): the soldier gives way to
                // the crackling ELECTRO animation.
                if (obj.IsEffectivelyDead && IsTeslaKill(obj))
                {
                    if (electrocuted.Add(obj.Id))
                        PlayRa2Anim("ELECTRO", obj.Position, art.IsTiberianSun);
                    return;
                }
                if (obj.IsEffectivelyDead && OpenWestwood.TiberianDawn.Art.TdSpriteState.DeathFor(obj.Body?.LastDamage?.DamageType) == OpenWestwood.TiberianDawn.Logic.DoType.DO_FIRE_DEATH
                    && FlamingInfantry(art, drawable.Clock, apparent) is { } burning)
                {
                    if (Ra2Texture(art, burning.Shape, burning.Frame, 0xFFFFFFFF, art.AnimPalette, false) is { } flame)
                        Quad(flame, burning.Shape.Width, burning.Shape.Height, unitsPerPixel * SageMapScale(art, true), 0);
                    return;
                }
                bool moving = obj.AI?.IsWalking ?? false, firing = drawable.SecondsSinceFired < 0.4f;
                var (start, count, step) = obj.IsEffectivelyDead ? Sequence(art, sequence, "Die1")
                    : firing ? Sequence(art, sequence, "FireUp") : moving ? Sequence(art, sequence, "Walk") : Sequence(art, sequence, "Ready");
                int stage = (int)(drawable.Clock * 12);
                stage = obj.IsEffectivelyDead ? Math.Min(stage, count - 1) : stage % count;
                int frame = start + Facing(apparent, 8) * step + stage;
                if (Ra2Texture(art, shape, Math.Clamp(frame, 0, shape.FrameCount - 1), team, art.UnitPalette, false) is { } texture)
                    Quad(texture, shape.Width, shape.Height, unitsPerPixel * SageMapScale(art, true), 0);
                return;
            }
            case "Building":
            {
                if (art.BuildingShape(image) is not { } shape)
                    return;
                int half = Math.Max(1, shape.FrameCount / 2);
                // The build-up plays when it is placed (Buildup=..., its frames over two and a half seconds).
                if (age < 2.5f && art.Art.Get(image, "Buildup") is { Length: > 0 } buildupName && art.BuildingShape(buildupName) is { } buildup)
                {
                    int frames = Math.Max(1, buildup.FrameCount / 2);
                    if (Ra2Texture(art, buildup, Math.Min(frames - 1, (int)(age / 2.5f * frames)), team, art.UnitPalette, false) is { } b)
                        Quad(b, buildup.Width, buildup.Height, unitsPerPixel, buildup.Height * 0.25f);
                    return;
                }
                bool damaged = obj.MaxHealth > 0 && obj.Health / obj.MaxHealth < 0.5f;
                int frame = damaged && half > 1 ? 1 : 0;
                if (Ra2Texture(art, shape, frame, team, art.UnitPalette, false) is { } texture)
                    Quad(texture, shape.Width, shape.Height, unitsPerPixel, shape.Height * 0.25f);
                // Its active animation: its own damaged one if it has one, else the anim's second half holds the damaged frames.
                string animName = art.Art.Get(image, "ActiveAnim");
                int damageOffset = 0;
                if (damaged && art.Art.Get(image, "ActiveAnimDamaged") is { Length: > 0 } damagedName && art.BuildingShape(damagedName) != null)
                    animName = damagedName;
                else if (damaged)
                    damageOffset = -1;
                if (animName.Length > 0 && art.BuildingShape(art.Art.Get(animName, "Image", animName)) is { } anim)
                {
                    int drawn = Math.Max(1, anim.FrameCount / 2); // the second half is shadows
                    if (damageOffset < 0)
                        damageOffset = drawn / 2;
                    // Start / LoopStart / LoopEnd (exclusive) and Rate (900 = a frame every game tick at 15 ticks a second).
                    int start = (int)art.Art.Num(animName, "Start", 0);
                    int loopStart = (int)art.Art.Num(animName, "LoopStart", start);
                    int loopEnd = (int)art.Art.Num(animName, "LoopEnd", damageOffset > 0 ? damageOffset : drawn);
                    loopEnd = Math.Clamp(loopEnd, loopStart + 1, drawn);
                    float fps = 15f * Math.Max(1, art.Art.Num(animName, "Rate", 900)) / 900f;
                    int animFrame = loopStart + (int)(drawable.Clock * fps) % (loopEnd - loopStart) + damageOffset;
                    if (Ra2Texture(art, anim, Math.Min(animFrame, drawn - 1), team, art.UnitPalette, false) is { } a)
                        Quad(a, anim.Width, anim.Height, unitsPerPixel, shape.Height * 0.25f, -1);
                }
                return;
            }
            default:
            {
                if (art.Shape(image) is not { } shape)
                    return;
                int frame = shape.FrameCount >= 32 ? Facing(apparent, 32) : 0;
                if (Ra2Texture(art, shape, frame, 0xFFFFFFFF, art.AnimPalette, false) is { } texture)
                    Quad(texture, shape.Width, shape.Height, unitsPerPixel, 0);
                return;
            }
        }
    }

    readonly HashSet<GameObject> ra2OrePiles = new();

    /// <summary>
    /// Red Alert 2's ore and gems and Tiberian Sun's Tiberium, flat on the ground: each pile's overlay frame (TIB01 / GEM01
    /// in the theater's art, its frame its richness) over its cell, a shape pixel as wide as the map's and twice as tall.
    /// </summary>
    public void RenderRa2Ore(Effects.EffectsRenderer renderer, Logic.GameLogic game)
    {
        if (ra2OrePiles.Count == 0)
            return;
        ra2OrePiles.RemoveWhere(p => p.IsDestroyed);
        foreach (var pile in ra2OrePiles)
        {
            var draw = Ra2Draw(pile);
            if (Ra2Art.For(draw.Settings.GetString("Game")) is not { } art)
                continue;
            string image = draw.Settings.GetString("Image");
            if (art.TheaterShape(image) is not { } shape)
                continue;
            int boxes = pile.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>()?.Boxes ?? 1;
            if (boxes <= 0)
                continue;
            bool gem = image.StartsWith("GEM", StringComparison.OrdinalIgnoreCase);
            int frames = Math.Max(1, Math.Min(12, shape.FrameCount / 2));
            int frame = Math.Clamp((gem ? boxes / 2 : boxes) - 1, 0, frames - 1);
            if (Ra2Texture(art, shape, frame, 0xFFFFFFFF, art.TheaterPalette, false) is not { } texture)
                continue;
            float upp = UnitsPerPixel2(art);
            float hw = shape.Width * upp / 2, hh = shape.Height * upp * 2 / 2;
            var c = pile.Position;
            Vector3 Corner(float dx, float dy) => new(c.X + dx, c.Y + dy, game.Terrain.GroundHeight(c.X + dx, c.Y + dy) + 0.4f);
            var a = Corner(-hw, hh); var b = Corner(hw, hh); var d = Corner(-hw, -hh); var e = Corner(hw, -hh);
            renderer.Triangle(texture, Effects.EffectBlend.Alpha, a, new(0, 0), b, new(1, 0), e, new(1, 1), Vector4.One);
            renderer.Triangle(texture, Effects.EffectBlend.Alpha, a, new(0, 0), e, new(1, 1), d, new(0, 1), Vector4.One);
        }
    }

    /// <summary>Plays a Red Alert 2 (or, tiberianSun, Tiberian Sun) animation (an explosion's SHP) once at a point.</summary>
    public void PlayRa2Anim(string name, Vector3 position, bool tiberianSun = false)
    {
        if (Ra2Art.For(tiberianSun ? "TS" : "") is { } art && art.Shape(name) is { } shape)
            ra2Anims.Add((art, shape, position, 0, Math.Max(1, art.Art.Bool(name, "Shadow") ? shape.FrameCount / 2 : shape.FrameCount), name));
    }

    /// <summary>
    /// An animation's frame after this many steps, with its art's loop (LoopStart..LoopEnd shown LoopCount times, then
    /// the rest: ELECTRO's sparks crackle five times before the soldier falls). -1 once it is over.
    /// </summary>
    static int AnimFrame(Ra2Art art, string name, int frames, int step)
    {
        int loopStart = Math.Clamp((int)art.Art.Num(name, "LoopStart", 0), 0, frames);
        int loopEnd = Math.Clamp((int)art.Art.Num(name, "LoopEnd", frames), loopStart, frames);
        int loopCount = Math.Max(1, (int)art.Art.Num(name, "LoopCount", 1));
        int loopLength = loopEnd - loopStart;
        if (step < loopStart)
            return step;
        step -= loopStart;
        if (step < loopLength * loopCount)
            return loopStart + step % Math.Max(1, loopLength);
        step -= loopLength * loopCount;
        return loopEnd + step < frames ? loopEnd + step : -1;
    }

    void AddRa2Anims(float seconds, Camera camera)
    {
        var view = camera.View;
        var up = new Vector3(view.M12, view.M22, view.M32);
        for (int i = ra2Anims.Count - 1; i >= 0; i--)
        {
            var (art, shape, position, t, frames, name) = ra2Anims[i];
            t += seconds;
            int frame = AnimFrame(art, name, frames, (int)(t * 15));
            if (frame < 0)
            {
                ra2Anims.RemoveAt(i);
                continue;
            }
            ra2Anims[i] = (art, shape, position, t, frames, name);
            if (Ra2Texture(art, shape, frame, 0xFFFFFFFF, art.AnimPalette, false) is not { } texture)
                continue;
            float upp = UnitsPerPixel2(art);
            quads.Add((Vector3.DistanceSquared(camera.Position, position) - 2, quads.Count, texture, position + up * (shape.Height * 0.25f * upp), position,
                shape.Width * upp / 2, shape.Height * upp / 2, Vector4.One));
        }
    }
}
