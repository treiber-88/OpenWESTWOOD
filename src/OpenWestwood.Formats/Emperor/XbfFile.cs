using System.Numerics;
using System.Text;

namespace OpenWestwood.Formats.Emperor;

/// <summary>
/// Emperor: Battle for Dune's 3D models (.xbf, the Xanadu engine's format). Layout as documented by the community
/// (xanlib by Lunaji, the FED2k "XBF Format" notes):
///   int version, int fxSize, fx data (animation ranges, effects), int textureSize, texture names (NUL separated),
///   then root nodes until the file's final int -1.
/// A node: int vertexCount, int flags, int faceCount, int childCount, 16 doubles (local transform, row-vector order:
///   translation in 12..14), int nameLength, name; its children; vertices (position, normal: 6 floats); faces
///   (3 vertex indices, texture index, flags, 3 UVs); then by flags: prelight colours (3 bytes a vertex), smoothing
///   groups (int a face), vertex animation, key animation.
/// </summary>
public sealed class XbfFile
{
    public int Version { get; private set; }
    public byte[] FxData { get; private set; } = [];
    public List<string> Textures { get; } = new();
    public List<XbfNode> Nodes { get; } = new();

    /// <summary>Named frame ranges from the fx data ("Move", "Fire 0", "Shot 1"...); several ranges = alternatives.</summary>
    public Dictionary<string, List<(int Start, int End)>> Animations { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<XbfNode> AllNodes => Nodes.SelectMany(n => n.SelfAndDescendants);

    public static XbfFile Read(byte[] data)
    {
        var file = new XbfFile();
        var r = new Reader(data);
        file.Version = r.Int();
        int fxSize = r.Int();
        file.FxData = r.Bytes(fxSize);
        int textureSize = r.Int();
        var textureBytes = r.Bytes(textureSize);
        foreach (string name in Encoding.Latin1.GetString(textureBytes).Split('\0'))
        {
            string clean = name.Trim('\u0002', ' ');
            if (clean.Length > 0)
                file.Textures.Add(clean);
        }
        // The nodes run to the closing -1.
        while (r.Position + 4 <= data.Length - 4)
            file.Nodes.Add(ReadNode(r, null));
        file.ReadAnimationRanges();
        return file;
    }

    static XbfNode ReadNode(Reader r, XbfNode? parent)
    {
        var node = new XbfNode { Parent = parent };
        int vertexCount = r.Int();
        int flags = r.Int();
        int faceCount = r.Int();
        int childCount = r.Int();
        var m = new double[16];
        for (int i = 0; i < 16; i++)
            m[i] = r.Double();
        node.Transform = new Matrix4x4(
            (float)m[0], (float)m[1], (float)m[2], (float)m[3],
            (float)m[4], (float)m[5], (float)m[6], (float)m[7],
            (float)m[8], (float)m[9], (float)m[10], (float)m[11],
            (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
        int nameLength = r.Int();
        node.Name = Encoding.Latin1.GetString(r.Bytes(nameLength)).TrimEnd('\0');
        for (int i = 0; i < childCount; i++)
            node.Children.Add(ReadNode(r, node));
        node.Positions = new Vector3[vertexCount];
        node.Normals = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            node.Positions[i] = new Vector3(r.Float(), r.Float(), r.Float());
            node.Normals[i] = new Vector3(r.Float(), r.Float(), r.Float());
        }
        node.Faces = new XbfFace[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            int a = r.Int(), b = r.Int(), c = r.Int(), texture = r.Int(), faceFlags = r.Int();
            node.Faces[i] = new XbfFace(a, b, c, texture, faceFlags,
                new Vector2(r.Float(), r.Float()), new Vector2(r.Float(), r.Float()), new Vector2(r.Float(), r.Float()));
        }
        if ((flags & 1) != 0)
        {
            node.Prelight = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var rgb = r.Bytes(3);
                node.Prelight[i] = new Vector3(rgb[0], rgb[1], rgb[2]) / 255f;
            }
        }
        if ((flags & 2) != 0)
            r.Skip(4 * faceCount); // smoothing groups
        if ((flags & 4) != 0)
            node.VertexAnimation = ReadVertexAnimation(r);
        if ((flags & 8) != 0)
            node.KeyAnimation = ReadKeyAnimation(r);
        return node;
    }

    static XbfVertexAnimation ReadVertexAnimation(Reader r)
    {
        var va = new XbfVertexAnimation { FrameCount = r.Int() };
        int count = r.Int();
        int keyCount = r.Int();
        va.Keys = new int[keyCount];
        for (int i = 0; i < keyCount; i++)
            va.Keys[i] = r.Int();
        if (count >= 0)
            return va;
        uint scale = (uint)r.Int();
        int baseCount = r.Int();
        int perFrame = keyCount > 0 ? baseCount / keyCount : 0;
        va.Scale = scale & 0x7FFFFFFF;
        va.Frames = new XbfCompressedVertex[keyCount][];
        for (int f = 0; f < keyCount; f++)
        {
            var frame = new XbfCompressedVertex[perFrame];
            for (int i = 0; i < perFrame; i++)
                frame[i] = new XbfCompressedVertex(r.Short(), r.Short(), r.Short(), (ushort)r.Short());
            va.Frames[f] = frame;
        }
        if ((scale & 0x80000000) != 0)
        {
            va.Interpolation = new uint[va.FrameCount];
            for (int i = 0; i < va.FrameCount; i++)
                va.Interpolation[i] = (uint)r.Int();
        }
        return va;
    }

    static XbfKeyAnimation ReadKeyAnimation(Reader r)
    {
        var ka = new XbfKeyAnimation { FrameCount = r.Int() };
        int flags = r.Int();
        ka.Flags = flags;
        if (flags is -1 or -2 or -3)
        {
            int count = ka.FrameCount + 1;
            if (flags == -3)
            {
                count = r.Int();
                ka.FrameToMatrix = new short[ka.FrameCount + 1];
                for (int i = 0; i <= ka.FrameCount; i++)
                    ka.FrameToMatrix[i] = r.Short();
            }
            ka.Matrices = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                if (flags == -1)
                {
                    var v = new float[16];
                    for (int k = 0; k < 16; k++)
                        v[k] = r.Float();
                    ka.Matrices[i] = new Matrix4x4(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
                }
                else
                {
                    // 3x4: three rotation rows, then the translation.
                    var v = new float[12];
                    for (int k = 0; k < 12; k++)
                        v[k] = r.Float();
                    ka.Matrices[i] = new Matrix4x4(v[0], v[1], v[2], 0, v[3], v[4], v[5], 0, v[6], v[7], v[8], 0, v[9], v[10], v[11], 1);
                }
            }
            return ka;
        }
        // Keyframes: frame, which parts follow (rotation quaternion w,x,y,z; scale; translation).
        for (int i = 0; i < flags; i++)
        {
            int frame = r.Short();
            int which = r.Short() >> 12;
            Quaternion? rotation = null;
            Vector3? scale = null, translation = null;
            if ((which & 1) != 0)
            {
                float w = r.Float(), x = r.Float(), y = r.Float(), z = r.Float();
                rotation = new Quaternion(x, y, z, w);
            }
            if ((which & 2) != 0)
                scale = new Vector3(r.Float(), r.Float(), r.Float());
            if ((which & 4) != 0)
                translation = new Vector3(r.Float(), r.Float(), r.Float());
            ka.Keys.Add(new XbfKey(frame, rotation, scale, translation));
        }
        return ka;
    }

    /// <summary>
    /// The fx data's animation table: for each named animation a 32-byte name and an int, then ranges of
    /// (int, int repeat, int body part, bool last, 3 pad bytes, int start, int end) until the one marked last.
    /// </summary>
    void ReadAnimationRanges()
    {
        foreach (string name in AnimationNames)
        {
            var key = Encoding.ASCII.GetBytes(name + "\0");
            int at = LastIndexOf(FxData, key);
            if (at < 0 || at + 36 > FxData.Length)
                continue;
            int o = at + 36;
            var ranges = new List<(int, int)>();
            while (o + 24 <= FxData.Length && ranges.Count < 32)
            {
                bool last = FxData[o + 12] != 0;
                int start = BitConverter.ToInt32(FxData, o + 16), end = BitConverter.ToInt32(FxData, o + 20);
                ranges.Add((start, end));
                o += 24;
                if (last)
                    break;
            }
            if (ranges.Count > 0)
                Animations[name] = ranges;
        }
    }

    static int LastIndexOf(byte[] data, byte[] key)
    {
        for (int i = data.Length - key.Length; i >= 0; i--)
        {
            int k = 0;
            while (k < key.Length && data[i + k] == key[k])
                k++;
            if (k == key.Length && (i == 0 || data[i - 1] == 0 || data[i - 1] > 0x7F || data[i - 1] < 0x20))
                return i;
        }
        return -1;
    }

    /// <summary>The animation names the game knows (from its executable, as listed in the community notes).</summary>
    public static readonly string[] AnimationNames =
    [
        "Stationary", "Idle 0", "Idle 1", "Move Start", "Move Stop", "Move", "Turn Left", "Turn Right",
        "Fire 0", "Fire 1", "Fire 2", "Fire 3", "Fire 4", "Explode", "Blow Up 1", "Blow Up 2", "Shot 1", "Shot 2",
        "Burnt 1", "Run Over 1", "Gassed 1", "Deployed Death 1", "Deployed Death 2", "Deploy Gun", "Deploy Gun Hold",
        "Undeploy Gun", "Deployed Idle 0", "Deployed Fire", "DeployedDeath0", "Harv Unload Start", "Harv Unload Hold",
        "Harv Unload End", "Harv Eat Start", "Harv Eat Hold", "Harv Eat End", "Repair Arms Out", "Repair Arms Hold",
        "Repair Arms In", "Sink", "SinkHold", "Surface", "SinkMove", "Move Special", "StandToLayDown", "LayDownToStand",
        "Lay Down", "Crawl", "Lay Down Fire", "Crouch", "CrouchFire", "Construct", "Deconstruct", "Takeoff", "Land",
        "Hover", "Fly", "FlyToHover", "HoverToFly", "StartPickup", "Pickup", "EndPickup", "Enter Portal", "Exit Portal",
        "Win", "Leeched", "Leech Death", "Born", "Refinery Pad 1", "Refinery Pad 2", "Sell",
    ];

    sealed class Reader(byte[] data)
    {
        public int Position;
        public int Int() { int v = BitConverter.ToInt32(data, Position); Position += 4; return v; }
        public short Short() { short v = BitConverter.ToInt16(data, Position); Position += 2; return v; }
        public float Float() { float v = BitConverter.ToSingle(data, Position); Position += 4; return v; }
        public double Double() { double v = BitConverter.ToDouble(data, Position); Position += 8; return v; }
        public byte[] Bytes(int n) { var v = data.AsSpan(Position, n).ToArray(); Position += n; return v; }
        public void Skip(int n) => Position += n;
    }
}

public sealed class XbfNode
{
    public XbfNode? Parent { get; init; }
    public string Name { get; set; } = "";
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
    public List<XbfNode> Children { get; } = new();
    public Vector3[] Positions { get; set; } = [];
    public Vector3[] Normals { get; set; } = [];
    public XbfFace[] Faces { get; set; } = [];
    public Vector3[]? Prelight { get; set; }
    public XbfVertexAnimation? VertexAnimation { get; set; }
    public XbfKeyAnimation? KeyAnimation { get; set; }

    public IEnumerable<XbfNode> SelfAndDescendants => new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants));

    /// <summary>The node's transform to model space (its own, then its parents').</summary>
    public Matrix4x4 ModelTransform => Parent == null ? Transform : Transform * Parent.ModelTransform;
}

public readonly record struct XbfFace(int A, int B, int C, int Texture, int Flags, Vector2 UvA, Vector2 UvB, Vector2 UvC);

public readonly record struct XbfCompressedVertex(short X, short Y, short Z, ushort Normal);

public sealed class XbfVertexAnimation
{
    public int FrameCount { get; set; }
    public int[] Keys { get; set; } = [];
    public uint Scale { get; set; }
    public XbfCompressedVertex[][] Frames { get; set; } = [];
    public uint[]? Interpolation { get; set; }
}

public readonly record struct XbfKey(int Frame, Quaternion? Rotation, Vector3? Scale, Vector3? Translation);

public sealed class XbfKeyAnimation
{
    public int FrameCount { get; set; }
    public int Flags { get; set; }
    public Matrix4x4[] Matrices { get; set; } = [];
    public short[]? FrameToMatrix { get; set; }
    public List<XbfKey> Keys { get; } = new();
}
