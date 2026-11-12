using System.Numerics;
using OpenWestwood.Core.FileSystem;
using OpenWestwood.Formats.W3d;

namespace OpenWestwood.Formats.Emperor;

/// <summary>
/// Emperor: Battle for Dune's archives (content/emperor/DATA/*.rfh) as one package whose files sit under "emperor/"
/// (emperor/Units/AT_inf_H0.xbf, emperor/Textures/=G_Infantry_256.tga, emperor/Rules.txt...), so nothing of
/// Emperor's can stand in for a Zero Hour file. It also builds models: "EM_AT_inf_H0" is Units/AT_inf_H0.xbf
/// turned into the engine's W3D form (see <see cref="XbfToW3d"/>).
/// </summary>
public sealed class EmperorArtPackage : IPackage, IW3dProvider
{
    public const string Folder = "emperor/";
    public const string ModelPrefix = "EM_";
    /// <summary>A building's model: turned so that Emperor's north (+z: the Occupy grid's first row) is the map's north.</summary>
    public const string BuildingPrefix = "EMB_";

    readonly List<RfhArchive> archives;
    readonly Dictionary<string, (RfhArchive Archive, string Path)> files = new(PathUtil.Comparer);
    readonly Dictionary<string, string> xbfByName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, W3dFile?> models = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "Emperor: Battle for Dune";

