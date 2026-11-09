using System.Numerics;
using System.Reflection;
using OpenWestwood.Logic.Commands;

namespace OpenWestwood.Logic.Network;

/// <summary>
/// Writes player orders (the GameCommand records) to bytes and reads them back, for network games (and replays).
/// Every command record is found by reflection: its id is its place in the alphabetical list of command names, and
/// its fields are its constructor's parameters in order. A new command needs no code here, only fields of the kinds
/// below (int, uint, uint[], float, bool, string, Vector3, Vector3?).
/// </summary>
public static class CommandCodec
{
    sealed record Shape(Type Type, ConstructorInfo Constructor, ParameterInfo[] Parameters, PropertyInfo[] Properties);

    static readonly Shape[] shapes;
    static readonly Dictionary<Type, int> ids = new();

    static CommandCodec()
    {
        shapes = typeof(GameCommand).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(GameCommand).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select(t =>
            {
                var constructor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
                var parameters = constructor.GetParameters();
                var properties = parameters.Select(p => t.GetProperty(p.Name!) ?? throw new InvalidOperationException($"{t.Name}.{p.Name} is not a property")).ToArray();
                return new Shape(t, constructor, parameters, properties);
            }).ToArray();
        for (int i = 0; i < shapes.Length; i++)
            ids[shapes[i].Type] = i;
    }

    /// <summary>A fingerprint of the command list: two builds can play together only if theirs match.</summary>
    public static uint Version
    {
        get
        {
            uint hash = 2166136261;
            foreach (var shape in shapes)
                foreach (char c in shape.Type.Name + string.Concat(shape.Parameters.Select(p => p.ParameterType.Name)))
                    hash = (hash ^ c) * 16777619;
            return hash;
        }
    }

    public static void Write(BinaryWriter w, GameCommand command)
    {
        var shape = shapes[ids[command.GetType()]];
        w.Write((ushort)ids[command.GetType()]);
        foreach (var property in shape.Properties)
            WriteValue(w, property.PropertyType, property.GetValue(command));
    }

    public static GameCommand Read(BinaryReader r)
    {
        int id = r.ReadUInt16();
        if (id >= shapes.Length)
            throw new InvalidDataException($"Unknown command {id}.");
        var shape = shapes[id];
        var args = shape.Parameters.Select(p => ReadValue(r, p.ParameterType)).ToArray();
        return (GameCommand)shape.Constructor.Invoke(args);
    }

    static void WriteValue(BinaryWriter w, Type type, object? value)
    {
        switch (value)
        {
            case int i: w.Write(i); return;
            case uint u: w.Write(u); return;
            case float f: w.Write(f); return;
            case bool b: w.Write(b); return;
            case string s: w.Write(s); return;
            case uint[] a:
                w.Write(a.Length);
                foreach (uint u in a)
                    w.Write(u);
                return;
            case Vector3 v:
                if (Nullable.GetUnderlyingType(type) != null)
                    w.Write(true);
                w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
                return;
            case null when Nullable.GetUnderlyingType(type) == typeof(Vector3):
                w.Write(false);
                return;
            case null when type == typeof(string):
                w.Write("");
                return;
            case null when type == typeof(uint[]):
                w.Write(0);
                return;
        }
        throw new NotSupportedException($"Commands can't carry a {type.Name}.");
    }

    static object? ReadValue(BinaryReader r, Type type)
    {
        if (type == typeof(int)) return r.ReadInt32();
        if (type == typeof(uint)) return r.ReadUInt32();
        if (type == typeof(float)) return r.ReadSingle();
        if (type == typeof(bool)) return r.ReadBoolean();
        if (type == typeof(string)) return r.ReadString();
        if (type == typeof(uint[]))
        {
            int n = r.ReadInt32();
            if (n < 0 || n > 10000)
                throw new InvalidDataException("Bad unit list.");
            var a = new uint[n];
            for (int i = 0; i < n; i++)
                a[i] = r.ReadUInt32();
            return a;
        }
        if (type == typeof(Vector3))
            return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        if (Nullable.GetUnderlyingType(type) == typeof(Vector3))
            return r.ReadBoolean() ? new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) : null;
        throw new NotSupportedException($"Commands can't carry a {type.Name}.");
    }
}
