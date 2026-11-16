using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenWestwood.Formats.Sage;

/// <summary>
/// A Kane's Wrath interface movie (Apt.big's data/aptui/&lt;movie&gt; stream): EA's APT, Flash's cousin. The stream's assets
/// are the classic APT files, each behind a small header (int 0, int header size, int size[, int id]):
///   AptAptData:&lt;X&gt;_apt     the "Apt Data:7" binary: characters (shapes, sprites, images, texts, buttons...) and the
///                               movie's frames (place / remove object, frame labels; actions are not run here);
///   AptConstData:&lt;X&gt;_const "Apt constant file": where the movie character starts;
///   AptDatData:&lt;X&gt;_dat     "image -&gt; texture" lines;
///   AptGeometryData:&lt;X&gt;_&lt;n&gt; a shape's geometry as text: "c" (next), "s s:r:g:b:a" solid, "s tc:r:g:b:a:image:m11:m12:m21:m22:tx:ty"
///                               textured (the matrix maps the shape's points onto the texture's pixels), "s l:..." lines,
///                               "t x0:y0:x1:y1:x2:y2" triangles, "l ..." lines;
///   Texture:apt_&lt;X&gt;_&lt;id&gt;   the textures (DDS).
/// Layout as OpenSAGE reads the uncompiled files (Data/Apt). Offsets in the binary are from its "Apt Data" start.
/// </summary>
public sealed class AptMovie
{
    public enum CharacterType { Shape = 1, Text = 2, Font = 3, Button = 4, Sprite = 5, Sound = 6, Image = 7, Morph = 8, Movie = 9, StaticText = 10 }

    public abstract class Character { public CharacterType Type { get; init; } }
    public sealed class Shape : Character { public Vector4 Bounds; public int Geometry; }
    public sealed class Image : Character { public int Texture; }
    public sealed class Text : Character
    {
        public Vector4 Bounds; public int Font; public int Alignment; public Vector4 Color; public float Height;
        public string Content = "", Value = "";
    }
    public class Playable : Character { public List<Frame> Frames { get; } = new(); }
    public sealed class Sprite : Playable { }
    public sealed class Button : Character
    {
        public Vector4 Bounds;
        public List<(int State, int Character, int Depth, Matrix3x2 Matrix)> Records { get; } = new();
    }
    public sealed class MovieCharacter : Playable
    {
        public List<Character?> Characters { get; } = new();
        public int Width, Height, MillisecondsPerFrame;
        public List<(string Movie, string Name, int Character)> Imports { get; } = new();
        public List<(string Name, int Character)> Exports { get; } = new();
    }
    public sealed class Other : Character { }

    public sealed class Frame
    {
        public List<PlaceObject> Places { get; } = new();
        public List<int> Removes { get; } = new();
        public List<string> Labels { get; } = new();
    }

    public sealed record PlaceObject(int Flags, int Depth, int Character, Matrix3x2? Matrix, Vector4? Tint, Vector4? Add, string? Name, int ClipDepth)
    {
        public bool IsMove => (Flags & 1) != 0;
        public bool HasCharacter => (Flags & 2) != 0;
    }

    public sealed record GeometryPart(bool Textured, Vector4 Color, int Image, Matrix3x2 TextureMatrix, List<(Vector2 A, Vector2 B, Vector2 C)> Triangles,
        float LineWidth, List<(Vector2 A, Vector2 B)> Lines);

    public string Name { get; private set; } = "";
    public MovieCharacter Movie { get; private set; } = new();
    public Dictionary<int, int> ImageTextures { get; } = new();
    public Dictionary<int, List<GeometryPart>> Geometries { get; } = new();
    /// <summary>The textures' DDS files, by id.</summary>
    public Dictionary<int, byte[]> Textures { get; } = new();

