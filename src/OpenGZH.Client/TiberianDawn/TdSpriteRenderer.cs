using System.Numerics;
using OpenGZH.Client.Graphics;
using OpenGZH.Client.Dune2000;
using OpenGZH.Client.World;
using OpenGZH.Core.Rules;
using OpenGZH.Formats.Westwood;
using OpenGZH.Logic.Objects;
using OpenGZH.TiberianDawn;
using OpenGZH.TiberianDawn.Art;
using OpenGZH.TiberianDawn.Logic;
using OpenGZH.TiberianDawn.Logic.Objects;
using OpenGZH.TiberianDawn.Logic.Types;
using Silk.NET.OpenGL;

namespace OpenGZH.Client.TiberianDawn;

/// <summary>
/// Draws Tiberian Dawn objects in a Zero Hour engine match (Draw module TDSpriteDraw) with their original
/// sprites: upright quads facing the camera, TdScale.UnitsPerPixel world units a pixel (10% under a 36-unit cell),
/// the frame for the facing as seen from the camera, in the owner's colour. Each sprite is depth-tested
/// as a whole at its ground point: drawn before the 3D models, it is never cut by the terrain, and models in front still hide it.
/// </summary>
public sealed partial class TdSpriteRenderer : IDisposable
{
    public const float UnitsPerPixel = Logic.Modules.Behaviors.TdScale.UnitsPerPixel;

    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec3 aAnchor;
        layout(location = 3) in vec4 aColor;
        uniform mat4 uViewProjection;
        out vec2 vUV;
        out vec4 vColor;
        out vec2 vWorld;
        flat out float vDepth;
        void main()
        {
            vUV = aUV;
            vColor = aColor;
            vWorld = aAnchor.xy;
            vec4 anchor = uViewProjection * vec4(aAnchor, 1.0);
            vDepth = clamp(anchor.z / anchor.w * 0.5 + 0.5, 0.0, 1.0);
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUV;
        in vec4 vColor;
        in vec2 vWorld;
        flat in float vDepth;
        uniform sampler2D uTexture;
        uniform sampler2D uShroud;
        uniform vec2 uShroudScale;
        out vec4 fragColor;
        void main()
        {
            vec4 c = texture(uTexture, vUV) * vColor;
            if (c.a < 0.02)
                discard;
            if (uShroudScale.x > 0.0)
                c.rgb *= texture(uShroud, vWorld * uShroudScale).r;
            fragColor = c;
            gl_FragDepth = vDepth;
        }
        """;

    const int FloatsPerVertex = 12;

    readonly GL gl;
    TdArt? art;
    readonly TdTypes? types;
    // Red Alert's objects (Draw module RASpriteDraw): its art and types, read with TD's classes.
    readonly TdArt? raArt = RedAlertContent.Art;
    readonly TdTypes? raTypes = RedAlertContent.Types;
    readonly Dune2000.D2kArt? d2k = Dune2000.D2kArt.Get();
    readonly Dictionary<(Formats.Westwood.D2kSpriteFile File, int Frame, uint Color, int Mode), Texture?> d2kTextures = new();
    readonly ShaderProgram shader;
    readonly uint vao, vbo;
    readonly Dictionary<(KeyframeShape Shape, int Frame, uint Color, int Mode), Texture?> textures = new();
    readonly Dictionary<GameObject, Info?> infos = new();
    readonly Dictionary<uint, int> createdFrame = new();
    readonly List<TdShapeLayer> layers = new();
    readonly List<(float Distance, int Order, Texture Texture, Vector3 Center, Vector3 Anchor, float HalfW, float HalfH, Vector4 Color)> quads = new();
    float[] vertices = new float[FloatsPerVertex * 6 * 256];

    /// <summary>What the exported Draw module says the object is.</summary>
    sealed record Info(string Kind, string Shape, object? Type, Vector3 ArtOffset);

    public World.ShroudTexture? Shroud { get; set; }

    TdSpriteRenderer(GL gl, TdContent? content)
    {
        this.gl = gl;
        if (content != null)
        {
            art = TiberianDawnContent.Art ?? new TdArt(content, TheaterType.THEATER_TEMPERATE);
            types = new TdTypes(content.Rules);
        }
        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        unsafe
        {
            uint stride = FloatsPerVertex * sizeof(float);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        }
        gl.BindVertexArray(0);
    }

    /// <summary>A renderer, if the original Tiberian Dawn files or the Dune 2000 data are installed.</summary>
    public static TdSpriteRenderer? TryCreate(GL gl) =>
        TiberianDawnContent.Get() is { } content ? new TdSpriteRenderer(gl, content)
        : Dune2000.D2kArt.Get() != null || RedAlertContent.Available || RedAlert2.Ra2Art.Get() != null || RedAlert2.Ra2Art.GetTiberianSun() != null
            ? new TdSpriteRenderer(gl, null) : null;

    /// <summary>Whether this object is drawn as a sprite (Tiberian Dawn's or Dune 2000's).</summary>
    public static bool Handles(GameObject obj) =>
        obj.Template.ModulesOfType("Draw").Any(m => m.ClassName.Equals("TDSpriteDraw", StringComparison.OrdinalIgnoreCase)
            || m.ClassName.Equals("D2KSpriteDraw", StringComparison.OrdinalIgnoreCase)
            || m.ClassName.Equals("RASpriteDraw", StringComparison.OrdinalIgnoreCase)
            || m.ClassName.Equals("RA2SpriteDraw", StringComparison.OrdinalIgnoreCase));

    /// <summary>The object appeared (built, produced, or there from the start): its build-up plays from now.</summary>
    /// <summary>
    /// The creation frame of what was there before it was seen being made (on the map from the start, or made while no
    /// one was watching, as in the shell battle played ahead): long ago, so its build-up is over, but not so long that
    /// its animation clock overflows and freezes it on one frame.
    /// </summary>
    const int LongAgo = -20 * 60 * Logic.Core.LogicTime.FramesPerSecond;

    public void OnCreated(GameObject obj, bool atStart) => createdFrame[obj.Id] = atStart ? LongAgo : obj.Game.Frame;

    public void OnDestroyed(GameObject obj)
    {
        infos.Remove(obj);
        createdFrame.Remove(obj.Id);
    }

    Info? InfoFor(GameObject obj)
    {
        if (infos.TryGetValue(obj, out var info))
            return info;
        var draw = obj.Template.ModulesOfType("Draw").FirstOrDefault(m => m.ClassName.Equals("TDSpriteDraw", StringComparison.OrdinalIgnoreCase));
        if (draw == null && obj.Template.ModulesOfType("Draw").FirstOrDefault(m => m.ClassName.Equals("D2KSpriteDraw", StringComparison.OrdinalIgnoreCase)) is { } d2kDraw)
        {
            var d2kOffset = obj.Template.Rules.GetString("TdArtOffset") is { Length: > 0 } od ? RuleValue.Coord(od) : Vector3.Zero;
            info = new Info("D2K:" + d2kDraw.Settings.GetString("TdKind"), d2kDraw.Settings.GetString("Image"), d2kDraw.Settings.GetString("TdType"), d2kOffset with { Z = 0 });
            return infos[obj] = info;
        }
        if (draw == null && obj.Template.ModulesOfType("Draw").FirstOrDefault(m => m.ClassName.Equals("RASpriteDraw", StringComparison.OrdinalIgnoreCase)) is { } raDraw)
        {
            // Red Alert: TdType is the INI name; its type class comes from RA's own tables.
            var raOffset = obj.Template.Rules.GetString("TdArtOffset") is { Length: > 0 } ro ? RuleValue.Coord(ro) : Vector3.Zero;
            string raKind = raDraw.Settings.GetString("TdKind");
            object? raType = raKind == "Tiberium" ? RuleValue.Int(raDraw.Settings.GetString("TdType")) : raTypes?.FromName(raDraw.Settings.GetString("TdType"));
            info = new Info("RA:" + raKind, raDraw.Settings.GetString("Shape"), raType, raOffset with { Z = 0 });
            return infos[obj] = info;
        }
        if (draw != null && types != null)
        {
            string kind = draw.Settings.GetString("TdKind");
            string typeName = draw.Settings.GetString("TdType");
            object? type = kind switch
            {
                "Building" when Enum.TryParse<StructType>(typeName, out var t) => types.Building(t),
                "Unit" when Enum.TryParse<UnitType>(typeName, out var t) => types.Unit(t),
                "Infantry" when Enum.TryParse<InfantryType>(typeName, out var t) => types.Infantry(t),
                "Aircraft" when Enum.TryParse<AircraftType>(typeName, out var t) => types.Aircraft(t),
                "Tiberium" => RuleValue.Int(typeName),
                "Bullet" when Enum.TryParse<BulletType>(typeName, out var t) => types.Bullet(t),
                _ => null,
            };
            var offset = obj.Template.Rules.GetString("TdArtOffset") is { Length: > 0 } o ? RuleValue.Coord(o) : Vector3.Zero;
            info = new Info(kind, draw.Settings.GetString("Shape"), type, offset with { Z = 0 });
        }
        return infos[obj] = info;
    }

    /// <summary>Collects the object's sprite layers for this frame (call after the drawable has updated).</summary>
    public void Add(GameObject obj, Drawable drawable, Camera camera)
    {
        if (IsRa2(obj))
        {
            AddRa2(obj, drawable, camera);
            return;
        }
        if (InfoFor(obj) is not { } info)
            return;
        if (info.Kind.StartsWith("D2K:"))
        {
            AddD2k(obj, drawable, camera, info);
            return;
        }
        // TD's art, or Red Alert's for its objects (the same shape format and layers).
        var tdArt = art;
        if (info.Kind.StartsWith("RA:"))
            art = raArt;
        try
        {
            AddShapes(obj, drawable, camera, info);
        }
        finally
        {
            art = tdArt;
        }
    }

    void AddShapes(GameObject obj, Drawable drawable, Camera camera, Info info)
    {
        if (art == null || art.Shape(info.Shape) is not { } own)
            return;
        var view = camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        float screenUp = MathF.Atan2(camera.ScreenUp.Y, camera.ScreenUp.X);
        int created = createdFrame.TryGetValue(obj.Id, out int f) ? f : LongAgo;
        float turret = obj.AI is { Turrets.Count: > 0 } ai ? obj.Orientation + ai.Turrets[0].Angle : obj.Orientation;
        var state = new TdSpriteState
        {
            Dir = TdSpriteLayers.Dir(obj.Orientation, screenUp),
            TurretDir = TdSpriteLayers.Dir(turret, screenUp),
            HealthRatio = obj.MaxHealth > 0 ? (int)(obj.Health / obj.MaxHealth * 256) : 256,
            Age = Math.Max(0, obj.Game.Frame - created) / (float)Logic.Core.LogicTime.FramesPerSecond,
            Moving = obj.AI?.IsWalking ?? false,
            Firing = drawable.SecondsSinceFired < 0.3f,
            Harvesting = obj.AI is Logic.Modules.Supply.SupplyTruckAIUpdate { IsDocking: true } truck
                && obj.Game.FindObject(truck.DockTargetId)?.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() != null,
            Dying = obj.IsEffectivelyDead,
            Altitude = (int)(Math.Max(0, obj.HeightAboveTerrain) / UnitsPerPixel),
            Ammo = 2,
            RedAlert = info.Kind.StartsWith("RA:"),
        };
        layers.Clear();
        // Red Alert's ships (VesselClass::Draw_It): a 16-facing hull, and the turret (TURR, SSAM, MGUN) over it.
        if (info.Kind == "RA:Vessel" && info.Type is UnitTypeClass ship)
        {
            int dir16 = ((state.Dir + 8) / 16) & 15;
            int hull = ship.IniName is "LST" or "CARR" ? 0 : TdMath.BodyShape[dir16 * 2] >> 1;
            layers.Add(new TdShapeLayer(null, Math.Clamp(hull, 0, own.FrameCount - 1), 0, 0, TdShapeFlags.Remap));
            string? turretShape = ship.IniName switch { "CA" => "TURR", "DD" => "SSAM", "PT" => "MGUN", _ => null };
            if (turretShape != null)
            {
                int dir32 = ((state.TurretDir + 4) / 8) & 31;
                layers.Add(new TdShapeLayer(turretShape, TdMath.BodyShape[dir32], 0, 0, TdShapeFlags.Remap));
                if (ship.IniName == "CA")
                    layers.Add(new TdShapeLayer(turretShape, TdMath.BodyShape[dir32], 0, 8, TdShapeFlags.Remap));
            }
        }
        else switch (info.Type)
        {
            case BuildingTypeClass b:
                TdSpriteLayers.Building(b, state, art.Shape(b.IniName + "MAKE")?.FrameCount ?? 0, own.FrameCount, layers);
                break;
            case UnitTypeClass u:
                TdSpriteLayers.Unit(u, state, layers);
                break;
            case InfantryTypeClass i:
                TdSpriteLayers.Infantry(i, state, layers);
                break;
            case AircraftTypeClass a:
                TdSpriteLayers.Aircraft(a, state, layers);
                break;
            case int stage:
            {
                // A Tiberium patch shows its stage: one a supply box, so it thins as it is harvested and thickens as it grows.
                if (obj.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() is { } pile)
                    stage = pile.Boxes - 1;
                layers.Add(new TdShapeLayer(null, Math.Clamp(stage, 0, own.FrameCount - 1), 0, 0, TdShapeFlags.None));
                break;
            }
            case BulletTypeClass bullet:
            {
                RecordContrail(obj);
                // BulletClass::Shape_Number: turned to its flight direction (unless faceless); napalm and grenades tumble.
                float heading = obj.FindModule<Logic.Modules.Updates.PhysicsBehavior>() is { } physics && physics.Velocity with { Z = 0 } is { } v && v.LengthSquared() > 1e-4f
                    ? MathF.Atan2(v.Y, v.X) : obj.Orientation;
                int shapenum = bullet.IsFaceless ? 0 : TdMath.BodyShape[TdMath.Facing32[TdSpriteLayers.Dir(heading, screenUp)]];
                int ticks = (int)(state.Age * TdSpriteLayers.TicksPerSecond);
                if (bullet.Type == BulletType.BULLET_NAPALM)
                    shapenum += ticks % 6;
                if (bullet.Type == BulletType.BULLET_GRENADE)
                    shapenum += ticks % 8;
                layers.Add(new TdShapeLayer(null, Math.Clamp(shapenum, 0, own.FrameCount - 1), 0, 0, bullet.IsTranslucent ? TdShapeFlags.Ghost : TdShapeFlags.None));
                break;
            }
            default:
                layers.Add(new TdShapeLayer(null, 0, 0, 0, TdShapeFlags.None));
                break;
        }

        uint team = obj.Owner.IsNeutral ? 0xFFFFFFFF : obj.Owner.Color & 0xFFFFFF;
        var anchor = obj.Position + info.ArtOffset;
        float distance = Vector3.DistanceSquared(camera.Position, anchor);
        float opacity = drawable.HiddenByStealth ? 0 : 1;
        foreach (var layer in layers)
        {
            var shape = layer.File != null ? art.Shape(layer.File) : own;
            if (shape == null)
                continue;
            int mode = (layer.Flags & TdShapeFlags.Shadow) != 0 ? 1 : (layer.Flags & TdShapeFlags.Ghost) != 0 ? 2 : 0;
            uint color = (layer.Flags & TdShapeFlags.Remap) != 0 ? team : 0xFFFFFFFF;
            if (TextureFor(shape, layer.Frame, color, mode) is not { } texture)
                continue;
            var center = anchor + right * (layer.Dx * UnitsPerPixel) - up * (layer.Dy * UnitsPerPixel);
            // Shadows go just behind their owner so the owner draws over them.
            quads.Add((distance + (mode == 1 ? 1 : 0), quads.Count, texture, center, anchor, shape.Width * UnitsPerPixel / 2, shape.Height * UnitsPerPixel / 2,
                new Vector4(1, 1, 1, opacity)));
        }
    }

    // ---- Dune 2000 objects: their sequences (stand, run, shoot, idle, turret, make...) from the D2K art ----

    /// <summary>World units a Dune 2000 art pixel (32 pixels a cell; the same cell size as TD objects).</summary>
    const float D2kUnitsPerPixel = Logic.Modules.Behaviors.TdScale.Cell / 32f;

    // Harvesters unloading at a refinery: when each settled in, and when each began to lift out (drawable clock seconds).
    readonly Dictionary<uint, float> dockedAt = new(), undockedAt = new();

    void AddD2k(GameObject obj, Drawable drawable, Camera camera, Info info)
    {
        if (d2k == null)
            return;
        string image = info.Shape, kind = info.Kind[4..];
        float screenUp = MathF.Atan2(camera.ScreenUp.Y, camera.ScreenUp.X);
        float facing = obj.Orientation - screenUp; // counter-clockwise from screen-up
        int created = createdFrame.TryGetValue(obj.Id, out int f) ? f : LongAgo;
        float age = Math.Max(0, obj.Game.Frame - created) / (float)Logic.Core.LogicTime.FramesPerSecond;
        bool moving = obj.AI?.IsWalking ?? false, firing = drawable.SecondsSinceFired < 0.3f;
        float health = obj.MaxHealth > 0 ? obj.Health / obj.MaxHealth : 1;
        var d2kLayers = new List<(D2kSequence Seq, float Facing, int Step)>();
        var d2kOffsets = new Dictionary<int, (float X, float Y)>(); // per layer index: extra pixels
        int Step(D2kSequence s, float seconds) => (int)(seconds * 1000 / Math.Max(1, s.Tick));
        D2kSequence? Seq(params string[] names) => names.Select(n => d2k.Sequence(image, n)).FirstOrDefault(s => s != null);

        switch (kind)
        {
            case "Spice":
            {
                // Drawn flat on the ground with its neighbours (RenderSpice); old maps without cell data fall back to one tile.
                if (obj.MapData.Length > 0)
                {
                    spicePiles.Add(obj);
                    return;
                }
                if (Seq("spicea") is { } tile)
                    d2kLayers.Add((tile, 0, 1));
                break;
            }
            case "Worm":
            {
                // Mostly under the sand: its wake while moving, the mouth while it swallows, crackling otherwise.
                bool eating = obj.FindModule<Logic.Modules.Behaviors.D2kSandworm>() is { IsEating: true };
                var seq = eating ? Seq("mouth") : moving ? Seq("sand") : Seq("lightninga", "sand");
                if (seq != null)
                    d2kLayers.Add((seq, 0, Step(seq, age)));
                break;
            }
            case "Bloom":
            {
                if (obj.FindModule<Logic.Modules.Behaviors.D2kSpiceBloom>() is { Stage: >= 0 } bloom && Seq($"grow{bloom.Stage}") is { } grow)
                    d2kLayers.Add((grow, 0, 0));
                break;
            }
            case "Building":
            {
                if (Seq("make") is { } make && Step(make, age) < make.Length)
                {
                    d2kLayers.Add((make, 0, Step(make, age)));
                    break;
                }
                if ((health < 0.5f ? Seq("damaged-idle", "idle") : Seq("idle")) is { } idle)
                    d2kLayers.Add((idle, facing, Step(idle, age)));
                if (Seq(health < 0.5f ? "damaged-idle-top" : "idle-top", "idle-top") is { } top)
                    d2kLayers.Add((top, 0, Step(top, age)));
                // A refinery puffs smoke while a harvester unloads in it (WithDockedOverlay@SMOKE).
                if (obj.FindModule<Logic.Modules.Supply.SupplyCenterDockUpdate>() is { IsDockerInside: true } && Seq("smoke") is { } smoke)
                    d2kLayers.Add((smoke, 0, Step(smoke, age)));
                if (Seq("turret") is { } turret)
                {
                    float turretFacing = obj.AI is { Turrets.Count: > 0 } ai ? facing + ai.Turrets[0].Angle : facing;
                    d2kLayers.Add((turret, turretFacing, 0));
                }
                break;
            }
            case "Infantry":
            {
                var seq = obj.IsEffectivelyDead ? Seq("die1", "die-crushed", "stand") : firing ? Seq("shoot", "stand") : moving ? Seq("run", "stand") : Seq("stand", "idle");
                if (seq != null)
                    d2kLayers.Add((seq, facing, obj.IsEffectivelyDead ? Math.Min(Step(seq, age), seq.Length - 1) : moving || firing ? Step(seq, age) : 0));
                break;
            }
            case "Bullet":
            {
                RecordContrail(obj);
                float heading = obj.FindModule<Logic.Modules.Updates.PhysicsBehavior>() is { } physics && physics.Velocity with { Z = 0 } is { } v && v.LengthSquared() > 1e-4f
                    ? MathF.Atan2(v.Y, v.X) : obj.Orientation;
                if (Seq("idle") is { } seq)
                    d2kLayers.Add((seq, heading - screenUp, Step(seq, age)));
                break;
            }
            default:
            {
                bool harvesting = obj.AI is Logic.Modules.Supply.SupplyTruckAIUpdate { IsDocking: true } truck
                    && obj.Game.FindObject(truck.DockTargetId)?.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() != null;
                // Unloading at a refinery (WithDockingAnimation): the harvester settles into the dock ("dock"), sits
                // there while the spice comes off ("dock-loop"), and lifts out again (the dock frames backwards).
                bool unloading = obj.AI is Logic.Modules.Supply.SupplyTruckAIUpdate { IsDocking: true } docker && obj.HasCondition("DOCKING_ACTIVE") && !moving
                    && obj.Game.FindObject(docker.DockTargetId)?.FindModule<Logic.Modules.Supply.SupplyCenterDockUpdate>() is { } refinery
                    && Logic.Core.MathUtil.Distance2D(obj.Position, refinery.GetDockPosition(obj)) < 20;
                float now = drawable.Clock;
                if (unloading)
                {
                    undockedAt.Remove(obj.Id);
                    if (!dockedAt.ContainsKey(obj.Id))
                        dockedAt[obj.Id] = now;
                }
                else if (dockedAt.Remove(obj.Id, out _))
                    undockedAt[obj.Id] = now;
                var dockSeq = Seq("dock");
                if (unloading && dockSeq != null)
                {
                    int step = Step(dockSeq, now - dockedAt[obj.Id]);
                    if (step < dockSeq.Length || Seq("dock-loop") is not { } loop)
                        d2kLayers.Add((dockSeq, 0, Math.Min(step, dockSeq.Length - 1)));
                    else
                        d2kLayers.Add((loop, 0, Step(loop, now)));
                    break;
                }
                if (dockSeq != null && undockedAt.TryGetValue(obj.Id, out float leftAt))
                {
                    int step = Step(dockSeq, now - leftAt);
                    if (step < dockSeq.Length)
                    {
                        d2kLayers.Add((dockSeq, 0, dockSeq.Length - 1 - step));
                        break;
                    }
                    undockedAt.Remove(obj.Id);
                }
                if (Seq("idle") is { } body)
                    d2kLayers.Add((body, facing, Step(body, age)));
                if (harvesting && !moving && Seq("harvest") is { } harvest)
                    d2kLayers.Add((harvest, facing, Step(harvest, age)));
                if (Seq("turret") is { } turret)
                {
                    float turretFacing = obj.AI is { Turrets.Count: > 0 } ai ? facing + ai.Turrets[0].Angle : facing;
                    d2kLayers.Add((turret, turretFacing, 0));
                }
                break;
            }
        }

        var view = camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        uint team = obj.Owner.IsNeutral ? 0xFFFFFFFF : obj.Owner.Color & 0xFFFFFF;
        var anchor = obj.Position + info.ArtOffset;
        float distance = Vector3.DistanceSquared(camera.Position, anchor);
        for (int layerIndex = 0; layerIndex < d2kLayers.Count; layerIndex++)
        {
            var (seq, layerFacing, step) = d2kLayers[layerIndex];
            var extra = d2kOffsets.GetValueOrDefault(layerIndex);
            if (d2k.SpriteFile(seq.File) is not { } file)
                continue;
            int frameIndex = seq.FrameIndex(layerFacing, step);
            if (frameIndex < 0 || frameIndex >= file.Frames.Count)
                continue;
            var frame = file.Frames[frameIndex];
            if (D2kTexture(file, frameIndex, team, seq.Alpha < 1 ? 2 : 0) is not { } texture)
                continue;
            // The frame's origin pixel goes on the object; the sequence's offset moves it (pixels, y down).
            float cx = frame.Width / 2f - frame.OriginX + seq.OffsetX + extra.X, cy = frame.Height / 2f - frame.OriginY + seq.OffsetY + extra.Y;
            var center = anchor + right * (cx * D2kUnitsPerPixel) - up * (cy * D2kUnitsPerPixel);
            // Spice lies on the ground: everything stands on it.
            quads.Add((kind == "Spice" ? distance + 1e6f : distance, quads.Count, texture, center, anchor, frame.Width * D2kUnitsPerPixel / 2, frame.Height * D2kUnitsPerPixel / 2,
                new Vector4(1, 1, 1, drawable.HiddenByStealth ? 0 : 1)));
        }
    }

    Texture? D2kTexture(Formats.Westwood.D2kSpriteFile file, int frame, uint color, int mode)
    {
        var key = (file, frame, color, mode);
        if (d2kTextures.TryGetValue(key, out var texture))
            return texture;
        (byte, byte, byte)? team = color == 0xFFFFFFFF ? null : ((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        var image = file.ToImage(frame, team, alpha: mode == 2 ? 0.5f : 1);
        texture = image != null ? new Texture(gl, image, repeat: false, mipmaps: false, nearest: true) : null;
        return d2kTextures[key] = texture;
    }

    // ---- Smudges: scorch marks and craters (TD's SmudgeClass, OpenRA's SmudgeLayer), kept for the match ----

    const int MaxSmudges = 3000;
    readonly List<(KeyframeShape? Td, D2kSequence? D2k, int Frame, Vector3 Position)> smudges = new();

    /// <summary>Whether a point is sand (D2K craters come as sand or rock craters). Set by the world view.</summary>
    public Func<Vector3, bool>? IsSand { get; set; }

    void AddSmudge(KeyframeShape? td, D2kSequence? d2k, int frame, Vector3 position)
    {
        // A crater hit again deepens (OpenRA's smudge depth), else a new one.
        for (int i = 0; i < smudges.Count; i++)
            if (Vector2.DistanceSquared(new(smudges[i].Position.X, smudges[i].Position.Y), new(position.X, position.Y)) < 100)
            {
                var s = smudges[i];
                if (s.D2k != null && s.D2k == d2k)
                    smudges[i] = s with { Frame = Math.Min(s.Frame + 1, d2k.Length - 1) };
                return;
            }
        if (smudges.Count >= MaxSmudges)
            smudges.RemoveAt(0);
        smudges.Add((td, d2k, frame, position));
    }

    /// <summary>TD: a scorch mark (SC1-6) or a crater (CR1-6) at a point.</summary>
    public void AddTdSmudge(bool crater, Vector3 position)
    {
        if (art == null)
            return;
        string name = (crater ? "CR" : "SC") + Random.Shared.Next(1, 7);
        if (art.Shape(name) is { } shape)
            AddSmudge(shape, null, 0, position);
    }

    /// <summary>Dune 2000: a sand or rock crater (rockcraters/sandcraters, one of two kinds) at a point.</summary>
    public void AddD2kCrater(Vector3 position)
    {
        if (d2k == null)
            return;
        bool sand = IsSand?.Invoke(position) ?? true;
        string image = sand ? "sandcraters" : "rockcraters";
        if (d2k.Sequence(image, (sand ? "sandcrater" : "rockcrater") + Random.Shared.Next(1, 3)) is { } seq)
            AddSmudge(null, seq, 0, position);
    }

    /// <summary>A building's death: craters over its cells (BuildingClass's crater ring; d2k's BuildingExplode).</summary>
    public void BuildingDestroyed(IEnumerable<Vector3> cells, bool d2kBuilding)
    {
        foreach (var cell in cells)
        {
            if (d2kBuilding)
            {
                AddD2kCrater(cell);
                if (Random.Shared.Next(2) == 0)
                    PlayD2kAnim("explosion/building", cell);
            }
            else
            {
                AddTdSmudge(crater: true, cell);
                if (Random.Shared.Next(2) == 0)
                    PlayAnim("ANIM_FIRE_SMALL", cell);
            }
        }
    }

    void AddSmudges(Camera camera)
    {
        var view = camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        foreach (var (td, seq, frame, position) in smudges)
        {
            float distance = Vector3.DistanceSquared(camera.Position, position) + 1.5e6f;
            if (td != null && art != null)
            {
                if (TextureFor(td, Math.Min(frame, td.FrameCount - 1), 0xFFFFFFFF, 0) is { } texture)
                    quads.Add((distance, quads.Count, texture, position, position, td.Width * UnitsPerPixel / 2, td.Height * UnitsPerPixel / 2, Vector4.One));
            }
            else if (seq != null && d2k?.SpriteFile(seq.File) is { } file)
            {
                int index = seq.FrameIndex(0, frame);
                if (index < 0 || index >= file.Frames.Count || D2kTexture(file, index, 0xFFFFFFFF, 0) is not { } texture)
                    continue;
                var f = file.Frames[index];
                float cx = f.Width / 2f - f.OriginX + seq.OffsetX + 16, cy = f.Height / 2f - f.OriginY + seq.OffsetY + 16;
                var center = position + right * (cx * D2kUnitsPerPixel) - up * (cy * D2kUnitsPerPixel);
                quads.Add((distance, quads.Count, texture, center, position, f.Width * D2kUnitsPerPixel / 2, f.Height * D2kUnitsPerPixel / 2, Vector4.One));
            }
        }
    }

    /// <summary>Plays a Dune 2000 animation ("explosion/small_napalm": image and sequence) once at a point.</summary>
    public void PlayD2kAnim(string imageAndSequence, Vector3 position)
    {
        if (imageAndSequence == "crater")
        {
            AddD2kCrater(position);
            return;
        }
        if (d2k == null || imageAndSequence.Split('/') is not [var image, var sequence] || d2k.Sequence(image, sequence) is not { } seq)
            return;
        d2kAnims.Add((seq, position, 0));
    }

    readonly List<(D2kSequence Seq, Vector3 Position, float Seconds)> d2kAnims = new();

    /// <summary>Concrete laid on a Dune 2000 map: its tile (concretea's preview, BLOXBASE 657) on each cell, under everything.</summary>
    public void AddD2kConcrete(IReadOnlyList<Vector3> cells, Camera camera)
    {
        if (d2k == null || cells.Count == 0 || d2k.Sequence("concretea", "preview") is not { } seq || d2k.SpriteFile(seq.File) is not { } file)
            return;
        int frameIndex = seq.FrameIndex(0, 0);
        if (frameIndex < 0 || frameIndex >= file.Frames.Count || D2kTexture(file, frameIndex, 0xFFFFFFFF, 0) is not { } texture)
            return;
        var frame = file.Frames[frameIndex];
        foreach (var cell in cells)
            quads.Add((Vector3.DistanceSquared(camera.Position, cell) + 2e6f, quads.Count, texture, cell, cell,
                frame.Width * D2kUnitsPerPixel / 2, frame.Height * D2kUnitsPerPixel / 2, Vector4.One));
    }

    void AddD2kAnims(float seconds, Camera camera)
    {
        if (d2k == null)
            return;
        var view = camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);
        for (int i = d2kAnims.Count - 1; i >= 0; i--)
        {
            var (seq, position, t) = d2kAnims[i];
            t += seconds;
            int step = (int)(t * 1000 / Math.Max(1, seq.Tick));
            if (step >= seq.Length || d2k.SpriteFile(seq.File) is not { } file)
            {
                d2kAnims.RemoveAt(i);
                continue;
            }
            d2kAnims[i] = (seq, position, t);
            int frameIndex = seq.FrameIndex(0, step);
            if (frameIndex < 0 || frameIndex >= file.Frames.Count || D2kTexture(file, frameIndex, 0xFFFFFFFF, seq.Alpha < 1 ? 2 : 0) is not { } texture)
                continue;
            var frame = file.Frames[frameIndex];
            float cx = frame.Width / 2f - frame.OriginX + seq.OffsetX, cy = frame.Height / 2f - frame.OriginY + seq.OffsetY;
            var center = position + right * (cx * D2kUnitsPerPixel) - up * (cy * D2kUnitsPerPixel);
            quads.Add((Vector3.DistanceSquared(camera.Position, position) - 2, quads.Count, texture, center, position,
                frame.Width * D2kUnitsPerPixel / 2, frame.Height * D2kUnitsPerPixel / 2, Vector4.One));
        }
    }

    // ---- TD animations (muzzle flashes, explosions): AnimClass, played on their own ----

    sealed class PlayingAnim
    {
        public required AnimTypeClass Type;
        public required Vector3 Position;
        /// <summary>A Red Alert animation (drawn from RA's art).</summary>
        public bool Ra;
        public float Ticks;
        public int Loops;
    }

    readonly List<PlayingAnim> anims = new();

    /// <summary>Plays a Red Alert animation (its INI name: GUNFIRE, FBALL1, PIFF...) once at a point.</summary>
    public void PlayRaAnim(string iniName, Vector3 position)
    {
        if (raTypes?.FromName<AnimTypeClass>(iniName) is { } cls)
            anims.Add(new PlayingAnim { Type = cls, Position = position, Loops = Math.Max(cls.Loops, 1), Ra = true });
    }

    /// <summary>
    /// Plays a TD animation ("ANIM_PIFF") at a point. Directional ones (TechnoClass::Fire_At's gun, chem and
    /// flame flashes) take the eighth of the compass the shot went towards.
    /// </summary>
    public void PlayAnim(string animName, Vector3 position, float? towards = null, Camera? camera = null)
    {
        if (types == null || !Enum.TryParse<AnimType>(animName, out var type) || type == AnimType.ANIM_NONE)
            return;
        if (type is AnimType.ANIM_GUN_N or AnimType.ANIM_CHEM_N or AnimType.ANIM_FLAME_N && towards is { } angle && camera != null)
        {
            byte dir = TdSpriteLayers.Dir(angle, MathF.Atan2(camera.ScreenUp.Y, camera.ScreenUp.X));
            type = (AnimType)((int)type + ((dir + 16) / 32 % 8));
        }
        var cls = types.Anim(type);
        anims.Add(new PlayingAnim { Type = cls, Position = position, Loops = Math.Max(cls.Loops, 1) });
        // AnimClass::Middle: scorchers and crater-formers mark the ground.
        if (cls.IsScorcher)
            AddTdSmudge(crater: false, position);
        if (cls.IsCraterForming)
            AddTdSmudge(crater: true, position);
    }

    /// <summary>Steps the animations (AnimClass::AI: a frame each Delay ticks, loops, then ChainTo) and collects their frames.</summary>
    public void AddAnims(float seconds, Camera camera)
    {
        AgeContrails(seconds);
        AddSmudges(camera);
        AddRa2Anims(seconds, camera);
        AddD2kAnims(seconds, camera);
        if (art == null && raArt == null)
            return;
        var tdArtForAnims = art;
        try
        {
            AddTdAnims(seconds, camera, tdArtForAnims);
        }
        finally
        {
            art = tdArtForAnims;
        }
    }

    void AddTdAnims(float seconds, Camera camera, TdArt? tdArtForAnims)
    {
        var view = camera.View;
        for (int i = anims.Count - 1; i >= 0; i--)
        {
            var a = anims[i];
            a.Ticks += seconds * TdSpriteLayers.TicksPerSecond;
            var cls = a.Type;
            // Red Alert's animations come from its own art (and palette).
            art = a.Ra ? raArt : tdArtForAnims;
            if (art?.Shape(cls.IniName) is not { } shape)
            {
                anims.RemoveAt(i);
                continue;
            }
            int stages = cls.Stages > 0 ? cls.Stages : shape.FrameCount - cls.Start;
            int stage = (int)(a.Ticks / Math.Max(cls.Delay, 1));
            bool looping = a.Loops > 1 && cls.LoopEnd > cls.LoopStart;
            if (looping ? stage >= cls.LoopEnd - cls.Start : stage >= stages)
            {
                if (--a.Loops > 0 && looping)
                {
                    a.Ticks = (cls.LoopStart - cls.Start) * Math.Max(cls.Delay, 1);
                    stage = cls.LoopStart - cls.Start;
                }
                else if (cls.ChainTo != AnimType.ANIM_NONE && (a.Ra ? raTypes : types) is { } chainTypes && chainTypes.HasAnim(cls.ChainTo))
                {
                    // (A Red Alert animation chains within RA's table: its enum values are RA's.)
                    a.Type = chainTypes.Anim(cls.ChainTo);
                    a.Ticks = 0;
                    a.Loops = Math.Max(a.Type.Loops, 1);
                    continue;
                }
                else
                {
                    anims.RemoveAt(i);
                    continue;
                }
            }
            int frame = cls.Start + stage;
            if (TextureFor(shape, frame, 0xFFFFFFFF, cls.IsTranslucent ? 2 : 0) is not { } texture)
                continue;
            float distance = Vector3.DistanceSquared(camera.Position, a.Position) - 2; // over what it hit
            quads.Add((distance, quads.Count, texture, a.Position, a.Position, shape.Width * UnitsPerPixel / 2, shape.Height * UnitsPerPixel / 2, Vector4.One));
        }
    }

    Texture? TextureFor(KeyframeShape shape, int frame, uint color, int mode)
    {
        var key = (shape, frame, color, mode);
        if (textures.TryGetValue(key, out var texture))
            return texture;
        var image = color == 0xFFFFFFFF
            ? art.Frame(shape, frame, null, shadowOnly: mode == 1, ghost: mode == 2)
            : art.Frame(shape, frame, ((byte)(color >> 16), (byte)(color >> 8), (byte)color), shadowOnly: mode == 1, ghost: mode == 2);
        texture = image != null ? new Texture(gl, image, repeat: false, mipmaps: false, nearest: true) : null;
        return textures[key] = texture;
    }

    // ---- Contrails (OpenRA's ContrailRenderable): a white ribbon through the projectile's last positions ----

    /// <summary>OpenRA's game ticks a second (a contrail is ContrailLength ticks long).</summary>
    const float ContrailTicksPerSecond = 25;

    /// <summary>OpenRA's default ContrailWidth: 64 of a cell's 1024.</summary>
    static readonly float ContrailWidth = Logic.Modules.Behaviors.TdScale.Cell * 64 / 1024f;

    sealed class Trail
    {
        public readonly List<(Vector3 Position, float Time)> Points = new();
        public float Seconds;
    }

    readonly Dictionary<uint, Trail> trails = new();
    float clock;
    Texture? white;

    /// <summary>Remembers where a projectile with a contrail (TdContrailLength ticks) is now.</summary>
    void RecordContrail(GameObject obj)
    {
        int length = RuleValue.Int(obj.Template.Rules.GetString("TdContrailLength"));
        if (length <= 0)
            return;
        if (!trails.TryGetValue(obj.Id, out var trail))
            trails[obj.Id] = trail = new Trail { Seconds = length / ContrailTicksPerSecond };
        if (trail.Points.Count == 0 || Vector3.DistanceSquared(trail.Points[^1].Position, obj.Position) > 0.01f)
            trail.Points.Add((obj.Position, clock));
    }

    /// <summary>Ages the contrails (the tail fades over its length; a spent projectile's trail fades out).</summary>
    void AgeContrails(float seconds)
    {
        clock += seconds;
        foreach (var (id, trail) in trails.ToList())
        {
            trail.Points.RemoveAll(p => clock - p.Time > trail.Seconds);
            if (trail.Points.Count == 0)
                trails.Remove(id);
        }
    }

    /// <summary>Appends the contrails' triangles to the vertex buffer; returns the vertex count added.</summary>
    int AppendContrails(ref int v, Camera camera)
    {
        int count = 0;
        var forward = Vector3.Normalize(camera.Target - camera.Position);
        foreach (var trail in trails.Values)
            for (int i = 1; i < trail.Points.Count; i++)
            {
                var (a, ta) = trail.Points[i - 1];
                var (b, tb) = trail.Points[i];
                var along = b - a;
                if (along.LengthSquared() < 1e-6f)
                    continue;
                var side = Vector3.Cross(along, forward);
                if (side.LengthSquared() < 1e-8f)
                    continue;
                side = Vector3.Normalize(side) * (ContrailWidth / 2);
                // OpenRA fades from the start colour (white) at the head to transparent at the tail.
                float alphaA = Math.Clamp(1 - (clock - ta) / trail.Seconds, 0, 1), alphaB = Math.Clamp(1 - (clock - tb) / trail.Seconds, 0, 1);
                int needed = (v + 6 * FloatsPerVertex);
                if (vertices.Length < needed)
                    Array.Resize(ref vertices, needed * 2);
                Put(ref v, a - side, alphaA); Put(ref v, a + side, alphaA); Put(ref v, b + side, alphaB);
                Put(ref v, a - side, alphaA); Put(ref v, b + side, alphaB); Put(ref v, b - side, alphaB);
                count += 6;
            }
        return count;

        void Put(ref int at, Vector3 p, float alpha)
        {
            vertices[at++] = p.X; vertices[at++] = p.Y; vertices[at++] = p.Z;
            vertices[at++] = 0.5f; vertices[at++] = 0.5f;
            vertices[at++] = p.X; vertices[at++] = p.Y; vertices[at++] = p.Z; // its own depth
            vertices[at++] = 1; vertices[at++] = 1; vertices[at++] = 1; vertices[at++] = alpha;
        }
    }

    /// <summary>Draws everything collected since the last call, far to near, and starts over.</summary>
    public unsafe void Draw(Matrix4x4 viewProjection, Camera camera)
    {
        if (quads.Count == 0 && trails.Count == 0)
            return;
        // Far to near; an object's layers in the order Draw_It draws them.
        quads.Sort((a, b) => b.Distance != a.Distance ? b.Distance.CompareTo(a.Distance) : a.Order.CompareTo(b.Order));
        var view = camera.View;
        var right = new Vector3(view.M11, view.M21, view.M31);
        var up = new Vector3(view.M12, view.M22, view.M32);

        int needed = quads.Count * 6 * FloatsPerVertex;
        if (vertices.Length < needed)
            Array.Resize(ref vertices, needed);
        int v = 0;
        foreach (var q in quads)
        {
            var r = right * q.HalfW;
            var u = up * q.HalfH;
            Put(q.Center - r + u, 0, 0); Put(q.Center + r + u, 1, 0); Put(q.Center + r - u, 1, 1);
            Put(q.Center - r + u, 0, 0); Put(q.Center + r - u, 1, 1); Put(q.Center - r - u, 0, 1);

            void Put(Vector3 p, float s, float t)
            {
                vertices[v++] = p.X; vertices[v++] = p.Y; vertices[v++] = p.Z;
                vertices[v++] = s; vertices[v++] = t;
                vertices[v++] = q.Anchor.X; vertices[v++] = q.Anchor.Y; vertices[v++] = q.Anchor.Z;
                vertices[v++] = q.Color.X; vertices[v++] = q.Color.Y; vertices[v++] = q.Color.Z; vertices[v++] = q.Color.W;
            }
        }
        int quadVertices = v / FloatsPerVertex;
        int contrailVertices = AppendContrails(ref v, camera);

        shader.Use();
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uTexture", 0);
        shader.Set("uShroud", 2);
        shader.Set("uShroudScale", Shroud != null ? new Vector2(1 / Shroud.WorldWidth, 1 / Shroud.WorldHeight) : Vector2.Zero);
        Shroud?.Bind(2);
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(v * sizeof(float)), p, BufferUsageARB.StreamDraw);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Disable(EnableCap.CullFace);
        // The terrain never hides a sprite (TD art stands on flat ground, and its lower half reaches over
        // the ground in front); sprites are painted far to near and write their anchor's depth, so 3D
        // models drawn afterwards still go behind or in front of them correctly.
        gl.DepthFunc(DepthFunction.Always);
        for (int i = 0; i < quads.Count; i++)
        {
            quads[i].Texture.Bind(0);
            gl.DrawArrays(PrimitiveType.Triangles, i * 6, 6);
        }
        if (contrailVertices > 0)
        {
            // Contrails go behind what is in front of them, and don't hide anything.
            white ??= new Texture(gl, WhitePixel(), repeat: false, mipmaps: false);
            white.Bind(0);
            gl.DepthFunc(DepthFunction.Lequal);
            gl.DepthMask(false);
            gl.DrawArrays(PrimitiveType.Triangles, quadVertices, (uint)contrailVertices);
            gl.DepthMask(true);
        }
        gl.BindVertexArray(0);
        gl.DepthFunc(DepthFunction.Less);
        gl.Disable(EnableCap.Blend);
        quads.Clear();
    }

    static Formats.Images.Image WhitePixel()
    {
        var image = new Formats.Images.Image(1, 1);
        image.SetPixel(0, 0, 255, 255, 255, 255);
        return image;
    }

    public void Dispose()
    {
        white?.Dispose();
        foreach (var texture in textures.Values)
            texture?.Dispose();
        shader.Dispose();
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
    }
}
