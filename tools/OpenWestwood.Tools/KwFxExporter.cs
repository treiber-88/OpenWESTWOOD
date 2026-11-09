using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace OpenWestwood.Tools;

public static partial class KwFactionExporter
{
    /// <summary>
    /// Kane's Wrath's effects: FXLists (fx-lists.yaml, "KWFX_&lt;id&gt;"), the FXParticleSystemTemplates they start
    /// (particle-systems.yaml, "KWPS_&lt;id&gt;") and the projectile objects weapons fire (objects "KW_&lt;id&gt;").
    /// Kane's Wrath's particle systems are Zero Hour's grown up: the same lifetimes, sizes, bursts, emission volumes and
    /// velocities, with alpha and color keyframes at relative ages (percent of a particle's life) instead of frames, textures
    /// that can be sheets of animation frames, and a few new kinds (GPU particles = particles, trails = streaks). Not converted:
    /// lightning and swarm particles (drawn as plain particles), decals, camera shakers.
    /// </summary>
    sealed partial class Exporter
    {
        readonly StringBuilder fxLists = new("FXLists:\n");
        readonly StringBuilder particles = new("ParticleSystems:\n");
        readonly HashSet<string> doneFx = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> doneParticles = new(StringComparer.OrdinalIgnoreCase);
        readonly Queue<string> pendingFx = new(), pendingParticles = new();
        readonly HashSet<string> doneProjectiles = new(StringComparer.OrdinalIgnoreCase);
        readonly StringBuilder projectiles = new("Objects:\n");
        public int FxCount => doneFx.Count;
        public int ParticleCount => doneParticles.Count;

        /// <summary>The yaml name of a Kane's Wrath FXList, queued for export; "" if there is none.</summary>
        string Fx(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "";
            id = id.Trim();
            if (x.Get("FXList", id) == null)
                return "";
            if (doneFx.Add(id))
                pendingFx.Enqueue(id);
            return "KWFX_" + id;
        }

        /// <summary>The yaml name of a Kane's Wrath particle system, queued for export; "" if there is none.</summary>
        string Ps(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "";
            id = id.Trim();
            if (x.Get("FXParticleSystemTemplate", id) is not { } system)
                return "";
            // Heat haze: a normal map (FXSphereNRM...) bending what is behind it. Not drawn yet (as a picture it is a square).
            if (x.Attr(system, "ParticleTexture").Contains("NRM", StringComparison.OrdinalIgnoreCase))
                return "";
            if (doneParticles.Add(id))
                pendingParticles.Enqueue(id);
            return "KWPS_" + id;
        }

