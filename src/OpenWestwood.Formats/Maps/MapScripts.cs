using OpenWestwood.Formats.Compression;
using System.Numerics;

namespace OpenWestwood.Formats.Maps;

/// <summary>
/// The players ("sides") and teams defined by a map, plus each side's scripts.
/// Mission logic in the campaign lives entirely in these scripts.
/// </summary>
public sealed class SidesList
{
    public List<MapSide> Sides { get; } = new();

    /// <summary>Team definitions (dictionaries with teamName, teamOwner, unit lists, behaviour...).</summary>
    public List<Dictionary<string, object>> Teams { get; } = new();

    internal static SidesList Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        var list = new SidesList();
        // (Kane's Wrath's version 6 starts with a flag.)
        if (chunk.Version >= 6)
            reader.ReadByte();
        int sideCount = reader.ReadInt32();
        for (int i = 0; i < sideCount; i++)
        {
            var side = new MapSide { Properties = chunks.ReadDict() };
            int buildCount = reader.ReadInt32();
            for (int j = 0; j < buildCount; j++)
            {
                var entry = new BuildListEntry
                {
                    BuildingName = chunks.ReadAsciiString(),
                    TemplateName = chunks.ReadAsciiString(),
                    Position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) with { Z = 0 },
                    Angle = reader.ReadSingle(),
                    InitiallyBuilt = reader.ReadByte() != 0,
                    Rebuilds = reader.ReadInt32(),
                };
                if (chunk.Version >= 3)
                {
                    entry.Script = chunks.ReadAsciiString();
                    entry.Health = reader.ReadInt32();
                    entry.Whiner = reader.ReadByte() != 0;
                    entry.Unsellable = reader.ReadByte() != 0;
                    entry.Repairable = reader.ReadByte() != 0;
                }
                side.BuildList.Add(entry);
            }
            list.Sides.Add(side);
        }

        // (Kane's Wrath keeps its teams in a Teams chunk inside the list.)
        if (chunk.Version >= 2 && chunk.Version < 6)
        {
            int teamCount = reader.ReadInt32();
            for (int i = 0; i < teamCount; i++)
                list.Teams.Add(chunks.ReadDict());
        }

        // Nested: PlayerScriptsList → one ScriptList per side (and Kane's Wrath's Teams).
        foreach (var sub in chunks.Chunks(chunk.End))
        {
            if (sub.Label == "Teams")
            {
                int teamCount = reader.ReadInt32();
                for (int i = 0; i < teamCount; i++)
                    list.Teams.Add(chunks.ReadDict());
                continue;
            }
            if (sub.Label != "PlayerScriptsList")
                continue;
            int sideIndex = 0;
            foreach (var scriptListChunk in chunks.SubChunks(sub))
            {
                if (scriptListChunk.Label != "ScriptList")
                    continue;
                var scripts = ScriptList.Read(chunks, scriptListChunk);
                if (sideIndex < list.Sides.Count)
                    list.Sides[sideIndex].Scripts = scripts;
                sideIndex++;
            }
        }

        return list;
    }
}

public sealed class MapSide
{
    /// <summary>playerName, playerFaction, playerIsHuman, playerDisplayName, playerAllies, playerEnemies, ...</summary>
    public Dictionary<string, object> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BuildListEntry> BuildList { get; } = new();
    public ScriptList Scripts { get; set; } = new();

    public string Name => Properties.TryGetValue("playerName", out var v) ? v.ToString() ?? "" : "";
    public string Faction => Properties.TryGetValue("playerFaction", out var v) ? v.ToString() ?? "" : "";
}

