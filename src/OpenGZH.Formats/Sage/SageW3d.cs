using System.Numerics;
using System.Text;
using OpenGZH.Formats.W3d;

namespace OpenGZH.Formats.Sage;

/// <summary>
/// Turns Command &amp; Conquer 3 / Kane's Wrath compiled art (W3DContainer, W3DHierarchy, W3DMesh and Texture
/// assets of a <see cref="SageStream"/>) into the Zero Hour W3D model the renderer draws. Layouts (offsets in an
/// asset's instance data; pointers are offsets from its start):
///   Texture:      +4 pointer to the DDS file, +8 its size.
///   W3DHierarchy: +4 pivot count, +8 pointer to pivots (104 bytes: name length, name pointer, parent,
///                 translation, rotation quaternion x y z w, fixup matrix).
///   W3DContainer: +4 hierarchy (import), +8 sub-object count, +12 pointer to sub-objects (16 bytes: bone index,
///                 id length, id pointer, render object (import)).
///   W3DMesh:      +4 pointer to the vertex buffer, +12 bounding box min/max, +36 sphere radius and centre,
///                 +52 triangle count, +56 pointer to triangles (24 bytes: 3, pointer to 3 indices, normal,
///                 distance), +60/+64 shader name, +68/+72 technique name, +76/+80 shader constants (pointers
///                 to: type hash, name length, name pointer, value).
///   Vertex buffer: vertex count, stride, pointer to the vertices, declaration size, pointer to the Direct3D 9
///                 vertex declaration (8-byte elements, ended by stream 0xFF).
/// </summary>
public static class SageW3d
{
    /// <summary>Model names get this prefix so they never clash with Zero Hour's.</summary>
    public const string ModelPrefix = "KW_";
    /// <summary>Texture files are offered as "kw/&lt;name&gt;.dds".</summary>
    public const string TextureFolder = "kw/";

    const uint ConstantTexture = 0xA59096A6, ConstantFloat = 0xDF08DF25;

    static uint U32(ReadOnlySpan<byte> d, int at) => at >= 0 && at + 4 <= d.Length ? BitConverter.ToUInt32(d[at..]) : 0;
    static float F32(ReadOnlySpan<byte> d, int at) => at >= 0 && at + 4 <= d.Length ? BitConverter.ToSingle(d[at..]) : 0;
    static Vector3 V3(ReadOnlySpan<byte> d, int at) => new(F32(d, at), F32(d, at + 4), F32(d, at + 8));

    static string Text(ReadOnlySpan<byte> d, int lengthAt)
    {
        int length = (int)U32(d, lengthAt), at = (int)U32(d, lengthAt + 4);
        return length > 0 && at >= 0 && at + length <= d.Length ? Encoding.ASCII.GetString(d.Slice(at, length)) : "";
    }

    /// <summary>The referenced asset an import slot holds (null if the slot is not an import).</summary>
    static SageManifest.Asset? Import(SageStream s, SageManifest.Asset a, ReadOnlySpan<byte> d, int at, List<uint> imports) =>
        imports.Contains((uint)at) ? s.Reference(a, (int)U32(d, at)) : null;

    /// <summary>A Texture asset's DDS file.</summary>
    public static byte[]? TextureDds(SageStream s, SageManifest.Asset texture)
    {
        var d = s.Data(texture);
        int at = (int)U32(d, 4), size = (int)U32(d, 8);
        if (size <= 0 || at + size > d.Length || U32(d, at) != 0x20534444) // "DDS "
            return null;
        return d.Slice(at, size).ToArray();
    }

    public static W3dHierarchy? Hierarchy(SageStream s, SageManifest.Asset? asset)
    {
        if (asset == null || asset.TypeName != "W3DHierarchy")
            return null;
        var d = s.Data(asset);
        var h = new W3dHierarchy { Name = ModelPrefix + asset.InstanceName };
        int count = (int)U32(d, 4), at = (int)U32(d, 8);
        for (int i = 0; i < count && at + i * 104 + 104 <= d.Length; i++)
        {
            int p = at + i * 104;
            h.Pivots.Add(new W3dPivot
            {
                Name = Text(d, p),
                ParentIndex = (int)U32(d, p + 8),
                Translation = V3(d, p + 12),
                Rotation = new Quaternion(F32(d, p + 24), F32(d, p + 28), F32(d, p + 32), F32(d, p + 36)),
            });
        }
        return h;
    }