        static string G(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>A ClientRandomVariable (Low/High attributes) as "low high".</summary>
        string Range(XElement? e, float fallback)
        {
            if (e == null)
                return $"{G(fallback)} {G(fallback)}";
            float low = x.Num(e, "Low", fallback), high = x.Num(e, "High", low);
            return $"{G(low)} {G(high)}";
        }

        string Coord(XElement? e) => e == null ? "X:0 Y:0 Z:0" : $"X:{G(x.Num(e, "x"))} Y:{G(x.Num(e, "y"))} Z:{G(x.Num(e, "z"))}";

        // ---- FX lists ----

        void WriteFxList(string id)
        {
            var fx = x.Get("FXList", id)!;
            var nuggets = new StringBuilder();
            foreach (var n in KwXml.Child(fx, "NuggetList")?.Elements() ?? [])
            {
                switch (n.Name.LocalName)
                {
                    case "ParticleSystem":
                        if (Ps(x.Attr(n, "Particle")) is { Length: > 0 } ps)
                        {
                            nuggets.Append($"    - Type: ParticleSystem\n      Name: {ps}\n");
                            if (x.Bool(n, "OrientToObject"))
                                nuggets.Append("      OrientToObject: Yes\n");
                            if (x.Bool(n, "CreateAtGroundHeight"))
                                nuggets.Append("      CreateAtGroundHeight: Yes\n");
                            if (x.Bool(n, "Ricochet"))
                                nuggets.Append("      Ricochet: Yes\n");
                            if (KwXml.Child(n, "Offset") is { } offset)
                                nuggets.Append($"      Offset: {Coord(offset)}\n");
                        }
                        break;
                    case "Sound":
                        if (Snd(x.Attr(n, "Value")) is { Length: > 0 } sound)
                            nuggets.Append($"    - Type: Sound\n      Name: {sound}\n");
                        break;
                    case "ViewShake":
                        nuggets.Append($"    - Type: ViewShake\n      Shake: {x.Attr(n, "Type", "NORMAL").ToUpperInvariant()}\n");
                        break;
                    case "TerrainScorch":
                        nuggets.Append($"    - Type: TerrainScorch\n      Scorch: RANDOM\n      Radius: {G(x.Num(n, "Radius", 15))}\n");
                        break;
                    case "LightPulse":
                    {
                        var c = KwXml.Child(n, "Color");
                        int R(string k) => (int)Math.Clamp(x.Num(c, k, 1) * 255, 0, 255);
                        nuggets.Append($"    - Type: LightPulse\n      Color: R:{R("r")} G:{R("g")} B:{R("b")}\n      Radius: {G(x.Num(n, "Radius", 30))}\n");
                        nuggets.Append($"      IncreaseTime: {G(x.Ms(n, "IncreaseTime"))}\n      DecreaseTime: {G(x.Ms(n, "DecreaseTime", 500))}\n");
                        break;
                    }
                    case "FXListAtBonePos":
                        if (Fx(x.Attr(n, "FX")) is { Length: > 0 } inner)
                            nuggets.Append($"    - Type: FXListAtBonePos\n      FX: {inner}\n      BoneName: {x.Attr(n, "Bone").ToUpperInvariant()}\n      OrientToBone: Yes\n");
                        break;
                }
            }
            fxLists.Append(nuggets.Length > 0 ? $"  KWFX_{id}:\n    Nuggets:\n{nuggets}" : $"  KWFX_{id}: {{}}\n");
        }

        // ---- Particle systems ----

        void WriteParticleSystem(string id)
        {
            var p = x.Get("FXParticleSystemTemplate", id)!;
            var sb = particles;
            sb.Append($"  KWPS_{id}:\n");
            string priority = x.Attr(p, "Priority").ToUpperInvariant();
            sb.Append($"    Priority: {(priority == "ALWAYS_RENDER" ? "ALWAYS_RENDER" : "WEAPON_EXPLOSION")}\n");
            sb.Append($"    IsOneShot: {(x.Bool(p, "IsOneShot") ? "Yes" : "No")}\n");
            string shader = x.Attr(p, "Shader", "ADDITIVE").ToUpperInvariant();
            sb.Append($"    Shader: {(shader.StartsWith("ADDITIVE") ? "ADDITIVE" : shader == "ALPHA_TEST" ? "ALPHA_TEST" : shader == "MULTIPLY" ? "INVERSE_MULTIPLY" : "ALPHA")}\n");
            string type = x.Attr(p, "Type", "PARTICLE").ToUpperInvariant();
            var draw = KwXml.Child(p, "Draw")?.Elements().FirstOrDefault();
            bool drawable = type == "DRAWABLE" && x.Attr(p, "Drawable").Length > 0;
            sb.Append($"    Type: {(drawable ? "DRAWABLE" : type == "TRAIL" ? "TRAIL" : type == "STREAK" ? "STREAK" : "PARTICLE")}\n");
            // A trail: each particle leaves a ribbon of where it has been, each point fading over TrailLife frames.
            if (type == "TRAIL")
                sb.Append($"    TrailLife: {G(x.Num(KwXml.Child(KwXml.Child(p, "Draw"), "Trail"), "TrailLife", 10))}\n");
            if (x.Bool(KwXml.Child(KwXml.Child(p, "Physics"), "Default"), "ParticlesAttachToBone"))
                sb.Append("    ParticlesAttachToEmitter: Yes\n");
            sb.Append($"    ParticleName: {(drawable ? P + x.Attr(p, "Drawable") : OpenWestwood.Formats.Sage.SageW3d.TextureFolder + x.Attr(p, "ParticleTexture", "EXWhiteDot") + ".dds")}\n");
            if (draw?.Name.LocalName == "Gpu" && x.Num(draw, "TotalFrames", 1) > 1)
                sb.Append($"    FramesPerRow: {G(x.Num(draw, "FramesPerRow", 1))}\n    TotalFrames: {G(x.Num(draw, "TotalFrames", 1))}\n    FrameSpeed: {G(x.Num(draw, "SpeedMultiplier", 1))}\n");

            var update = KwXml.Child(p, "Update")?.Elements().FirstOrDefault();
            var physics = KwXml.Child(KwXml.Child(p, "Physics"), "Default");
            sb.Append($"    AngleZ: {Range(KwXml.Child(update, "AngleZ"), 0)}\n    AngularRateZ: {Range(KwXml.Child(update, "AngularRateZ"), 0)}\n");
            sb.Append($"    AngularDamping: {Range(KwXml.Child(update, "AngularDamping") ?? KwXml.Child(update, "AngularDampingXY"), 1)}\n");
            sb.Append($"    VelocityDamping: {Range(KwXml.Child(physics, "VelocityDamping"), 1)}\n");
            sb.Append($"    Gravity: {G(x.Num(physics, "Gravity"))}\n");
            if (KwXml.Child(physics, "DriftVelocity") is { } drift)
                sb.Append($"    DriftVelocity: {Coord(drift)}\n");

            var life = KwXml.Child(p, "Lifetime");
            float meanLife = (x.Num(life, "Low", 30) + x.Num(life, "High", x.Num(life, "Low", 30))) / 2;
            sb.Append($"    Lifetime: {Range(life, 30)}\n    SystemLifetime: {G(x.Num(p, "SystemLifetime"))}\n");
            sb.Append($"    Size: {Range(KwXml.Child(p, "Size"), 1)}\n    StartSizeRate: {Range(KwXml.Child(p, "StartSizeRate"), 0)}\n");
            if (update?.Name.LocalName == "RenderObject")
                sb.Append($"    SizeRate: {Range(KwXml.Child(KwXml.Child(update, "SizeRate"), "x"), 0)}\n    SizeRateDamping: {Range(KwXml.Child(KwXml.Child(update, "SizeDamping"), "x"), 1)}\n");
            else
                sb.Append($"    SizeRate: {Range(KwXml.Child(update, "SizeRate"), 0)}\n    SizeRateDamping: {Range(KwXml.Child(update, "SizeRateDamping"), 1)}\n");

            // Keyframes at relative ages become frames of the mean lifetime (each later than the one before).
            int Frame(XElement k, int index, ref int last)
            {
                int frame = index == 0 ? 0 : Math.Max(last + 1, (int)MathF.Round(x.Num(k, "RelativeAge") / 100f * meanLife));
                last = frame;
                return frame;
            }
            var alphas = KwXml.Children(KwXml.Child(p, "Alphas"), "Alpha").Take(8).ToList();
            if (alphas.Count == 0)
                sb.Append("    Alpha1: 1 1 0\n");
            int lastFrame = 0;
            for (int i = 0; i < alphas.Count; i++)
                sb.Append($"    Alpha{i + 1}: {Range(alphas[i], 1)} {Frame(alphas[i], i, ref lastFrame)}\n");
            var colorsNode = KwXml.Child(p, "Colors");
            var colors = KwXml.Children(colorsNode, "Color").Take(8).ToList();
            if (colors.Count == 0)
                sb.Append("    Color1: R:255 G:255 B:255 0\n");
            lastFrame = 0;
            for (int i = 0; i < colors.Count; i++)
            {
                var c = KwXml.Child(colors[i], "Color");
                int C(string k) => (int)Math.Clamp(MathF.Round(x.Num(c, k, 1) * 255), 0, 255);
                sb.Append($"    Color{i + 1}: R:{C("r")} G:{C("g")} B:{C("b")} {Frame(colors[i], i, ref lastFrame)}\n");
            }
            // (Not ColorScale: Zero Hour adds it to the color every frame, but Kane's Wrath's values, up to ±250, can't mean that.)
            sb.Append($"    BurstDelay: {Range(KwXml.Child(p, "BurstDelay"), 0)}\n    BurstCount: {Range(KwXml.Child(p, "BurstCount"), 1)}\n    InitialDelay: {Range(KwXml.Child(p, "InitialDelay"), 0)}\n");

            var velocity = KwXml.Child(p, "Velocity")?.Elements().FirstOrDefault();
            switch (velocity?.Name.LocalName)
            {
                case "Ortho":
                    var pos = KwXml.Child(velocity, "Position");
                    sb.Append($"    VelocityType: ORTHO\n    VelOrthoX: {Range(KwXml.Child(pos, "x"), 0)}\n    VelOrthoY: {Range(KwXml.Child(pos, "y"), 0)}\n    VelOrthoZ: {Range(KwXml.Child(pos, "z"), 0)}\n");
                    break;
                case "Outward":
                    sb.Append($"    VelocityType: OUTWARD\n    VelOutward: {Range(KwXml.Child(velocity, "Speed"), 0)}\n    VelOutwardOther: {Range(KwXml.Child(velocity, "OtherSpeed"), 0)}\n");
                    break;
                case "Spherical":
                    sb.Append($"    VelocityType: SPHERICAL\n    VelSpherical: {Range(KwXml.Child(velocity, "Speed"), 0)}\n");
                    break;
                case "Hemispherical":
                    sb.Append($"    VelocityType: HEMISPHERICAL\n    VelHemispherical: {Range(KwXml.Child(velocity, "Speed"), 0)}\n");
                    break;
                case "Cylindrical":
                    sb.Append($"    VelocityType: CYLINDRICAL\n    VelCylindricalRadial: {Range(KwXml.Child(velocity, "Radial"), 0)}\n    VelCylindricalNormal: {Range(KwXml.Child(velocity, "Normal"), 0)}\n");
                    break;
            }

            var volume = KwXml.Child(p, "Volume")?.Elements().FirstOrDefault();
            switch (volume?.Name.LocalName)
            {
                case "Line":
                case "Spline":
                    sb.Append($"    VolumeType: LINE\n    VolLineStart: {Coord(KwXml.Child(volume, "StartPoint"))}\n    VolLineEnd: {Coord(KwXml.Child(volume, "EndPoint"))}\n");
                    break;
                case "Box":
                    sb.Append($"    VolumeType: BOX\n    VolBoxHalfSize: {Coord(KwXml.Child(volume, "HalfSize"))}\n");
                    break;
                case "Sphere":
                    sb.Append($"    VolumeType: SPHERE\n    VolSphereRadius: {G(x.Num(volume, "Radius"))}\n");
                    break;
                case "Cylinder":
                    sb.Append($"    VolumeType: CYLINDER\n    VolCylinderRadius: {G(x.Num(volume, "Radius"))}\n    VolCylinderLength: {G(x.Num(volume, "Length"))}\n");
                    break;
                default:
                    sb.Append("    VolumeType: POINT\n");
                    break;
            }
            sb.Append($"    IsHollow: {(x.Bool(volume, "IsHollow") ? "Yes" : "No")}\n");
            foreach (string flag in new[] { "IsGroundAligned", "IsEmitAboveGroundOnly", "IsParticleUpTowardsEmitter" })
                sb.Append($"    {flag}: {(x.Bool(p, flag) ? "Yes" : "No")}\n");
            if (Ps(x.Attr(p, "SlaveSystem")) is { Length: > 0 } slave)
            {
                sb.Append($"    SlaveSystem: {slave}\n");
                if (KwXml.Child(p, "SlavePosOffset") is { } so)
                    sb.Append($"    SlavePosOffset: {Coord(so)}\n");
            }
            if (Ps(x.Attr(p, "PerParticleAttachedSystem")) is { Length: > 0 } attached)
                sb.Append($"    PerParticleAttachedSystem: {attached}\n");
            if (KwXml.Child(p, "Wind") is { } wind)
            {
                string motion = x.Attr(wind, "Motion", "NOT_USED").ToUpperInvariant();
                sb.Append($"    WindMotion: {(motion == "PING_PONG" ? "PingPong" : motion == "CIRCULAR" ? "Circular" : "Unused")}\n");
            }
        }

        // ---- Projectiles ----

        /// <summary>
        /// A projectile object (a BezierProjectile with a model) as Zero Hour's DumbProjectileBehavior projectile; returns its
        /// name and the FX its death plays (the impact), or null if it can't be made.
        /// </summary>
        (string Name, string ImpactFx)? Projectile(string id)
        {
            var o = Obj(id);
            if (o == null)
                return null;
            var bezier = KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "BezierProjectile");
            var fxBehavior = KwXml.AllModules(o).FirstOrDefault(m => m.Name.LocalName == "FXListBehavior");
            string impact = Fx(KwXml.Children(fxBehavior, "Event").Where(e => x.Attr(e, "Index") == "onDeath").Select(e => x.Attr(e, "FX")).FirstOrDefault());
            string name = P + id;
            if (bezier == null)
                return null;
            if (!doneProjectiles.Add(id))
                return (name, impact);
            // The model and its trails (the default state's particle systems).
            string model = "", bones = "";
            foreach (var d in KwXml.Modules(o, "Draws"))
            {
                var def = KwXml.Children(d, "ModelConditionState").FirstOrDefault(s => x.Attr(s, "ParseCondStateType") == "PARSE_DEFAULT");
                if (x.Attr(KwXml.Child(def, "Model"), "Name") is not { Length: > 0 } m)
                    continue;
                model = m;
                var trails = KwXml.Children(def, "ParticleSysBone")
                    .Select(b => (Bone: x.Attr(b, "BoneName", "NONE").ToUpperInvariant(), System: Ps(x.Attr(b, "FXParticleSystemTemplate"))))
                    .Where(b => b.System.Length > 0).ToList();
                if (trails.Count > 0)
                    bones = $"            ParticleSysBone: [{string.Join(", ", trails.Select(t => $"{t.Bone} {t.System}"))}]\n";
                break;
            }
            float H(string a, string b) => (x.Num(bezier, a) + x.Num(bezier, b, x.Num(bezier, a))) / 2;
            var sb = projectiles;
            sb.Append($"  {name}:\n    EditorSorting: SYSTEM\n    VisionRange: 0\n    KindOf: PROJECTILE\n    Geometry: Sphere\n    GeometryIsSmall: Yes\n    GeometryMajorRadius: 1.0\n");
            sb.Append("    ArmorSet:\n      Armor: ProjectileArmor\n    Modules:\n");
            sb.Append("      ModuleTag_01:\n        Module: Draw\n        Class: W3DModelDraw\n        ConditionState:\n          NONE:\n");
            sb.Append($"            Model: {(model.Length > 0 ? P + model : "None")}\n{bones}");
            sb.Append("      ModuleTag_02:\n        Module: Body\n        Class: ActiveBody\n        MaxHealth: 100.0\n        InitialHealth: 100.0\n");
            sb.Append("      ModuleTag_03:\n        Module: Behavior\n        Class: DestroyDie\n");
            sb.Append("      ModuleTag_04:\n        Module: Behavior\n        Class: DumbProjectileBehavior\n");
            sb.Append($"        FirstHeight: {G(H("FirstHeightMin", "FirstHeightMax"))}\n        SecondHeight: {G(H("SecondHeightMin", "SecondHeightMax"))}\n");
            sb.Append($"        FirstPercentIndent: {G(x.Num(bezier, "FirstPercentIndent", 33))}%\n        SecondPercentIndent: {G(x.Num(bezier, "SecondPercentIndent", 66))}%\n");
            sb.Append($"        FlightPathAdjustDistPerSecond: {G(x.Num(bezier, "FlightPathAdjustDistPerSecond", 100))}\n");
            sb.Append("      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 0.01\n");
            return (name, impact);
        }

        void WriteEffects(string output, string header)
        {
            // FX lists and particle systems pull in each other (and sounds): until nothing is left.
            while (pendingFx.Count > 0 || pendingParticles.Count > 0)
            {
                while (pendingFx.TryDequeue(out var fx))
                    WriteFxList(fx);
                while (pendingParticles.TryDequeue(out var ps))
                    WriteParticleSystem(ps);
            }
            File.WriteAllText(Path.Combine(output, "fx-lists.yaml"), header + fxLists);
            File.WriteAllText(Path.Combine(output, "particle-systems.yaml"), header + particles);
            File.WriteAllText(Path.Combine(output, "projectiles.yaml"), header + projectiles);
        }
    }
}