/// <summary>A structure the AI (or the map) should build or rebuild at a fixed spot.</summary>
public sealed class BuildListEntry
{
    public string BuildingName { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public Vector3 Position { get; set; }
    public float Angle { get; set; }
    public bool InitiallyBuilt { get; set; }
    public int Rebuilds { get; set; }
    public string Script { get; set; } = "";
    public int Health { get; set; } = 100;
    public bool Whiner { get; set; }
    public bool Unsellable { get; set; }
    public bool Repairable { get; set; }
}

/// <summary>All scripts of one side, loose or in groups.</summary>
public sealed class ScriptList
{
    public List<MapScript> Scripts { get; } = new();
    public List<ScriptGroup> Groups { get; } = new();

    public IEnumerable<MapScript> AllScripts => Scripts.Concat(Groups.SelectMany(g => g.Scripts));

    internal static ScriptList Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var list = new ScriptList();
        foreach (var sub in chunks.SubChunks(chunk))
        {
            if (sub.Label == "Script")
            {
                list.Scripts.Add(MapScript.Read(chunks, sub));
            }
            else if (sub.Label == "ScriptGroup")
            {
                var group = new ScriptGroup
                {
                    Name = chunks.ReadAsciiString(),
                    IsActive = chunks.Reader.ReadByte() != 0,
                };
                if (sub.Version == 2)
                    group.IsSubroutine = chunks.Reader.ReadByte() != 0;
                foreach (var scriptChunk in chunks.Chunks(sub.End))
                    if (scriptChunk.Label == "Script")
                        group.Scripts.Add(MapScript.Read(chunks, scriptChunk));
                list.Groups.Add(group);
            }
        }
        return list;
    }
}

public sealed class ScriptGroup
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool IsSubroutine { get; set; }
    public List<MapScript> Scripts { get; } = new();
}

/// <summary>
/// One script: "if (any OR-clause, each an AND of conditions) then actions else false-actions".
/// Scripts run once (one-shot) or every evaluation, optionally only on some difficulties.
/// </summary>
public sealed class MapScript
{
    public string Name { get; set; } = "";
    public string Comment { get; set; } = "";
    public string ConditionComment { get; set; } = "";
    public string ActionComment { get; set; } = "";
    public bool IsActive { get; set; }
    public bool IsOneShot { get; set; }
    public bool Easy { get; set; }
    public bool Normal { get; set; }
    public bool Hard { get; set; }
    public bool IsSubroutine { get; set; }
    public int DelayEvaluationSeconds { get; set; }

    /// <summary>The script is true when any inner list is true; an inner list is true when all its conditions are.</summary>
    public List<List<ScriptCondition>> OrConditions { get; } = new();
    public List<ScriptAction> Actions { get; } = new();
    public List<ScriptAction> FalseActions { get; } = new();

    internal static MapScript Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        var script = new MapScript
        {
            Name = chunks.ReadAsciiString(),
            Comment = chunks.ReadAsciiString(),
            ConditionComment = chunks.ReadAsciiString(),
            ActionComment = chunks.ReadAsciiString(),
            IsActive = reader.ReadByte() != 0,
            IsOneShot = reader.ReadByte() != 0,
            Easy = reader.ReadByte() != 0,
            Normal = reader.ReadByte() != 0,
            Hard = reader.ReadByte() != 0,
            IsSubroutine = reader.ReadByte() != 0,
        };
        if (chunk.Version >= 2)
            script.DelayEvaluationSeconds = reader.ReadInt32();

        foreach (var sub in chunks.Chunks(chunk.End))
        {
            switch (sub.Label)
            {
                case "OrCondition":
                    var and = new List<ScriptCondition>();
                    foreach (var conditionChunk in chunks.SubChunks(sub))
                        if (conditionChunk.Label == "Condition")
                            and.Add(ScriptCondition.Read(chunks, conditionChunk));
                    script.OrConditions.Add(and);
                    break;
                case "ScriptAction":
                    script.Actions.Add(ScriptAction.Read(chunks, sub, nameKeyVersion: 2));
                    break;
                case "ScriptActionFalse":
                    script.FalseActions.Add(ScriptAction.Read(chunks, sub, nameKeyVersion: 2));
                    break;
            }
        }
        return script;
    }
}