    /// <summary>A container (or a lone mesh) as a W3D file named <see cref="ModelPrefix"/> + its name.</summary>
    public static W3dFile? Model(SageStream s, string name)
    {
        if (s.Find("W3DContainer", name) is { } container)
            return Container(s, container);
        if (s.Find("W3DAnimation", name) is { } animationAsset && Animation(s, animationAsset) is { } animation)
        {
            var file = new W3dFile();
            file.Animations.Add(animation);
            return file;
        }
        if (s.Find("W3DHierarchy", name) is { } hierarchyAsset)
            return new W3dFile { Hierarchy = Hierarchy(s, hierarchyAsset) };
        if (s.Find("W3DMesh", name) is { } lone && Mesh(s, lone, ModelPrefix + name, "MESH") is { } mesh)
        {
            var file = new W3dFile();
            file.Meshes.Add(mesh);
            return file;
        }
        return null;
    }

    static W3dFile Container(SageStream s, SageManifest.Asset container)
    {
        var d = s.Data(container);
        var imports = s.Imports(container);
        string model = ModelPrefix + container.InstanceName;
        var file = new W3dFile { Hierarchy = Hierarchy(s, Import(s, container, d, 4, imports)) };
        var hlod = new W3dHLod { Name = model, HierarchyName = file.Hierarchy?.Name ?? "" };
        var lod = new List<W3dSubObject>();
        int count = (int)U32(d, 8), at = (int)U32(d, 12);
        for (int i = 0; i < count && at + i * 16 + 16 <= d.Length; i++)
        {
            int o = at + i * 16;
            int bone = (int)U32(d, o);
            string id = Text(d, o + 4);
            if (Import(s, container, d, o + 12, imports) is not { TypeName: "W3DMesh" } meshAsset)
                continue;
            if (id.Length == 0)
                id = meshAsset.InstanceName[(meshAsset.InstanceName.IndexOf('.') + 1)..];
            if (Mesh(s, meshAsset, model, id) is not { } mesh)
                continue;
            file.Meshes.Add(mesh);
            lod.Add(new W3dSubObject(bone, mesh.FullName));
        }
        hlod.Lods.Add(lod);
        file.HLod = hlod;
        return file;
    }

    const uint ChannelDelta = 0x77F97305;

