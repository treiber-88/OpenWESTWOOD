using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace OpenWestwood.Logic.Saving;

/// <summary>
/// The whole state of a game as a snapshot: every object reachable from the game (its objects, modules, players, AI,
/// scripts, teams, pathfinding, fog...) stored field by field, with shared references kept shared. What comes from the
/// rules, models and map is stored as a key (see <see cref="SaveExternals"/>). Anything that belongs to the client (the
/// screen, sounds, effects being drawn) is left out; the client is rebuilt around the loaded game.
///
/// Dictionaries and sets are stored as their contents and refilled on load, in the same order (hash codes of strings and
/// objects change between runs, so their internal tables can't be copied).
/// </summary>
static class Snapshot
{
    const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    enum Tag : byte { Null, Ref, External, String, Primitive, Enum, Struct, Delegate, Type, Skipped }

    enum Shape : byte { Fields, Array, Dictionary, Set }

    enum ComparerKind : byte { Default, Ordinal, OrdinalIgnoreCase, Reference, Other }

    static readonly ConditionalWeakTable<Type, FieldInfo[]> fieldCache = new();

    /// <summary>A type's instance fields, base class first (the names in a save include the declaring class).</summary>
    public static FieldInfo[] FieldsOf(Type type) => fieldCache.GetValue(type, t =>
    {
        var chain = new List<Type>();
        for (var c = t; c != null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
            chain.Add(c);
        chain.Reverse();
        return chain.SelectMany(c => c.GetFields(InstanceFields)).Where(f => !f.FieldType.IsPointer).ToArray();
    });

    static string FieldKey(FieldInfo f) => f.DeclaringType!.Name + "." + f.Name;

    /// <summary>Types the game's state is made of (the rest, the client's, is left out).</summary>
    public static bool IsGameType(Type type)
    {
        string assembly = type.Assembly.GetName().Name ?? "";
        return assembly is "OpenWestwood.Logic" or "OpenWestwood.Core" or "OpenWestwood.Formats" or "OpenWestwood.TiberianDawn"
            || assembly.StartsWith("System", StringComparison.Ordinal) || assembly is "mscorlib" or "netstandard";
    }

    static Shape ShapeOf(Type type)
    {
        if (type.IsArray)
            return Shape.Array;
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(Dictionary<,>))
                return Shape.Dictionary;
            if (def == typeof(HashSet<>))
                return Shape.Set;
        }
        return Shape.Fields;
    }

    static string TypeName(Type type) => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    // ================================================================ Writing

    public sealed class Writer
    {
        readonly SaveExternals externals;
        readonly Dictionary<object, int> ids = new(ReferenceEqualityComparer.Instance);
        readonly List<object> objects = new();
        readonly Dictionary<Type, int> typeIds = new();
        readonly List<Type> types = new();
        readonly BinaryWriter body = new(new MemoryStream());
        readonly HashSet<string> skipped = new();

        public Writer(SaveExternals externals) => this.externals = externals;

        /// <summary>Client types the game pointed at and the save left out (for diagnostics).</summary>
        public IReadOnlyCollection<string> Skipped => skipped;

        int TypeId(Type type)
        {
            if (!typeIds.TryGetValue(type, out int id))
            {
                id = types.Count;
                typeIds[type] = id;
                types.Add(type);
            }
            return id;
        }

        int Id(object obj)
        {
            if (!ids.TryGetValue(obj, out int id))
            {
                id = objects.Count;
                ids[obj] = id;
                objects.Add(obj);
            }
            return id;
        }

        public void Write(object root, Stream output)
        {
            Id(root);
            for (int i = 0; i < objects.Count; i++)
                WriteBody(objects[i]);

            var w = new BinaryWriter(output);
            // Types, with their field names.
            w.Write7BitEncodedInt(types.Count);
            // (Writing a type's fields can't add types: the table is complete once the bodies are written.)
            foreach (var type in types)
            {
                w.Write(TypeName(type));
                var fields = ShapeOf(type) == Shape.Fields && !type.IsArray ? FieldsOf(type) : [];
                w.Write7BitEncodedInt(fields.Length);
                foreach (var f in fields)
                    w.Write(FieldKey(f));
            }
            // The objects: type and (arrays) dimensions.
            w.Write7BitEncodedInt(objects.Count);
            foreach (var obj in objects)
            {
                var type = obj.GetType();
                w.Write7BitEncodedInt(typeIds[type]);
                if (obj is Array array)
                {
                    w.Write7BitEncodedInt(array.Rank);
                    for (int d = 0; d < array.Rank; d++)
                        w.Write7BitEncodedInt(array.GetLength(d));
                }
            }
            var bytes = (MemoryStream)body.BaseStream;
            w.Write7BitEncodedInt64(bytes.Length);
            bytes.Position = 0;
            bytes.CopyTo(output);
            w.Flush();
        }

        void WriteBody(object obj)
        {
            var type = obj.GetType();
            TypeId(type);
            switch (ShapeOf(type))
            {
                case Shape.Array:
                    WriteArray((Array)obj);
                    break;
                case Shape.Dictionary:
                {
                    var dict = (IDictionary)obj;
                    WriteComparer(type.GetProperty("Comparer")!.GetValue(obj), type.GetGenericArguments()[0]);
                    body.Write7BitEncodedInt(dict.Count);
                    foreach (DictionaryEntry e in dict)
                    {
                        WriteValue(e.Key);
                        WriteValue(e.Value);
                    }
                    break;
                }
                case Shape.Set:
                {
                    WriteComparer(type.GetProperty("Comparer")!.GetValue(obj), type.GetGenericArguments()[0]);
                    var items = ((IEnumerable)obj).Cast<object?>().ToList();
                    body.Write7BitEncodedInt(items.Count);
                    foreach (var item in items)
                        WriteValue(item);
                    break;
                }
                default:
                    foreach (var f in FieldsOf(type))
                        WriteValue(f.GetValue(obj));
                    break;
            }
        }

        void WriteArray(Array array)
        {
            var element = array.GetType().GetElementType()!;
            if (element.IsPrimitive)
            {
                int length = Buffer.ByteLength(array);
                var raw = new byte[length];
                Buffer.BlockCopy(array, 0, raw, 0, length);
                body.Write(raw);
                return;
            }
            foreach (var item in array)
                WriteValue(item);
        }

        void WriteComparer(object? comparer, Type keyType)
        {
            var defaultComparer = typeof(EqualityComparer<>).MakeGenericType(keyType).GetProperty("Default")!.GetValue(null);
            if (comparer == null || ReferenceEquals(comparer, defaultComparer))
                body.Write((byte)ComparerKind.Default);
            else if (ReferenceEquals(comparer, StringComparer.Ordinal))
                body.Write((byte)ComparerKind.Ordinal);
            else if (ReferenceEquals(comparer, StringComparer.OrdinalIgnoreCase))
                body.Write((byte)ComparerKind.OrdinalIgnoreCase);
            else if (ReferenceEquals(comparer, ReferenceEqualityComparer.Instance))
                body.Write((byte)ComparerKind.Reference);
            else
            {
                body.Write((byte)ComparerKind.Other);
                WriteValue(comparer);
            }
        }

        void WriteValue(object? value)
        {
            if (value == null)
            {
                body.Write((byte)Tag.Null);
                return;
            }
            var type = value.GetType();
            if (value is string s)
            {
                body.Write((byte)Tag.String);
                body.Write(s);
                return;
            }
            if (type.IsEnum)
            {
                body.Write((byte)Tag.Enum);
                body.Write7BitEncodedInt(TypeId(type));
                body.Write(EnumBits(value));
                return;
            }
            if (type.IsPrimitive)
            {
                body.Write((byte)Tag.Primitive);
                WritePrimitive(value);
                return;
            }
            if (value is Type t)
            {
                body.Write((byte)Tag.Type);
                body.Write(TypeName(t));
                return;
            }
            if (value is Delegate d)
            {
                WriteDelegate(d);
                return;
            }
            if (!IsGameType(type))
            {
                skipped.Add(type.FullName ?? type.Name);
                body.Write((byte)Tag.Skipped);
                return;
            }
            if (type.IsValueType)
            {
                body.Write((byte)Tag.Struct);
                body.Write7BitEncodedInt(TypeId(type));
                foreach (var f in FieldsOf(type))
                    WriteValue(f.GetValue(value));
                return;
            }
            if (externals.KeyOf(value) is { } key)
            {
                body.Write((byte)Tag.External);
                body.Write(key);
                return;
            }
            if (type.Namespace?.StartsWith("OpenWestwood.Formats", StringComparison.Ordinal) == true)
                throw new SaveGameException($"The game points at file data the save can't store: {type.FullName}.");
            if (value is MemberInfo or Assembly or Module or System.Threading.Thread or Stream)
                throw new SaveGameException($"The game holds something that can't be saved: {type.FullName}.");
            body.Write((byte)Tag.Ref);
            body.Write7BitEncodedInt(Id(value));
        }

        static long EnumBits(object value) => Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
        {
            TypeCode.UInt64 => unchecked((long)Convert.ToUInt64(value)),
            _ => Convert.ToInt64(value),
        };

        void WritePrimitive(object value)
        {
            var code = Type.GetTypeCode(value.GetType());
            if (value is IntPtr ip)
            {
                body.Write((byte)TypeCode.Int64);
                body.Write(ip.ToInt64());
                return;
            }
            if (value is UIntPtr up)
            {
                body.Write((byte)TypeCode.UInt64);
                body.Write(up.ToUInt64());
                return;
            }
            body.Write((byte)code);
            switch (value)
            {
                case bool v: body.Write(v); break;
                case char v: body.Write((ushort)v); break;
                case sbyte v: body.Write(v); break;
                case byte v: body.Write(v); break;
                case short v: body.Write(v); break;
                case ushort v: body.Write(v); break;
                case int v: body.Write(v); break;
                case uint v: body.Write(v); break;
                case long v: body.Write(v); break;
                case ulong v: body.Write(v); break;
                case float v: body.Write(v); break;
                case double v: body.Write(v); break;
                default: throw new SaveGameException($"Can't save a {value.GetType()}.");
            }
        }

        void WriteDelegate(Delegate d)
        {
            // Only the parts that belong to the game (the client hooks itself up again after loading).
            var parts = d.GetInvocationList().Where(p => IsGameType(p.Method.DeclaringType!) && (p.Target == null || IsGameType(p.Target.GetType()))).ToList();
            if (parts.Count == 0)
            {
                body.Write((byte)Tag.Null);
                return;
            }
            body.Write((byte)Tag.Delegate);
            body.Write7BitEncodedInt(TypeId(d.GetType()));
            body.Write7BitEncodedInt(parts.Count);
            foreach (var p in parts)
            {
                WriteValue(p.Target);
                var m = p.Method;
                body.Write7BitEncodedInt(TypeId(m.DeclaringType!));
                body.Write(m.Name);
                var parameters = m.GetParameters();
                body.Write7BitEncodedInt(parameters.Length);
                foreach (var parameter in parameters)
                    body.Write(TypeName(parameter.ParameterType));
            }
        }
    }

    // ================================================================ Reading

    public sealed class Reader
    {
        readonly SaveExternals externals;
        Type[] types = [];
        readonly Dictionary<Type, int> typeIndex = new();
        FieldInfo?[][] layouts = [];
        object[] objects = [];
        readonly List<(object Collection, object? Comparer, List<(object? Key, object? Value)> Items, bool IsSet)> hashed = new();
        BinaryReader body = null!;

        public Reader(SaveExternals externals) => this.externals = externals;

        /// <summary>
        /// Reads a snapshot written by <see cref="Writer"/>; its first object is filled into <paramref name="root"/> (or made,
        /// when null) and returned.
        /// </summary>
        public object Read(Stream input, object? root)
        {
            var r = new BinaryReader(input);
            int typeCount = r.Read7BitEncodedInt();
            types = new Type[typeCount];
            layouts = new FieldInfo?[typeCount][];
            for (int i = 0; i < typeCount; i++)
            {
                string name = r.ReadString();
                types[i] = Type.GetType(name, throwOnError: false) ?? throw new SaveGameException($"This version of the game doesn't have '{name}' (the save is from another version).");
                typeIndex.TryAdd(types[i], i);
                var current = FieldsOf(types[i]).ToDictionary(FieldKey);
                int fieldCount = r.Read7BitEncodedInt();
                layouts[i] = new FieldInfo?[fieldCount];
                for (int f = 0; f < fieldCount; f++)
                    layouts[i][f] = current.GetValueOrDefault(r.ReadString());
            }
            int objectCount = r.Read7BitEncodedInt();
            objects = new object[objectCount];
            for (int i = 0; i < objectCount; i++)
            {
                var type = types[r.Read7BitEncodedInt()];
                if (type.IsArray)
                {
                    int rank = r.Read7BitEncodedInt();
                    var lengths = new int[rank];
                    for (int d = 0; d < rank; d++)
                        lengths[d] = r.Read7BitEncodedInt();
                    objects[i] = Array.CreateInstance(type.GetElementType()!, lengths);
                }
                else if (i == 0 && root != null)
                {
                    if (root.GetType() != type)
                        throw new SaveGameException("The saved game isn't a game of this kind.");
                    objects[i] = root;
                }
                else
                    objects[i] = RuntimeHelpers.GetUninitializedObject(type);
            }
            long length = r.Read7BitEncodedInt64();
            body = r;
            for (int i = 0; i < objectCount; i++)
                ReadBody(objects[i]);
            // Dictionaries and sets last, once every key they hash is filled in.
            foreach (var (collection, comparer, items, isSet) in hashed)
                Refill(collection, comparer, items, isSet);
            return objects[0];
        }

        void ReadBody(object obj)
        {
            var type = obj.GetType();
            switch (ShapeOf(type))
            {
                case Shape.Array:
                    ReadArray((Array)obj);
                    break;
                case Shape.Dictionary or Shape.Set:
                {
                    bool isSet = ShapeOf(type) == Shape.Set;
                    var comparer = ReadComparer(type.GetGenericArguments()[0]);
                    int count = body.Read7BitEncodedInt();
                    var items = new List<(object?, object?)>(count);
                    for (int i = 0; i < count; i++)
                        items.Add(isSet ? (ReadValue(), null) : (ReadValue(), ReadValue()));
                    hashed.Add((obj, comparer, items, isSet));
                    break;
                }
                default:
                {
                    int typeId = typeIndex[type];
                    foreach (var f in layouts[typeId])
                    {
                        var value = ReadValue();
                        if (f != null)
                            Set(f, obj, value);
                    }
                    break;
                }
            }
        }

        static void Set(FieldInfo f, object target, object? value)
        {
            if (value == null && f.FieldType.IsValueType && Nullable.GetUnderlyingType(f.FieldType) == null)
                return;
            if (value != null && !f.FieldType.IsInstanceOfType(value) && !(Nullable.GetUnderlyingType(f.FieldType)?.IsInstanceOfType(value) ?? false))
                return; // (the field changed type since the save)
            f.SetValue(target, value);
        }

        void ReadArray(Array array)
        {
            var element = array.GetType().GetElementType()!;
            if (element.IsPrimitive)
            {
                int length = Buffer.ByteLength(array);
                var raw = body.ReadBytes(length);
                Buffer.BlockCopy(raw, 0, array, 0, length);
                return;
            }
            int total = array.Length;
            if (array.Rank == 1)
            {
                for (int i = 0; i < total; i++)
                    array.SetValue(ReadValue(), i);
                return;
            }
            var index = new int[array.Rank];
            for (int n = 0; n < total; n++)
            {
                int rest = n;
                for (int d = array.Rank - 1; d >= 0; d--)
                {
                    int len = array.GetLength(d);
                    index[d] = rest % len;
                    rest /= len;
                }
                array.SetValue(ReadValue(), index);
            }
        }

        object? ReadComparer(Type keyType) => (ComparerKind)body.ReadByte() switch
        {
            ComparerKind.Default => null,
            ComparerKind.Ordinal => StringComparer.Ordinal,
            ComparerKind.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            ComparerKind.Reference => ReferenceEqualityComparer.Instance,
            _ => ReadValue(),
        };

        static void Refill(object collection, object? comparer, List<(object? Key, object? Value)> items, bool isSet)
        {
            var type = collection.GetType();
            var comparerType = typeof(IEqualityComparer<>).MakeGenericType(type.GetGenericArguments()[0]);
            type.GetConstructor([comparerType])!.Invoke(collection, [comparer]);
            if (isSet)
            {
                var add = type.GetMethod("Add")!;
                foreach (var (key, _) in items)
                    add.Invoke(collection, [key]);
            }
            else
            {
                var dict = (IDictionary)collection;
                foreach (var (key, value) in items)
                    dict[key!] = value;
            }
        }

        object? ReadValue()
        {
            var tag = (Tag)body.ReadByte();
            switch (tag)
            {
                case Tag.Null or Tag.Skipped:
                    return null;
                case Tag.Ref:
                    return objects[body.Read7BitEncodedInt()];
                case Tag.External:
                    return externals.Resolve(body.ReadString());
                case Tag.String:
                    return body.ReadString();
                case Tag.Primitive:
                    return ReadPrimitive();
                case Tag.Enum:
                {
                    var type = types[body.Read7BitEncodedInt()];
                    return Enum.ToObject(type, body.ReadInt64());
                }
                case Tag.Type:
                {
                    string name = body.ReadString();
                    return Type.GetType(name, throwOnError: false);
                }
                case Tag.Struct:
                {
                    int typeId = body.Read7BitEncodedInt();
                    var boxed = RuntimeHelpers.GetUninitializedObject(types[typeId]);
                    foreach (var f in layouts[typeId])
                    {
                        var value = ReadValue();
                        if (f != null)
                            Set(f, boxed, value);
                    }
                    return boxed;
                }
                case Tag.Delegate:
                    return ReadDelegate();
                default:
                    throw new SaveGameException("The saved game is damaged.");
            }
        }

        object ReadPrimitive() => (TypeCode)body.ReadByte() switch
        {
            TypeCode.Boolean => body.ReadBoolean(),
            TypeCode.Char => (char)body.ReadUInt16(),
            TypeCode.SByte => body.ReadSByte(),
            TypeCode.Byte => body.ReadByte(),
            TypeCode.Int16 => body.ReadInt16(),
            TypeCode.UInt16 => body.ReadUInt16(),
            TypeCode.Int32 => body.ReadInt32(),
            TypeCode.UInt32 => body.ReadUInt32(),
            TypeCode.Int64 => body.ReadInt64(),
            TypeCode.UInt64 => body.ReadUInt64(),
            TypeCode.Single => body.ReadSingle(),
            TypeCode.Double => body.ReadDouble(),
            _ => throw new SaveGameException("The saved game is damaged."),
        };

        Delegate? ReadDelegate()
        {
            var delegateType = types[body.Read7BitEncodedInt()];
            int count = body.Read7BitEncodedInt();
            Delegate? result = null;
            for (int i = 0; i < count; i++)
            {
                var target = ReadValue();
                var declaring = types[body.Read7BitEncodedInt()];
                string name = body.ReadString();
                int parameterCount = body.Read7BitEncodedInt();
                var parameterTypes = new Type?[parameterCount];
                for (int p = 0; p < parameterCount; p++)
                    parameterTypes[p] = Type.GetType(body.ReadString(), throwOnError: false);
                var method = declaring.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Select(x => x.ParameterType).SequenceEqual(parameterTypes!))
                    ?? throw new SaveGameException($"This version of the game doesn't have {declaring.Name}.{name} (the save is from another version).");
                var part = method.IsStatic ? Delegate.CreateDelegate(delegateType, method) : Delegate.CreateDelegate(delegateType, target, method);
                result = Delegate.Combine(result, part);
            }
            return result;
        }
    }
}