/// <summary>A script condition, identified by its internal name (e.g. "TEAM_DESTROYED").</summary>
public sealed class ScriptCondition
{
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public List<ScriptParameter> Parameters { get; } = new();

    internal static ScriptCondition Read(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        var condition = new ScriptCondition { TypeId = reader.ReadInt32() };
        if (chunk.Version >= 4)
            condition.Name = chunks.NameOf((uint)(reader.ReadInt32() >> 8));
        // Older maps (all of the original Generals') store only the number.
        if (condition.Name.Length == 0)
            condition.Name = ScriptNames.Condition(condition.TypeId);
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            condition.Parameters.Add(ScriptParameter.Read(chunks));
        HealOldFile(condition, chunk.Version);
        return condition;
    }

    // The original's ParameterChangesVer2: conditions that gained a third parameter in version 2.
    static readonly HashSet<string> ParameterChangesVer2 =
    [
        "TEAM_INSIDE_AREA_PARTIALLY", "TEAM_INSIDE_AREA_ENTIRELY", "TEAM_OUTSIDE_AREA_ENTIRELY", "TEAM_ENTERED_AREA_PARTIALLY",
        "TEAM_ENTERED_AREA_ENTIRELY", "TEAM_EXITED_AREA_ENTIRELY", "TEAM_EXITED_AREA_PARTIALLY",
    ];

    /// <summary>The original's "heal old files" in Condition::ParseConditionDataChunk.</summary>
    static void HealOldFile(ScriptCondition condition, int version)
    {
        var p = condition.Parameters;
        if (version < 2 && ParameterChangesVer2.Contains(condition.Name))
        {
            while (p.Count > 2)
                p.RemoveAt(p.Count - 1);
            p.Add(new ScriptParameter { Type = ScriptParameter.SurfacesAllowedType, Int = 3 });
        }
        if (condition.Name == "SKIRMISH_SPECIAL_POWER_READY" && p.Count == 1)
            p.Insert(0, new ScriptParameter { Type = ScriptParameter.SideType, String = ScriptParameter.ThisPlayer });
    }

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>A script action, identified by its internal name (e.g. "VICTORY").</summary>
public sealed class ScriptAction
{
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public List<ScriptParameter> Parameters { get; } = new();

    internal static ScriptAction Read(DataChunkReader chunks, DataChunkReader.Chunk chunk, int nameKeyVersion)
    {
        var reader = chunks.Reader;
        var action = new ScriptAction { TypeId = reader.ReadInt32() };
        if (chunk.Version >= nameKeyVersion)
            action.Name = chunks.NameOf((uint)(reader.ReadInt32() >> 8));
        // Older maps (all of the original Generals') store only the number.
        if (action.Name.Length == 0)
            action.Name = ScriptNames.Action(action.TypeId);
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
            action.Parameters.Add(ScriptParameter.Read(chunks));
        HealOldFile(action);
        return action;
    }