    /// <summary>Reads the movie from its stream (e.g. SageStream.Load(read, "aptui/tacticalhud")).</summary>
    public static AptMovie Read(SageStream stream)
    {
        var apt = new AptMovie();
        byte[]? aptData = null, constData = null;
        foreach (var a in stream.Assets)
        {
            var data = stream.Data(a).ToArray();
            switch (a.TypeName)
            {
                case "AptAptData":
                    aptData = Payload(data);
                    apt.Name = a.InstanceName.EndsWith("_apt", StringComparison.OrdinalIgnoreCase) ? a.InstanceName[..^4] : a.InstanceName;
                    break;
                case "AptConstData":
                    constData = Payload(data);
                    break;
                case "AptDatData":
                    foreach (string line in Encoding.ASCII.GetString(Payload(data)).Split('\n'))
                    {
                        string l = line.Split(';')[0].Trim();
                        if (l.Split(["->", "="], StringSplitOptions.None) is [var image, var texture]
                            && int.TryParse(image.Trim(), out int i) && int.TryParse(texture.Trim().Split(' ', '(')[0], out int t))
                            apt.ImageTextures[i] = t;
                    }
                    break;
                case "AptGeometryData":
                    if (int.TryParse(a.InstanceName[(a.InstanceName.LastIndexOf('_') + 1)..], out int id))
                        apt.Geometries[id] = ParseGeometry(Encoding.ASCII.GetString(Payload(data)));
                    break;
                case "Texture":
                    if (int.TryParse(a.InstanceName[(a.InstanceName.LastIndexOf('_') + 1)..], out int tid) && SageW3d.TextureDds(stream, a) is { } dds)
                        apt.Textures[tid] = dds;
                    break;
            }
        }
        if (aptData == null || constData == null)
            throw new InvalidDataException("not an APT movie stream");
        int entry = BitConverter.ToInt32(constData, 20);
        var r = new Reader(aptData);
        r.At = entry;
        if (apt.ReadCharacter(r) is MovieCharacter movie)
            apt.Movie = movie;
        return apt;
    }

    /// <summary>A stream asset's file: after its header (whose second int is the header's size).</summary>
    static byte[] Payload(byte[] data)
    {
        if (data.Length < 12)
            return data;
        int header = BitConverter.ToInt32(data, 4);
        return header is >= 8 and <= 64 && header <= data.Length ? data[header..] : data;
    }

    sealed class Reader(byte[] data)
    {
        public readonly byte[] Data = data;
        public int At;
        public int Int() { int v = BitConverter.ToInt32(Data, At); At += 4; return v; }
        public float Float() { float v = BitConverter.ToSingle(Data, At); At += 4; return v; }
        public Vector4 Color() { var v = new Vector4(Data[At], Data[At + 1], Data[At + 2], Data[At + 3]) / 255f; At += 4; return v; }
        public string StringAt()
        {
            int offset = Int();
            if (offset <= 0 || offset >= Data.Length)
                return "";
            int end = Array.IndexOf(Data, (byte)0, offset);
            return Encoding.ASCII.GetString(Data, offset, (end < 0 ? Data.Length : end) - offset);
        }
        /// <summary>A list: int count, int offset; items there in a row, or (pointers) offsets to each item.</summary>
        public List<T?> ListAt<T>(Func<T> item, bool pointers = false)
        {
            int count = Int(), offset = Int();
            var list = new List<T?>();
            if (count <= 0 || offset <= 0 || offset >= Data.Length)
                return list;
            int back = At;
            At = offset;
            for (int i = 0; i < count; i++)
            {
                if (!pointers)
                {
                    list.Add(item());
                    continue;
                }
                int p = Int();
                if (p == 0)
                {
                    list.Add(default);
                    continue;
                }
                int next = At;
                At = p;
                list.Add(item());
                At = next;
            }
            At = back;
            return list;
        }
    }

    bool movieRead;