    /// <summary>
    /// A W3DAnimation: +4 hierarchy (import), +8 frame count, +12 frame rate, +16 channel count, +20 pointer to
    /// channel pointers. A channel: type hash, then channel type (X, Y, Z translation, orientation, visibility),
    /// frame count, vector length, pivot, then either adaptive delta (scale, initial values, bits per delta,
    /// packed deltas) or time-coded keys (frames with a step flag, values).
    /// </summary>
    public static W3dAnimation? Animation(SageStream s, SageManifest.Asset asset)
    {
        var d = s.Data(asset);
        if (d.Length < 24)
            return null;
        var imports = s.Imports(asset);
        var animation = new W3dAnimation
        {
            Name = ModelPrefix + asset.InstanceName,
            HierarchyName = Import(s, asset, d, 4, imports)?.InstanceName is { } h ? ModelPrefix + h : "",
            FrameCount = (int)U32(d, 8),
            FrameRate = Math.Max(1, (int)U32(d, 12)),
        };
        int count = (int)U32(d, 16), list = (int)U32(d, 20);
        for (int i = 0; i < count; i++)
        {
            int c = (int)U32(d, list + i * 4);
            if (c <= 0 || c + 24 > d.Length)
                continue;
            uint kind = U32(d, c), type = U32(d, c + 4);
            int frames = (int)U32(d, c + 8), vector = (int)U32(d, c + 12), pivot = (int)U32(d, c + 16);
            if (vector is < 1 or > 4 || frames <= 0)
                continue;
            float[] values;
            int[]? keys = null;
            bool[]? steps = null;
            if (kind == ChannelDelta)
            {
                float scale = F32(d, c + 20);
                int init = (int)U32(d, c + 28), bits = (int)U32(d, c + 32), packed = (int)U32(d, c + 40), packedSize = (int)U32(d, c + 36);
                values = new float[frames * vector];
                int packetSize = bits == 8 ? 17 : 9;
                for (int k = 0; k < vector; k++)
                {
                    float value = F32(d, init + k * 4);
                    values[k] = value;
                    for (int f = 1; f < frames; f++)
                    {
                        int group = (f - 1) / 16, inGroup = (f - 1) % 16;
                        int packet = packed + (group * vector + k) * packetSize;
                        if (packet + packetSize > packed + packedSize || packet + packetSize > d.Length)
                        {
                            values[f * vector + k] = value;
                            continue;
                        }
                        float filter = W3dReader.AdaptiveDeltaFilter[d[packet]] * scale;
                        float delta;
                        if (bits == 8)
                            delta = (sbyte)d[packet + 1 + inGroup] / 16f;
                        else
                        {
                            int nibble = d[packet + 1 + inGroup / 2];
                            nibble = (inGroup & 1) != 0 ? nibble >> 4 : nibble & 0xF;
                            delta = nibble >= 8 ? nibble - 16 : nibble;
                        }
                        value += delta * filter;
                        values[f * vector + k] = value;
                    }
                }
            }
            else
            {
                int keyCount = (int)U32(d, c + 20), keyList = (int)U32(d, c + 24), valueCount = (int)U32(d, c + 28), valueList = (int)U32(d, c + 32);
                keyCount = Math.Min(keyCount, valueCount / vector);
                if (keyCount <= 0 || keyList + keyCount * 2 > d.Length || valueList + keyCount * vector * 4 > d.Length)
                    continue;
                keys = new int[keyCount];
                steps = new bool[keyCount];
                values = new float[keyCount * vector];
                for (int k = 0; k < keyCount; k++)
                {
                    int code = BitConverter.ToUInt16(d.Slice(keyList + k * 2, 2));
                    keys[k] = code & 0x7FFF;
                    steps[k] = (code & 0x8000) != 0;
                    for (int j = 0; j < vector; j++)
                        values[k * vector + j] = F32(d, valueList + (k * vector + j) * 4);
                }
            }
            if (type == 4)
            {
                // Visibility: one value a frame (or a key), non-zero = shown.
                var shown = new bool[Math.Max(animation.FrameCount, frames)];
                for (int f = 0; f < shown.Length; f++)
                {
                    float v;
                    if (keys == null)
                        v = values[Math.Min(f, values.Length - 1)];
                    else
                    {
                        int k = 0;
                        while (k + 1 < keys.Length && keys[k + 1] <= f)
                            k++;
                        v = values[k];
                    }
                    shown[f] = v > 0.5f;
                }
                animation.AddVisibility(pivot, new W3dAnimation.VisibilityChannel(0, shown.Length > 0 && shown[^1], shown));
                continue;
            }
            var channelType = type switch { 0 => W3dChannelType.X, 1 => W3dChannelType.Y, 2 => W3dChannelType.Z, _ => W3dChannelType.Quaternion };
            if (channelType == W3dChannelType.Quaternion && vector != 4)
                continue;
            animation.AddChannel(pivot, channelType, keys == null
                ? new W3dAnimation.RawChannel(0, vector, values)
                : new W3dAnimation.TimeCodedChannel(vector, keys, steps!, values));
        }
        return animation;
    }

    // Direct3D 9 declaration types.
    const int Float1 = 0, Float2 = 1, Float3 = 2, Float4 = 3, Color = 4, UByte4 = 5, Short2 = 6, Short4 = 7, UByte4N = 8,
        Short2N = 9, Short4N = 10, UShort2N = 11, UShort4N = 12, Dec3N = 14, Half2 = 15, Half4 = 16;
    // Direct3D 9 usages.
    const int Position = 0, BlendWeight = 1, BlendIndices = 2, Normal = 3, TexCoord = 5, ColorUsage = 10;

    static Vector4 Element(ReadOnlySpan<byte> d, int at, int type)
    {
        Span<float> S = stackalloc float[4], US = stackalloc float[4], H = stackalloc float[4], B = stackalloc float[4];
        for (int k = 0; k < 4; k++)
        {
            bool word = at + k * 2 + 2 <= d.Length;
            S[k] = word ? BitConverter.ToInt16(d[(at + k * 2)..]) : 0;
            US[k] = word ? BitConverter.ToUInt16(d[(at + k * 2)..]) : 0;
            H[k] = word ? (float)BitConverter.ToHalf(d[(at + k * 2)..]) : 0;
            B[k] = at + k < d.Length ? d[at + k] : 0;
        }
        switch (type)
        {
            case Float1: return new(F32(d, at), 0, 0, 1);
            case Float2: return new(F32(d, at), F32(d, at + 4), 0, 1);
            case Float3: return new(V3(d, at), 1);
            case Float4: return new(V3(d, at), F32(d, at + 12));
            case Color: return new(B[2] / 255, B[1] / 255, B[0] / 255, B[3] / 255); // stored B G R A
            case UByte4: return new(B[0], B[1], B[2], B[3]);
            case UByte4N: return new(B[0] / 255, B[1] / 255, B[2] / 255, B[3] / 255);
            case Short2: return new(S[0], S[1], 0, 1);
            case Short4: return new(S[0], S[1], S[2], S[3]);
            case Short2N: return new(S[0] / 32767, S[1] / 32767, 0, 1);
            case Short4N: return new(S[0] / 32767, S[1] / 32767, S[2] / 32767, S[3] / 32767);
            case UShort2N: return new(US[0] / 65535, US[1] / 65535, 0, 1);
            case UShort4N: return new(US[0] / 65535, US[1] / 65535, US[2] / 65535, US[3] / 65535);
            case Half2: return new(H[0], H[1], 0, 1);
            case Half4: return new(H[0], H[1], H[2], H[3]);
            case Dec3N:
            {
                uint v = U32(d, at);
                static float N(uint x) => ((int)(x << 22) >> 22) / 511f;
                return new(N(v), N(v >> 10), N(v >> 20), 1);
            }
            default: return Vector4.Zero;
        }
    }