    /// <summary>The original's "heal old files" in ScriptAction::ParseAction: parameters that later versions added.</summary>
    static void HealOldFile(ScriptAction action)
    {
        var p = action.Parameters;
        static ScriptParameter Real(float value) => new() { Type = ScriptParameter.RealType, Real = value };
        static ScriptParameter Bool(bool value) => new() { Type = ScriptParameter.BooleanType, Int = value ? 1 : 0 };
        switch (action.Name)
        {
            case "SKIRMISH_FIRE_SPECIAL_POWER_AT_MOST_COST":
                if (p.Count == 1)
                    p.Insert(0, new ScriptParameter { Type = ScriptParameter.SideType, String = ScriptParameter.ThisPlayer });
                break;
            case "TEAM_FOLLOW_WAYPOINTS":
                if (p.Count == 2)
                    p.Add(Bool(true));
                break;
            case "SKIRMISH_BUILD_BASE_DEFENSE_FRONT":
                if (p.Count == 1)
                {
                    bool flank = p[0].Int != 0;
                    p.Clear();
                    if (flank)
                        action.Name = "SKIRMISH_BUILD_BASE_DEFENSE_FLANK";
                }
                break;
            case "NAMED_SET_ATTITUDE":
            case "TEAM_SET_ATTITUDE":
                if (p.Count >= 2 && p[1].Type == ScriptParameter.IntType)
                    p[1].Type = ScriptParameter.AiMoodType;
                break;
            case "MAP_REVEAL_AT_WAYPOINT":
            case "MAP_SHROUD_AT_WAYPOINT":
                if (p.Count == 2)
                    p.Add(new ScriptParameter { Type = ScriptParameter.SideType });
                break;
            case "MAP_REVEAL_ALL":
            case "MAP_REVEAL_ALL_PERM":
            case "MAP_REVEAL_ALL_UNDO_PERM":
            case "MAP_SHROUD_ALL":
                if (p.Count == 0)
                    p.Add(new ScriptParameter { Type = ScriptParameter.SideType });
                break;
            case "SPEECH_PLAY":
                if (p.Count == 1)
                    p.Add(Bool(true)); // "Default it to TRUE, as per conversation with JohnL"
                break;
            case "CAMERA_MOD_SET_FINAL_ZOOM":
            case "CAMERA_MOD_SET_FINAL_PITCH":
                if (p.Count == 1)
                {
                    p.Add(new ScriptParameter { Type = ScriptParameter.PercentType });
                    p.Add(new ScriptParameter { Type = ScriptParameter.PercentType });
                }
                break;
            case "MOVE_CAMERA_TO":
            case "MOVE_CAMERA_ALONG_WAYPOINT_PATH":
            case "CAMERA_LOOK_TOWARD_OBJECT":
                if (p.Count == 3)
                {
                    p.Add(Real(0));
                    p.Add(Real(0));
                }
                break;
            case "RESET_CAMERA":
            case "ZOOM_CAMERA":
            case "PITCH_CAMERA":
            case "ROTATE_CAMERA":
                if (p.Count == 2)
                {
                    p.Add(Real(0));
                    p.Add(Real(0));
                }
                break;
            case "CAMERA_LOOK_TOWARD_WAYPOINT":
                if (p.Count == 2)
                {
                    p.Add(Real(0));
                    p.Add(Real(0));
                    p.Add(Bool(false));
                }
                else if (p.Count == 4)
                    p.Add(Bool(false));
                break;
        }
    }

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>A script parameter. Which field is meaningful depends on <see cref="Type"/>.</summary>
public sealed class ScriptParameter
{
    /// <summary>Original ParameterType enum value (0 = INT, 1 = REAL, 3 = TEAM, 7 = WAYPOINT, 16 = COORD3D, ...).</summary>
    public int Type { get; set; }
    public int Int { get; set; }
    public float Real { get; set; }
    public string String { get; set; } = "";
    public Vector3 Coord { get; set; }

    // The original's Parameter::ParameterType values that the reader itself needs.
    public const int IntType = 0, RealType = 1, BooleanType = 8, SideType = 11, AiMoodType = 20, SurfacesAllowedType = 36, PercentType = 51;
    /// <summary>The original's THIS_PLAYER.</summary>
    public const string ThisPlayer = "<This Player>";

    const int Coord3DType = 16;
    const int ObjectType = 15;
    const int UpgradeType = 32;

    internal static ScriptParameter Read(DataChunkReader chunks)
    {
        var reader = chunks.Reader;
        var parameter = new ScriptParameter { Type = reader.ReadInt32() };
        if (parameter.Type == Coord3DType)
        {
            parameter.Coord = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }
        else
        {
            parameter.Int = reader.ReadInt32();
            parameter.Real = reader.ReadSingle();
            parameter.String = chunks.ReadAsciiString();
        }

        // Fix-ups the original loader applied to old data.
        if (parameter.Type == ObjectType && parameter.String.StartsWith("Fundamentalist", StringComparison.Ordinal))
            parameter.String = "GLA" + parameter.String["Fundamentalist".Length..];
        if (parameter.Type == UpgradeType && parameter.String is "Upgrade_AmericaRangerCaptureBuilding"
                or "Upgrade_ChinaRedguardCaptureBuilding" or "Upgrade_GLARebelCaptureBuilding")
            parameter.String = "Upgrade_InfantryCaptureBuilding";

        return parameter;
    }

