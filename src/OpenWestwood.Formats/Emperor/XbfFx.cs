using System.Text;

namespace OpenWestwood.Formats.Emperor;

/// <summary>
/// The effect block at the start of an Emperor .xbf (XbfFile.FxData), worked out from the game's 1529 models (no
/// public documentation exists):
///   "FXDataHeader" in a 20-byte field, int 5, int stateCount, byte hasParticles, [int count, particle systems],
///   [int size (bytes after it), int tracks, per track: int frames, int flags, frames ints (events on each frame),
///   C-string track name ("MASTER"), then each frame's events], then the animation states.
/// An event: int type, int chance (100), int argument mask, then by mask bit, lowest first: 2, 4, 16 a C string;
///   8 and 64 eight bytes; 32 four bytes. Types seen: 3 start a particle system on a node (strings: system, node),
///   4 stop it, 9 play a sound, 7 wakes, 6 texture swaps, 1/2/8 node shows/hides, 10..12 others.
/// The animation frames (and so the event frames) run at 30 a second, as the models' (XbfToW3d.FrameRate).
/// </summary>
public sealed class XbfFx
{
    public List<XbfParticleSystem> Systems { get; } = new();
    public List<XbfFxEvent> Events { get; } = new();
    public int Frames { get; private set; }

    public static XbfFx Read(byte[] data)
    {
        var fx = new XbfFx();
        if (data.Length < 33 || Encoding.ASCII.GetString(data, 0, 12) != "FXDataHeader")
            return fx;
        try
        {
            int o = 20;
            int Int() { int v = BitConverter.ToInt32(data, o); o += 4; return v; }
            float Float() { float v = BitConverter.ToSingle(data, o); o += 4; return v; }
            string LenStr() { int n = Int(); string s = Encoding.Latin1.GetString(data, o, n).Split('\0')[0]; o += n; return s; }
            string CStr() { int e = Array.IndexOf(data, (byte)0, o); string s = Encoding.Latin1.GetString(data, o, e - o); o = e + 1; return s; }

            Int(); // version 5
            Int(); // animation states
            bool hasParticles = data[o++] != 0;
            if (hasParticles)
            {
                int count = Int();
                for (int i = 0; i < count; i++)
                {
                    var s = new XbfParticleSystem { Name = LenStr() };
                    s.Emitter = Int(); s.PerEmission = Int(); s.Lifetime = Int();
                    s.Unknown0 = Float(); s.Speed = Float(); s.Spread = Float(); s.Size = Float();
                    s.Red = Int(); s.Green = Int(); s.Blue = Int();
                    s.Flag0 = Int(); s.Flag1 = Int(); s.Gravity = Float(); s.Drag = Float();
                    s.Spread2 = Float(); s.TextureFrames = Int();
                    s.Texture = LenStr();
                    s.Alpha = Int(); s.RedRate = Int(); s.GreenRate = Int(); s.BlueRate = Int();
                    s.AlphaRate = Int(); s.Flag2 = Int(); s.Flag3 = Int();
                    s.SizeRate = Float();
                    fx.Systems.Add(s);
                }
            }
            // The event block, when there is one (otherwise the animation states' names follow).
            if (o + 8 > data.Length)
                return fx;
            int blockSize = BitConverter.ToInt32(data, o), blockTracks = BitConverter.ToInt32(data, o + 4);
            if (blockSize <= 0 || o + 4 + blockSize > data.Length || blockTracks is < 1 or > 16)
                return fx;
            int size = Int(), end = o + size;
            int tracks = Int();
            for (int t = 0; t < tracks && o < end; t++)
            {
                int frames = Int();
                Int(); // flags
                var counts = new int[frames];
                for (int f = 0; f < frames; f++)
                    counts[f] = Int();
                CStr(); // "MASTER"
                fx.Frames = Math.Max(fx.Frames, frames);
                for (int f = 0; f < frames; f++)
                    for (int e = 0; e < counts[f]; e++)
                    {
                        var ev = new XbfFxEvent { Frame = f, Type = Int(), Chance = Int() };
                        int mask = Int();
                        for (int bit = 1; bit <= 256; bit <<= 1)
                        {
                            if ((mask & bit) == 0)
                                continue;
                            switch (bit)
                            {
                                case 2: case 4: case 16: ev.Strings.Add(CStr()); break;
                                case 8: case 64: o += 8; break;
                                case 32: o += 4; break;
                                default: throw new InvalidDataException($"argument bit {bit}");
                            }
                        }
                        fx.Events.Add(ev);
                    }
            }
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            // (A few blocks use argument kinds not worked out yet: what was read stays.)
        }
        return fx;
    }
}

/// <summary>An Emperor particle system (sizes in model units, times in 30 a second frames). Field meanings partly guessed.</summary>
public sealed class XbfParticleSystem
{
    public string Name = "";
    /// <summary>0, 1 or 2: how it emits (2 = every frame while running, 0/1 = one burst).</summary>
    public int Emitter;
    public int PerEmission, Lifetime;
    public float Unknown0, Speed, Spread, Size;
    public int Red, Green, Blue;
    public int Flag0, Flag1;
    public float Gravity, Drag, Spread2;
    /// <summary>Frames of the texture sequence ("!%Bru" -> "!%Bru0.tga".."!%Bru20.tga").</summary>
    public int TextureFrames;
    public string Texture = "";
    /// <summary>Start alpha and the per-frame colour changes.</summary>
    public int Alpha, RedRate, GreenRate, BlueRate, AlphaRate, Flag2, Flag3;
    /// <summary>Each frame the particle's size is multiplied by this (0 = unchanged).</summary>
    public float SizeRate;
}

public sealed class XbfFxEvent
{
    public int Frame, Type, Chance;
    public List<string> Strings { get; } = new();
}