    Character? ReadCharacter(Reader r)
    {
        var type = (CharacterType)r.Int();
        if ((uint)r.Int() != 0x09876543)
            throw new InvalidDataException("bad APT character");
        switch (type)
        {
            case CharacterType.Movie:
            {
                if (movieRead)
                    return null;
                movieRead = true;
                var movie = new MovieCharacter { Type = type };
                movie.Frames.AddRange(r.ListAt(() => ReadFrame(r))!);
                r.Int();
                movie.Characters.AddRange(r.ListAt(() => ReadCharacter(r), pointers: true));
                movie.Width = r.Int();
                movie.Height = r.Int();
                movie.MillisecondsPerFrame = r.Int();
                foreach (var import in r.ListAt(() => (r.StringAt(), r.StringAt(), r.Int(), r.Int())))
                    movie.Imports.Add((import.Item1, import.Item2, import.Item3));
                foreach (var export in r.ListAt(() => (r.StringAt(), r.Int())))
                    movie.Exports.Add((export.Item1, export.Item2));
                if (movie.Characters.Count > 0)
                    movie.Characters[0] = movie;
                return movie;
            }
            case CharacterType.Shape:
                return new Shape { Type = type, Bounds = new Vector4(r.Float(), r.Float(), r.Float(), r.Float()), Geometry = r.Int() };
            case CharacterType.Image:
                return new Image { Type = type, Texture = r.Int() };
            case CharacterType.Sprite:
            {
                var sprite = new Sprite { Type = type };
                sprite.Frames.AddRange(r.ListAt(() => ReadFrame(r))!);
                return sprite;
            }
            case CharacterType.Text:
            {
                var t = new Text { Type = type, Bounds = new Vector4(r.Float(), r.Float(), r.Float(), r.Float()), Font = r.Int(), Alignment = r.Int(), Color = r.Color(), Height = r.Float() };
                r.Int(); r.Int(); r.Int(); // read only, multiline, word wrap
                t.Content = r.StringAt();
                t.Value = r.StringAt();
                return t;
            }
            case CharacterType.Button:
            {
                var b = new Button { Type = type };
                r.Int(); // is menu
                b.Bounds = new Vector4(r.Float(), r.Float(), r.Float(), r.Float());
                r.Int(); r.Int(); r.Int(); r.Int(); // triangle and vertex counts and their offsets
                foreach (var rec in r.ListAt(() =>
                {
                    int flags = r.Data[r.At];
                    r.At += 4;
                    int character = r.Int(), depth = r.Int();
                    var m = new Matrix3x2(r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float());
                    r.At += 32; // colour (4 floats), 4 unknown floats
                    return (flags, character, depth, m);
                }))
                    b.Records.Add(rec);
                return b;
            }
            default:
                return new Other { Type = type };
        }
    }

    Frame ReadFrame(Reader r)
    {
        var frame = new Frame();
        foreach (var _ in r.ListAt<object?>(() =>
        {
            int kind = r.Int();
            switch (kind)
            {
                case 2: // frame label
                    frame.Labels.Add(r.StringAt());
                    r.Int();
                    r.Int();
                    break;
                case 3: // place object
                {
                    int flags = r.Data[r.At];
                    r.At += 4;
                    int depth = r.Int();
                    int character = r.Int();
                    var m = new Matrix3x2(r.Float(), r.Float(), r.Float(), r.Float(), r.Float(), r.Float());
                    var tint = r.Color();
                    var add = r.Color();
                    r.Float(); // ratio
                    string? name = (flags & 32) != 0 ? r.StringAt() : null;
                    if (name == null)
                        r.Int();
                    int clipDepth = r.Int();
                    frame.Places.Add(new PlaceObject(flags, depth, character, (flags & 4) != 0 ? m : null,
                        (flags & 8) != 0 ? tint : null, (flags & 8) != 0 ? add : null, name, clipDepth));
                    break;
                }
                case 4: // remove object
                    frame.Removes.Add(r.Int());
                    break;
            }
            return null;
        }, pointers: true)) { }
        return frame;
    }

    static List<GeometryPart> ParseGeometry(string text)
    {
        var parts = new List<GeometryPart>();
        GeometryPart? current = null;
        static float F(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            char mode = line[0];
            string[] p = line.TrimStart('c', 's', 'l', 't').Trim().Split(':');
            try
            {
                switch (mode)
                {
                    case 'c':
                        current = null;
                        break;
                    case 's':
                        if (p[0] == "tc" && p.Length >= 12)
                            current = new GeometryPart(true, new Vector4(F(p[1]), F(p[2]), F(p[3]), F(p[4])) / 255f, int.Parse(p[5]),
                                new Matrix3x2(F(p[6]), F(p[7]), F(p[8]), F(p[9]), F(p[10]), F(p[11])), new(), 0, new());
                        else if (p[0] == "s" && p.Length >= 5)
                            current = new GeometryPart(false, new Vector4(F(p[1]), F(p[2]), F(p[3]), F(p[4])) / 255f, -1, Matrix3x2.Identity, new(), 0, new());
                        else if (p[0] == "l" && p.Length >= 6)
                            current = new GeometryPart(false, new Vector4(F(p[2]), F(p[3]), F(p[4]), F(p[5])) / 255f, -1, Matrix3x2.Identity, new(), F(p[1]), new());
                        if (current != null)
                            parts.Add(current);
                        break;
                    case 't':
                        current?.Triangles.Add((new Vector2(F(p[0]), F(p[1])), new Vector2(F(p[2]), F(p[3])), new Vector2(F(p[4]), F(p[5]))));
                        break;
                    case 'l':
                        current?.Lines.Add((new Vector2(F(p[0]), F(p[1])), new Vector2(F(p[2]), F(p[3]))));
                        break;
                }
            }
            catch (FormatException) { }
            catch (IndexOutOfRangeException) { }
        }
        return parts;
    }
}