    public override string ToString() => Type == Coord3DType ? Coord.ToString() : String.Length > 0 ? String : Int != 0 ? Int.ToString() : Real.ToString();
}

/// <summary>A named area on the map, used by scripts ("unit entered area") and for water.</summary>
public sealed class PolygonTrigger
{
    public string Name { get; set; } = "";
    public string Layer { get; set; } = "";
    public int Id { get; set; }
    public bool IsWater { get; set; }
    public bool IsRiver { get; set; }
    public int RiverStart { get; set; }
    public List<Vector3> Points { get; } = new();

    /// <summary>True if a point (x, y) lies inside the polygon.</summary>
    public bool Contains(float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var a = Points[i];
            var b = Points[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    internal static IEnumerable<PolygonTrigger> ReadAll(DataChunkReader chunks, DataChunkReader.Chunk chunk)
    {
        var reader = chunks.Reader;
        int count = reader.ReadInt32();
        var list = new List<PolygonTrigger>(count);
        for (int t = 0; t < count; t++)
        {
            var trigger = new PolygonTrigger { Name = chunks.ReadAsciiString() };
            if (chunk.Version >= 4)
                trigger.Layer = chunks.ReadAsciiString();
            trigger.Id = reader.ReadInt32();
            if (chunk.Version >= 2)
                trigger.IsWater = reader.ReadByte() != 0;
            if (chunk.Version >= 3)
            {
                trigger.IsRiver = reader.ReadByte() != 0;
                trigger.RiverStart = reader.ReadInt32();
            }
            int points = reader.ReadInt32();
            for (int i = 0; i < points; i++)
                trigger.Points.Add(new Vector3(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
            if (points >= 2)
                list.Add(trigger);
        }
        return list;
    }
}

/// <summary>
/// A standalone script file (.scb), such as Data/Scripts/SkirmishScripts.scb: script lists for named
/// players (ScriptsPlayers + PlayerScriptsList) and team definitions (ScriptTeams). Skirmish games give
/// these to the computer players by side.
/// </summary>
public sealed class ScriptFile
{
    public List<string> PlayerNames { get; } = new();
    public List<Dictionary<string, object>> PlayerProperties { get; } = new();
    public List<ScriptList> ScriptLists { get; } = new();
    public List<Dictionary<string, object>> Teams { get; } = new();

    public static ScriptFile Read(byte[] fileData)
    {
        var data = RefPack.DecompressIfNeeded(fileData);
        var chunks = new DataChunkReader(data);
        var reader = chunks.Reader;
        var file = new ScriptFile();
        foreach (var chunk in chunks.Chunks())
        {
            switch (chunk.Label)
            {
                case "PlayerScriptsList":
                    foreach (var list in chunks.SubChunks(chunk))
                        if (list.Label == "ScriptList")
                            file.ScriptLists.Add(ScriptList.Read(chunks, list));
                    break;
                case "ScriptsPlayers":
                {
                    bool readDicts = chunk.Version >= 2 && reader.ReadInt32() != 0;
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        file.PlayerNames.Add(chunks.ReadAsciiString());
                        file.PlayerProperties.Add(readDicts ? chunks.ReadDict() : new Dictionary<string, object>());
                    }
                    break;
                }
                case "ScriptTeams":
                    while (reader.BaseStream.Position < chunk.End)
                        file.Teams.Add(chunks.ReadDict());
                    break;
            }
            reader.BaseStream.Position = chunk.End;
        }
        return file;
    }
}