    public static W3dMesh? Mesh(SageStream s, SageManifest.Asset asset, string model, string name)
    {
        var d = s.Data(asset);
        if (d.Length < 0x60)
            return null;
        var mesh = new W3dMesh
        {
            Name = name.ToUpperInvariant(),
            ContainerName = model,
            BoundsMin = V3(d, 0x0C),
            BoundsMax = V3(d, 0x18),
            SphereRadius = F32(d, 0x24),
            SphereCenter = V3(d, 0x28),
        };

        // The vertex buffer and its declaration.
        int vb = (int)U32(d, 4);
        int vertexCount = (int)U32(d, vb), stride = (int)U32(d, vb + 4), vertices = (int)U32(d, vb + 8);
        int declSize = (int)U32(d, vb + 12), decl = (int)U32(d, vb + 16);
        if (vertexCount <= 0 || stride <= 0 || vertices + (long)vertexCount * stride > d.Length || decl + declSize > d.Length)
            return null;
        var elements = new List<(int Offset, int Type, int Usage, int Index)>();
        for (int e = decl; e + 8 <= decl + declSize; e += 8)
        {
            if (d[e] == 0xFF || BitConverter.ToUInt16(d[e..]) != 0)
                break;
            elements.Add((BitConverter.ToUInt16(d[(e + 2)..]), d[e + 4], d[e + 6], d[e + 7]));
        }
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var colors = new Vector4[vertexCount];
        var bones = new ushort[vertexCount];
        bool hasColor = false, skinned = elements.Any(e => e.Usage == BlendIndices);
        // A skin's bone table: its blend indices pick from these hierarchy pivots.
        int paletteCount = (int)U32(d, vb + 20), palette = (int)U32(d, vb + 24);
        var bonePalette = new ushort[Math.Max(0, paletteCount)];
        for (int i = 0; i < bonePalette.Length && palette + i * 2 + 2 <= d.Length; i++)
            bonePalette[i] = BitConverter.ToUInt16(d.Slice(palette + i * 2, 2));
        ushort Bone(int index) => index < bonePalette.Length ? bonePalette[index] : (ushort)index;
        var position2 = new Vector3[2];
        var normal2 = new Vector3[2];
        for (int v = 0; v < vertexCount; v++)
        {
            int at = vertices + v * stride;
            colors[v] = Vector4.One;
            Vector4 weights = new(1, 0, 0, 0);
            int index0 = 0, index1 = 0;
            foreach (var e in elements)
            {
                var value = Element(d, at + e.Offset, e.Type);
                switch (e.Usage)
                {
                    case Position when e.Index < 2: position2[e.Index] = new(value.X, value.Y, value.Z); break;
                    case Normal when e.Index < 2: normal2[e.Index] = new(value.X, value.Y, value.Z); break;
                    // Direct3D's V runs down the image, W3D's up.
                    case TexCoord when e.Index == 0: uvs[v] = new(value.X, 1 - value.Y); break;
                    case ColorUsage when e.Index == 0: colors[v] = value; hasColor = true; break;
                    case BlendWeight: weights = value; break;
                    case BlendIndices:
                        // Raw bytes (the shaders read them with D3DCOLORtoUBYTE4).
                        index0 = at + e.Offset < d.Length ? d[at + e.Offset] : 0;
                        index1 = at + e.Offset + 1 < d.Length ? d[at + e.Offset + 1] : 0;
                        break;
                }
            }
            // Skins keep each vertex once per bone it follows, in that bone's space (as W3D always has);
            // Zero Hour's follow one bone, so take the heavier.
            int k = skinned && weights.Y > weights.X ? 1 : 0;
            positions[v] = position2[k];
            normals[v] = normal2[k];
            if (skinned)
                bones[v] = Bone(k == 0 ? index0 : index1);
            position2[1] = position2[0];
            normal2[1] = normal2[0];
        }
        mesh.Positions = positions;
        mesh.Normals = normals;
        // +8 geometry type (Normal, Skin, CameraAligned, CameraOriented); +0x5D casts a shadow, +0x5E hidden.
        mesh.Flags |= U32(d, 8) switch
        {
            2 => W3dMeshFlags.CameraAligned,
            3 => W3dMeshFlags.CameraOriented,
            _ => 0,
        };
        if (d[0x5D] != 0)
            mesh.Flags |= W3dMeshFlags.CastShadow;
        if (d[0x5E] != 0)
            mesh.Flags |= W3dMeshFlags.Hidden;
        if (skinned)
        {
            mesh.BoneInfluences = bones;
            mesh.Flags |= W3dMeshFlags.Skin;
        }

        // Triangles.
        int triangles = (int)U32(d, 0x34), list = (int)U32(d, 0x38);
        var indicesOut = new List<int>(triangles * 3);
        for (int t = 0; t < triangles && list + t * 24 + 24 <= d.Length; t++)
        {
            int at = (int)U32(d, list + t * 24 + 4);
            int a = (int)U32(d, at), b = (int)U32(d, at + 4), c = (int)U32(d, at + 8);
            if (a < vertexCount && b < vertexCount && c < vertexCount)
                indicesOut.AddRange([a, b, c]);
        }
        mesh.Indices = indicesOut.ToArray();

        // The shader's textures and settings.
        string shader = Text(d, 0x3C);
        var imports = s.Imports(asset);
        var textures = new List<(string Constant, string Texture)>();
        var floats = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        int constantCount = (int)U32(d, 0x4C), constants = (int)U32(d, 0x50);
        var bytes = d.ToArray();
        for (int k = 0; k < constantCount; k++)
        {
            int c = (int)U32(d, constants + k * 4);
            uint type = U32(d, c);
            string constant = Text(d, c + 4);
            if (type == ConstantTexture && Import(s, asset, bytes, c + 12, imports) is { } texture)
                textures.Add((constant, texture.InstanceName));
            else if (type == ConstantFloat && U32(d, c + 12) > 0)
                floats[constant] = F32(d, (int)U32(d, c + 16));
        }
        string? diffuse = textures.FirstOrDefault(t => t.Constant.Contains("Diffuse", StringComparison.OrdinalIgnoreCase)).Texture
            ?? textures.FirstOrDefault(t => t.Constant is "Texture_0" or "Texture" or "BaseTexture").Texture
            ?? textures.FirstOrDefault(t => !t.Constant.Contains("Normal", StringComparison.OrdinalIgnoreCase)
                && !t.Constant.Contains("Spec", StringComparison.OrdinalIgnoreCase)).Texture;
        var pass = new W3dMaterialPass();
        if (hasColor)
            pass.DiffuseColors = colors;
        var shaderSettings = new W3dShader
        {
            DepthCompare = 3, DepthMask = 1, SrcBlend = W3dShader.SrcOne, DestBlend = W3dShader.DstZero, PrimaryGradient = 1,
            Texturing = (byte)(diffuse != null ? 1 : 0),
        };
        bool additive = shader.Contains("Additive", StringComparison.OrdinalIgnoreCase);
        bool alpha = shader.Contains("Alpha", StringComparison.OrdinalIgnoreCase) || floats.ContainsKey("AlphaTestThreshold")
            || shader.StartsWith("FX", StringComparison.OrdinalIgnoreCase) || shader.Contains("Mask", StringComparison.OrdinalIgnoreCase);
        if (additive)
        {
            shaderSettings.SrcBlend = W3dShader.SrcOne;
            shaderSettings.DestBlend = W3dShader.DstOne;
            shaderSettings.DepthMask = 0;
        }
        else if (alpha)
            shaderSettings.AlphaTest = 1;
        mesh.Shaders.Add(shaderSettings);
        mesh.VertexMaterials.Add(new W3dVertexMaterial { Name = shader, Diffuse = Vector3.One, Ambient = Vector3.One });
        pass.ShaderIds = [0];
        pass.VertexMaterialIds = [0];
        if (diffuse != null)
        {
            mesh.Textures.Add(new W3dTexture { Name = TextureFolder + diffuse + ".dds" });
            var stage = new W3dTextureStage { TextureIds = [0], TexCoords = uvs };
            pass.Stages.Add(stage);
        }
        mesh.Passes.Add(pass);
        return mesh;
    }
}