    public EmperorArtPackage(string dataFolder)
    {
        archives = RfhArchive.AllIn(dataFolder).ToList();
        // Later archives (MODEL0002's patched Rules.txt) replace earlier ones.
        foreach (var archive in archives)
            foreach (string path in archive.FilePaths)
            {
                files[Folder + path] = (archive, path);
                if (path.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase))
                    xbfByName[Path.GetFileNameWithoutExtension(path)] = Folder + path;
            }
    }

    /// <summary>Mounts content/emperor/DATA if it is there.</summary>
    public static void MountIfPresent(VirtualFileSystem fileSystem, string root)
    {
        string data = Path.Combine(root, "content", "emperor", "DATA");
        if (!Directory.Exists(data))
            return;
        try
        {
            fileSystem.Mount(new EmperorArtPackage(data));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Could not mount {data}: {e.Message}");
        }
    }

    public IEnumerable<string> FilePaths => files.Keys;

    public bool Contains(string path) => files.ContainsKey(PathUtil.Normalize(path));

    public Stream Open(string path)
    {
        if (!files.TryGetValue(PathUtil.Normalize(path), out var file))
            throw new FileNotFoundException($"{path} is not in Emperor's archives.");
        return file.Archive.Open(file.Path);
    }

    /// <summary>
    /// A model's texture as a file (emperor/Textures/...), or null. As the game does, a size the archives lack
    /// ("=&amp;ATConyardPnl6_D_128.tga") falls back to another size of the same picture ("_64", "_32"...) or the
    /// one without a size ("=&amp;ATConyardPnl6_D.tga").
    /// </summary>
    public string? ResolveTexture(string name)
    {
        string path = Folder + "Textures/" + name;
        if (files.ContainsKey(path))
            return path;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        var sized = System.Text.RegularExpressions.Regex.Match(stem, "^(.*)_(32|64|128|256)$");
        string bare = sized.Success ? sized.Groups[1].Value : stem;
        foreach (string candidate in new[] { bare, bare + "_256", bare + "_128", bare + "_64", bare + "_32", bare + "_32tga" })
            if (files.ContainsKey(Folder + "Textures/" + candidate + ext))
                return Folder + "Textures/" + candidate + ext;
        return null;
    }

    /// <summary>How far a building's model turns (degrees, anticlockwise from above): Emperor's +z (north) to the map's +y.</summary>
    public static float BuildingYaw = 90;

    /// <summary>
    /// How far a unit's (or projectile's) model turns: Emperor's units face its -z, which the conversion makes -x, while
    /// the engine moves things along +x; turned round, soldiers and vehicles face the way they go and missiles fly nose
    /// first (checked in the model viewer: OW_VIEW_YAW=0 shows a model's front).
    /// </summary>
    public static float UnitYaw = 180;

    /// <summary>The XBF's path (emperor/Units/AT_inf_H0.xbf) for a model name (AT_inf_H0), or null.</summary>
    public string? XbfPath(string name) => xbfByName.GetValueOrDefault(name);

    public byte[] ReadAllBytes(string path)
    {
        using var s = Open(path);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public W3dFile? GetW3d(string name)
    {
        bool building = name.StartsWith(BuildingPrefix, StringComparison.OrdinalIgnoreCase);
        if (!building && !name.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        lock (models)
            if (models.TryGetValue(name, out var cached))
                return cached;
        W3dFile? file = null;
        if (XbfPath(name[(building ? BuildingPrefix : ModelPrefix).Length..]) is { } path)
            file = XbfToW3d.Convert(XbfFile.Read(ReadAllBytes(path)), name, building ? BuildingYaw : UnitYaw, ResolveTexture);
        lock (models)
            models[name] = file;
        return file;
    }

    public void Dispose()
    {
        foreach (var archive in archives)
            archive.Dispose();
    }
}

/// <summary>
/// An Emperor model in the engine's W3D form:
///   - Emperor is Y-up and left-handed (x right, y up, z forward); the engine is Z-up and right-handed (x forward,
///     y left, z up): (x, y, z) becomes (z, -x, y), and Scale shrinks Emperor's units to the engine's
///     (an Emperor light infantryman stands 34 high, a Zero Hour one about 13).
///   - every node is a bone (pivot); its geometry a mesh on that bone, unshared per corner (faces carry the UVs).
///   - the named frame ranges in the file ("Move", "Fire 0", "Shot 1"...) are animations "EM_x.Move", "EM_x.Fire0".
///     Key-animated nodes get rotation and translation channels.
///   - vertex-animated nodes (infantry bodies) get one mesh per stored pose, each on its own bone that the
///     animation shows only from that pose's frame to the next one's; at rest only the node's own mesh shows.
///   - helper nodes (names starting # ~ : &gt; ? ^, selection boxes SLCT...) are bones without meshes.
/// </summary>
public static class XbfToW3d
{
    public const float Scale = 0.4f;

    [ThreadStatic] static Func<string, string?>? textureResolver;

    /// <summary>Emperor's axes to the engine's, as a row-vector matrix.</summary>
    static readonly Matrix4x4 Axes = new(
        0, -1, 0, 0,   // x (right) -> -y
        0, 0, 1, 0,    // y (up)    -> z
        1, 0, 0, 0,    // z (fwd)   -> x
        0, 0, 0, 1);

    static Vector3 Point(Vector3 p) => Vector3.Transform(p, Axes) * Scale;
    static Vector3 Direction(Vector3 n) => Vector3.Normalize(Vector3.TransformNormal(n, Axes)) is var d && float.IsFinite(d.X) ? d : Vector3.UnitZ;

    /// <summary>A node's local transform in engine axes and units: rotation and translation.</summary>
    /// <summary>
    /// A transform's (uniform) scale: the length of its rows. Emperor scales whole infantrymen (0.25 to 18) at
    /// their root; the engine's bones don't scale, so it is baked into the meshes and the children's offsets.
    /// </summary>
    static float ScaleOf(Matrix4x4 m)
    {
        float s = (new Vector3(m.M11, m.M12, m.M13).Length() + new Vector3(m.M21, m.M22, m.M23).Length() + new Vector3(m.M31, m.M32, m.M33).Length()) / 3;
        // (Some unused helper nodes hold garbage: 1e32, infinity.)
        return float.IsFinite(s) && s > 1e-4f && s < 1000 ? s : 1;
    }

    /// <summary>A node's scale with its parents': what its own geometry is multiplied by.</summary>
    static float TotalScale(XbfNode? node) => node == null ? 1 : ScaleOf(node.Transform) * TotalScale(node.Parent);

    /// <param name="parentScale">The parents' scale (the offset is in their scaled frame).</param>
    static (Quaternion Rotation, Vector3 Translation) Local(Matrix4x4 m, float parentScale = 1)
    {
        Matrix4x4.Invert(Axes, out var inverse);
        var converted = inverse * m * Axes;
        var translation = converted.Translation * Scale * parentScale;
        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z) || translation.Length() > 1e5f)
            translation = Vector3.Zero;
        converted.Translation = Vector3.Zero;
        // (Rows may carry a little scale; normalise them before taking the rotation.)
        var x = Vector3.Normalize(new Vector3(converted.M11, converted.M12, converted.M13));
        var y = Vector3.Normalize(new Vector3(converted.M21, converted.M22, converted.M23));
        var z = Vector3.Normalize(new Vector3(converted.M31, converted.M32, converted.M33));
        var rotationMatrix = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        var rotation = Quaternion.CreateFromRotationMatrix(rotationMatrix);
        return (float.IsFinite(rotation.W) ? Quaternion.Normalize(rotation) : Quaternion.Identity, translation);
    }

    /// <summary>
    /// Nodes that are not drawn: dummies (a # anywhere in the name: #light, ~~0root#, ::0gun#), the shadow plane (#^^), effects shown only now and then (?... : the
    /// spotlight, the leech effect, muzzle flashes) and the selection box (SLCT...). Tags in front of a drawn part's
    /// name (::0 the turret's yaw joint, ::1 its pitch joint, ~~n, >>n a muzzle) don't hide it.
    /// </summary>
    public static bool IsHelper(string name) =>
        name.Length == 0 || name[0] is '?' or '^' || name.Contains('#') || name.StartsWith("SLCT", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The bone name the engine's model draw looks for: the turret's yaw joint (::0) TURRET, its pitch joint (::1)
    /// TURRETPITCH, the muzzles (&gt;&gt;n) MUZZLE01, MUZZLE02...; other nodes keep their names.
    /// </summary>
    static string BoneName(string name, HashSet<string> taken)
    {
        string? special = null;
        if (name.StartsWith("::0"))
            special = "TURRET";
        else if (name.StartsWith("::1"))
            special = "TURRETPITCH";
        else if (name.StartsWith(">>") && name.Length > 2 && char.IsDigit(name[2]))
            special = $"MUZZLE{name[2] - '0' + 1:00}";
        if (special != null && taken.Add(special))
            return special;
        return name;
    }

    /// <param name="yawDegrees">Turns the whole model (buildings face south).</param>
    public static W3dFile Convert(XbfFile xbf, string modelName, float yawDegrees = 0, Func<string, string?>? resolveTexture = null)
    {
        textureResolver = resolveTexture;
        var file = new W3dFile();
        var hierarchy = new W3dHierarchy { Name = modelName };
        hierarchy.Pivots.Add(new W3dPivot { Name = "ROOTTRANSFORM" });
        int top = 0;
        if (yawDegrees != 0)
        {
            hierarchy.Pivots.Add(new W3dPivot { Name = "EMTURN", ParentIndex = 0, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawDegrees * MathF.PI / 180) });
            top = 1;
        }
        var pivotOf = new Dictionary<XbfNode, int>();
        var lod = new List<W3dSubObject>();
        // Vertex-animated poses: (node, pose index) -> its bone.
        var poseBones = new List<(XbfNode Node, int Pose, int Bone)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A '?' node only shows while an effect plays (a muzzle flash, the leech); a bullet's whole look is such nodes
        // (?mgbullet, ?sonic), so a model with nothing else to draw draws those ('#' ones stay out: particle emitters).
        bool drawEffects = !xbf.AllNodes.Any(n => !IsHelper(n.Name) && n.Faces.Length > 0);
        bool Drawn(XbfNode node) => node.Faces.Length > 0 && (!IsHelper(node.Name) ||
            drawEffects && node.Name.StartsWith('?') && !node.Name.Contains('#'));

        void AddNode(XbfNode node, int parent)
        {
            var (rotation, translation) = Local(node.Transform, TotalScale(node.Parent));
            int pivot = hierarchy.Pivots.Count;
            hierarchy.Pivots.Add(new W3dPivot { Name = BoneName(node.Name, taken), ParentIndex = parent, Rotation = rotation, Translation = translation });
            pivotOf[node] = pivot;
            if (Drawn(node))
            {
                bool animated = node.VertexAnimation is { Frames.Length: > 0 };
                foreach (var mesh in Meshes(xbf, node, modelName, node.Name, node.Positions, node.Normals))
                {
                    file.Meshes.Add(mesh);
                    lod.Add(new W3dSubObject(pivot, mesh.FullName));
                }
                if (animated)
                {
                    // The rest mesh hides while an animation plays the poses.
                    var va = node.VertexAnimation!;
                    float unit = 1f / (1 << (int)Math.Min(va.Scale, 30));
                    for (int k = 0; k < va.Frames.Length; k++)
                    {
                        var frame = va.Frames[k];
                        if (frame.Length != node.Positions.Length)
                            continue;
                        int bone = hierarchy.Pivots.Count;
                        hierarchy.Pivots.Add(new W3dPivot { Name = $"{node.Name}#pose{k}", ParentIndex = pivot, HiddenAtRest = true });
                        var positions = frame.Select(v => new Vector3(v.X, v.Y, v.Z) * unit).ToArray();
                        var normals = frame.Select(v => PackedNormal(v.Normal)).ToArray();
                        foreach (var poseMesh in Meshes(xbf, node, modelName, $"{node.Name}#pose{k}", positions, normals))
                        {
                            file.Meshes.Add(poseMesh);
                            lod.Add(new W3dSubObject(bone, poseMesh.FullName));
                        }
                        poseBones.Add((node, k, bone));
                    }
                }
            }
            foreach (var child in node.Children)
                AddNode(child, pivot);
        }
        foreach (var node in xbf.Nodes)
            AddNode(node, top);

        file.Hierarchy = hierarchy;
        var hlod = new W3dHLod { Name = modelName, HierarchyName = modelName };
        hlod.Lods.Add(lod);
        file.HLod = hlod;

        // "All": the whole timeline (explosions and effects have no named ranges).
        int lastFrame = xbf.AllNodes.Select(n => Math.Max(n.KeyAnimation?.FrameCount ?? 0, (n.VertexAnimation?.FrameCount ?? 0) - 1)).DefaultIfEmpty(0).Max();
        if (lastFrame > 0)
            file.Animations.Add(Animation(xbf, modelName, "All", 0, lastFrame, pivotOf, poseBones));
        foreach (var (name, ranges) in xbf.Animations)
        {
            var (start, end) = ranges[0];
            if (end < start)
                continue;
            file.Animations.Add(Animation(xbf, modelName, name.Replace(" ", ""), start, end, pivotOf, poseBones));
        }
        return file;
    }

    static Vector3 PackedNormal(ushort packed)
    {
        static int Signed5(int v) => (v & 0x1F) > 15 ? -(v & 0xF) : v & 0xF;
        var n = new Vector3(Signed5(packed), Signed5(packed >> 5), Signed5(packed >> 10));
        return n.LengthSquared() > 0 ? Direction(n) : Vector3.UnitZ;
    }

    enum Blend { Opaque, Additive, Alpha }

    /// <summary>
    /// How a texture blends, by the marks in front of its name: ! glows (added to what is behind: flashes, lights,
    /// flames), @ is see-through by its alpha (skirts, wakes, the leech effect); others are solid, cut out by alpha.
    /// </summary>
    static Blend BlendOf(XbfFile xbf, int texture)
    {
        string name = texture >= 0 && texture < xbf.Textures.Count ? xbf.Textures[texture] : "";
        string marks = new(name.TakeWhile(c => !char.IsLetterOrDigit(c)).ToArray());
        return marks.Contains('!') ? Blend.Additive : marks.Contains('@') ? Blend.Alpha : Blend.Opaque;
    }

    /// <summary>A node's geometry as meshes, one for each way its textures blend.</summary>
    static IEnumerable<W3dMesh> Meshes(XbfFile xbf, XbfNode node, string container, string name, Vector3[] positions, Vector3[] normals) =>
        node.Faces.GroupBy(f => BlendOf(xbf, f.Texture)).OrderBy(g => g.Key)
            .Select(g => Mesh(xbf, node, g.ToArray(), g.Key, container, g.Key == Blend.Opaque ? name : $"{name}#{g.Key}", positions, normals));

    static W3dMesh Mesh(XbfFile xbf, XbfNode node, XbfFace[] faces, Blend blend, string container, string name, Vector3[] positions, Vector3[] normals)
    {
        var mesh = new W3dMesh { Name = name, ContainerName = container };
        float scale = TotalScale(node);
        int corners = faces.Length * 3;
        var p = new Vector3[corners];
        var n = new Vector3[corners];
        var uv = new Vector2[corners];
        var colors = node.Prelight != null ? new Vector4[corners] : null;
        var textureIds = new uint[faces.Length];
        var indices = new int[corners];
        var textureSlots = new Dictionary<int, uint>();
        for (int f = 0; f < faces.Length; f++)
        {
            var face = faces[f];
            // Swapping two corners keeps the faces turned outwards after the change of handedness.
            (int Index, Vector2 Uv)[] tri = [(face.A, face.UvA), (face.C, face.UvC), (face.B, face.UvB)];
            for (int c = 0; c < 3; c++)
            {
                int at = f * 3 + c, v = tri[c].Index;
                bool valid = v >= 0 && v < positions.Length;
                p[at] = valid ? Point(positions[v] * scale) : Vector3.Zero;
                if (!float.IsFinite(p[at].X) || !float.IsFinite(p[at].Y) || !float.IsFinite(p[at].Z))
                    p[at] = Vector3.Zero;
                n[at] = valid && v < normals.Length ? Direction(normals[v]) : Vector3.UnitZ;
                uv[at] = new Vector2(tri[c].Uv.X, 1 - tri[c].Uv.Y);
                if (colors != null)
                    colors[at] = valid && v < node.Prelight!.Length ? new Vector4(node.Prelight[v], 1) : Vector4.One;
                indices[at] = at;
            }
            int texture = face.Texture;
            if (!textureSlots.TryGetValue(texture, out uint slot))
            {
                slot = (uint)mesh.Textures.Count;
                textureSlots[texture] = slot;
                string textureName = texture >= 0 && texture < xbf.Textures.Count ? xbf.Textures[texture] : "";
                string texturePath = textureName.Length == 0 ? "" : textureResolver?.Invoke(textureName) ?? EmperorArtPackage.Folder + "Textures/" + textureName;
                mesh.Textures.Add(new W3dTexture { Name = texturePath });
            }
            textureIds[f] = slot;
        }
        mesh.Positions = p;
        mesh.Normals = n;
        mesh.Indices = indices;
        mesh.Shaders.Add(blend switch
        {
            Blend.Additive => new W3dShader { DepthCompare = 3, DepthMask = 0, SrcBlend = W3dShader.SrcOne, DestBlend = W3dShader.DstOne, PrimaryGradient = 1, Texturing = 1 },
            Blend.Alpha => new W3dShader { DepthCompare = 3, DepthMask = 0, SrcBlend = W3dShader.SrcSrcAlpha, DestBlend = W3dShader.DstOneMinusSrcAlpha, PrimaryGradient = 1, Texturing = 1 },
            // Solid textures cut out with their alpha (tracks, struts, fences).
            _ => new W3dShader { DepthCompare = 3, DepthMask = 1, SrcBlend = W3dShader.SrcOne, DestBlend = W3dShader.DstZero, PrimaryGradient = 1, Texturing = 1, AlphaTest = 1 },
        });
        mesh.VertexMaterials.Add(new W3dVertexMaterial { Name = name, Diffuse = Vector3.One, Ambient = Vector3.One });
        var pass = new W3dMaterialPass { ShaderIds = [0], VertexMaterialIds = [0] };
        if (colors != null)
            pass.DiffuseColors = colors;
        pass.Stages.Add(new W3dTextureStage { TextureIds = textureIds, TexCoords = uv });
        mesh.Passes.Add(pass);
        if (corners > 0)
        {
            var min = p.Aggregate(Vector3.Min);
            var max = p.Aggregate(Vector3.Max);
            mesh.BoundsMin = min;
            mesh.BoundsMax = max;
            mesh.SphereCenter = (min + max) / 2;
            mesh.SphereRadius = (max - min).Length() / 2;
        }
        return mesh;
    }

    const float FrameRate = 30;

    static W3dAnimation Animation(XbfFile xbf, string modelName, string name, int start, int end,
        Dictionary<XbfNode, int> pivotOf, List<(XbfNode Node, int Pose, int Bone)> poseBones)
    {
        int frames = end - start + 1;
        var animation = new W3dAnimation { Name = name, HierarchyName = modelName, FrameCount = frames, FrameRate = FrameRate };
        foreach (var (node, pivot) in pivotOf)
        {
            // Key animation: each frame's local transform, relative to the rest transform (the pose applies
            // rotate, translate, then the rest transform).
            if (node.KeyAnimation is { } ka && ka.Matrices.Length > 0)
            {
                float parentScale = TotalScale(node.Parent);
                var (baseRotation, baseTranslation) = Local(node.Transform, parentScale);
                var rest = Matrix4x4.CreateFromQuaternion(baseRotation) * Matrix4x4.CreateTranslation(baseTranslation);
                Matrix4x4.Invert(rest, out var restInverse);
                var t = new float[frames * 3];
                var q = new float[frames * 4];
                bool moves = false;
                for (int f = 0; f < frames; f++)
                {
                    var matrix = KeyMatrix(ka, start + f) ?? node.Transform;
                    var (rotation, translation) = Local(matrix, parentScale);
                    var delta = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation) * restInverse;
                    var deltaTranslation = delta.Translation;
                    delta.Translation = Vector3.Zero;
                    var deltaRotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(delta));
                    // (R(anim) * T(anim) = delta: its translation row is T's.)
                    var animTranslation = deltaTranslation;
                    t[f * 3] = animTranslation.X; t[f * 3 + 1] = animTranslation.Y; t[f * 3 + 2] = animTranslation.Z;
                    q[f * 4] = deltaRotation.X; q[f * 4 + 1] = deltaRotation.Y; q[f * 4 + 2] = deltaRotation.Z; q[f * 4 + 3] = deltaRotation.W;
                    moves |= animTranslation.LengthSquared() > 1e-6f || MathF.Abs(deltaRotation.W) < 0.99999f;
                }
                if (moves)
                {
                    animation.AddChannel(pivot, W3dChannelType.X, new W3dAnimation.RawChannel(0, 1, Enumerable.Range(0, frames).Select(f => t[f * 3]).ToArray()));
                    animation.AddChannel(pivot, W3dChannelType.Y, new W3dAnimation.RawChannel(0, 1, Enumerable.Range(0, frames).Select(f => t[f * 3 + 1]).ToArray()));
                    animation.AddChannel(pivot, W3dChannelType.Z, new W3dAnimation.RawChannel(0, 1, Enumerable.Range(0, frames).Select(f => t[f * 3 + 2]).ToArray()));
                    animation.AddChannel(pivot, W3dChannelType.Quaternion, new W3dAnimation.RawChannel(0, 4, q));
                }
            }
        }
        // Vertex animation: the rest mesh hides, and each frame shows its pose.
        foreach (var group in poseBones.GroupBy(p => p.Node))
        {
            var node = group.Key;
            var poses = PoseOfFrames(node.VertexAnimation!, node.Positions.Length);
            animation.AddVisibility(pivotOf[node], new W3dAnimation.VisibilityChannel(0, false, new bool[frames]));
            foreach (var (_, pose, bone) in group)
            {
                var visible = new bool[frames];
                for (int f = 0; f < frames; f++)
                    visible[f] = poses[Math.Clamp(start + f, 0, poses.Length - 1)] == pose;
                animation.AddVisibility(bone, new W3dAnimation.VisibilityChannel(0, false, visible));
            }
        }
        return animation;
    }

    /// <summary>
    /// Which stored pose each frame shows. The per-frame table holds, for a frame that has its own pose, 1 + the
    /// pose's offset in shorts (4 a vertex); 0 for a frame between two poses (shown here as the one before).
    /// Without the table the poses are the frames in order.
    /// </summary>
    static int[] PoseOfFrames(XbfVertexAnimation va, int vertexCount)
    {
        int frames = Math.Max(1, va.FrameCount);
        var poses = new int[frames];
        int last = 0;
        for (int f = 0; f < frames; f++)
        {
            if (va.Interpolation is { } table)
            {
                uint value = f < table.Length ? table[f] : 0;
                if (value != 0 && vertexCount > 0)
                    last = (int)Math.Min((value - 1) / (4u * (uint)vertexCount), (uint)Math.Max(0, va.Frames.Length - 1));
            }
            else
                last = Math.Min(f, va.Frames.Length - 1);
            poses[f] = last;
        }
        return poses;
    }

    /// <summary>The node's local transform at a frame of its key animation.</summary>
    static Matrix4x4? KeyMatrix(XbfKeyAnimation ka, int frame)
    {
        if (ka.Matrices.Length == 0)
            return null;
        frame = Math.Clamp(frame, 0, ka.FrameCount);
        int index = ka.FrameToMatrix is { } map ? (frame < map.Length ? map[frame] : map[^1]) : frame;
        return ka.Matrices[Math.Clamp(index, 0, ka.Matrices.Length - 1)];
    }
}
