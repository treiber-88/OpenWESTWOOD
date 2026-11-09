using System.IO.Compression;
using OpenWestwood.Formats.Maps;

namespace OpenWestwood.Logic.Saving;

/// <summary>
/// What the load menu shows about a save without loading it: the map, the player's description, when it was saved, how far
/// into the game, and anything the client keeps with it (the campaign and mission, the camera).
/// </summary>
public sealed record SaveHeader(string Map, string Description, DateTime SavedAt, int Frame, IReadOnlyDictionary<string, string> Extras)
{
    public string Extra(string key) => Extras.GetValueOrDefault(key) ?? "";
}

/// <summary>
/// Saved games: a full snapshot of the game (see <see cref="Snapshot"/>). Loading builds a fresh game from the same map and
/// rules (for what the save points at) and then replaces all of its state with the saved one.
/// </summary>
public static class SaveGame
{
    const string Magic = "OPENWESTWOOD-SAVE";
    const int FormatVersion = 1;

    /// <param name="attachment">Something else kept with the game (the client's skirmish setup), or null.</param>
    public static void Write(GameLogic game, MapFile? map, SaveHeader header, Stream output, object? attachment = null)
    {
        var w = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        WriteHeader(w, header with { Frame = game.Frame });
        w.Flush();
        using (var zip = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var externals = new SaveExternals(game, map);
            new Snapshot.Writer(externals).Write(game, zip);
            zip.WriteByte(attachment == null ? (byte)0 : (byte)1);
            if (attachment != null)
                new Snapshot.Writer(externals).Write(attachment, zip);
        }
        output.Flush();
    }

    /// <summary>Saves to a file (written next to it first, so a failed save never spoils an old one).</summary>
    public static void WriteFile(GameLogic game, MapFile? map, SaveHeader header, string path, object? attachment = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        using (var file = File.Create(temp))
            Write(game, map, header, file, attachment);
        File.Move(temp, path, overwrite: true);
    }

    public static SaveHeader ReadHeader(Stream input)
    {
        var r = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            if (r.ReadString() != Magic)
                throw new SaveGameException("This isn't an OpenWESTWOOD saved game.");
            int version = r.ReadInt32();
            if (version > FormatVersion)
                throw new SaveGameException("This game was saved by a newer OpenWESTWOOD.");
            return ReadHeaderFields(r);
        }
        catch (EndOfStreamException e)
        {
            throw new SaveGameException("The saved game is damaged.", e);
        }
    }

    public static SaveHeader ReadHeaderFile(string path)
    {
        using var file = File.OpenRead(path);
        return ReadHeader(file);
    }

    /// <summary>
    /// Replaces the state of <paramref name="fresh"/> (a game just set up on the save's map with the same rules) with the
    /// saved game's.
    /// </summary>
    public static SaveHeader Load(Stream input, GameLogic fresh, MapFile? map) => Load(input, fresh, map, out _);

    public static SaveHeader Load(Stream input, GameLogic fresh, MapFile? map, out object? attachment)
    {
        var header = ReadHeader(input);
        attachment = null;
        try
        {
            using var zip = new BrotliStream(input, CompressionMode.Decompress, leaveOpen: true);
            var buffered = new BufferedStream(zip, 1 << 16);
            var externals = new SaveExternals(fresh, map);
            new Snapshot.Reader(externals).Read(buffered, fresh);
            if (buffered.ReadByte() == 1)
                attachment = new Snapshot.Reader(externals).Read(buffered, null);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IndexOutOfRangeException)
        {
            throw new SaveGameException("The saved game is damaged.", e);
        }
        return header;
    }

    static void WriteHeader(BinaryWriter w, SaveHeader h)
    {
        w.Write(h.Map);
        w.Write(h.Description);
        w.Write(h.SavedAt.ToBinary());
        w.Write(h.Frame);
        w.Write(h.Extras.Count);
        foreach (var (k, v) in h.Extras)
        {
            w.Write(k);
            w.Write(v);
        }
    }

    static SaveHeader ReadHeaderFields(BinaryReader r)
    {
        string map = r.ReadString(), description = r.ReadString();
        var at = DateTime.FromBinary(r.ReadInt64());
        int frame = r.ReadInt32();
        int n = r.ReadInt32();
        var extras = new Dictionary<string, string>();
        for (int i = 0; i < n; i++)
            extras[r.ReadString()] = r.ReadString();
        return new SaveHeader(map, description, at, frame, extras);
    }
}
