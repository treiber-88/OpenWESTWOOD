using System.Numerics;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;

namespace OpenWestwood.Logic.Missions;

/// <summary>Teams (TEAM.CPP), reinforcements (Do_Reinforcements) and the computer houses' production (HouseClass::AI).</summary>
public sealed partial class TdMission
{
    /// <summary>The TD type of an object (its template's name after the side's prefix: TDNOD_E1 is E1).</summary>
    static string TdType(GameObject obj) => obj.Template.Name.IndexOf('_') is int i and > 0 ? obj.Template.Name[(i + 1)..] : obj.Template.Name;

    bool InATeam(uint id) => Teams.Any(t => t.Members.Contains(id));

    int TeamCount(string type) => Teams.Count(t => t.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    // ---- Teams ----

    /// <summary>TeamTypeClass::Create_One_Of (a trigger's team is made whatever the limit, as during scenario start).</summary>
    Team? CreateTeam(TeamType type, bool forced, bool byTrigger = true)
    {
        if (!byTrigger && TeamCount(type.Name) >= type.MaxAllowed)
            return null;
        var team = new Team { Type = type.Name, ForcedActive = forced };
        Teams.Add(team);
        Recruit(team, type);
        return team;
    }

    /// <summary>TeamClass::Recruit: free units of the house and the wanted types join.</summary>
    void Recruit(Team team, TeamType type)
    {
        foreach (var (wanted, count) in type.Classes)
        {
            int have = team.Members.Count(id => game.FindObject(id) is { } m && TdType(m).Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (have >= count)
                continue;
            foreach (var obj in Owned(type.House).Where(o => !o.IsKindOf(Kind.Structure) && TdType(o).Equals(wanted, StringComparison.OrdinalIgnoreCase) && !InATeam(o.Id)).ToList())
            {
                team.Members.Add(obj.Id);
                if (++have >= count)
                    break;
            }
        }
    }

    void UpdateTeams()
    {
        foreach (var team in Teams.ToList())
        {
            if (FindTeamType(team.Type) is not { } type)
            {
                Teams.Remove(team);
                continue;
            }
            team.Members.RemoveAll(id => game.FindObject(id) is not { IsEffectivelyDead: false });
            var members = team.Members.Select(id => game.FindObject(id)!).ToList();
            int desired = type.Classes.Sum(c => c.Count);
            bool human = Player(type.House)?.IsHuman ?? false;
            bool full = members.Count >= desired;
            if (members.Count == 0 && (team.HasBeen || human))
            {
                Teams.Remove(team);
                continue;
            }

            // Ready: off it goes.
            if (!team.Moving && members.Count > 0 && (full || team.ForcedActive))
            {
                team.Moving = true;
                team.HasBeen = true;
                team.Mission = -1;
                team.NextMission = true;
                team.ForcedActive = false;
            }
            if (!team.Moving || (!full && type.Reinforcable && !human))
                Recruit(team, type);
            if (!team.Moving || members.Count == 0)
                continue;

            if (team.NextMission)
            {
                team.NextMission = false;
                team.Mission++;
                if (team.Mission >= type.Missions.Count)
                {
                    Teams.Remove(team);
                    continue;
                }
                StartTeamMission(team, type, members);
            }
            RunTeamMission(team, type, members);
        }
    }

    void StartTeamMission(Team team, TeamType type, List<GameObject> members)
    {
        var (mission, argument) = type.Missions[team.Mission];
        team.TimeOutFrame = game.Frame + argument * TenthOfAMinute;
        team.Target = 0;
        team.Arrived = false;
        switch (mission)
        {
            case "Move":
            case "Unload":
            case "Defend Base":
            case "Retreat":
            {
                // (A negative waypoint, as SCG07EA's grd3 "Move:-16", is no waypoint: the team stays.)
                var to = mission is "Move" or "Unload" ? Waypoint(argument) : Center(members);
                team.Destination = to ?? Center(members);
                foreach (var m in members)
                    m.AI?.MoveTo(team.Destination, CommandSource.Script);
                break;
            }
            case "Move to Cell":
                team.Destination = CellCenter(argument);
                foreach (var m in members)
                    m.AI?.MoveTo(team.Destination, CommandSource.Script);
                break;
            case "Guard":
                foreach (var m in members)
                    m.AI?.Guard(m.Position, CommandSource.Script);
                break;
            case "Loop":
                team.Mission = argument - 1;
                team.NextMission = true;
                break;
        }
    }

    void RunTeamMission(Team team, TeamType type, List<GameObject> members)
    {
        if (team.Mission < 0 || team.Mission >= type.Missions.Count)
            return;
        var (mission, _) = type.Missions[team.Mission];
        bool timedOut = game.Frame >= team.TimeOutFrame;
        switch (mission)
        {
            case "Attack Base":
            case "Attack Units":
            case "Attack Civil.":
            case "Rampage":
            case "Attack Tarcom":
                AttackOrder(team, type, members, mission);
                if (timedOut)
                    team.NextMission = true;
                break;
            case "Move":
            case "Move to Cell":
                if (members.All(m => Vector2.Distance(new(m.Position.X, m.Position.Y), new(team.Destination.X, team.Destination.Y)) < Cell * 2.5f))
                    team.NextMission = true;
                break;
            case "Defend Base":
            case "Retreat":
            case "Unload":
            case "Guard":
                if (timedOut)
                    team.NextMission = true;
                break;
        }
    }

    static Vector3 Center(List<GameObject> members) =>
        members.Count == 0 ? Vector3.Zero : members.Aggregate(Vector3.Zero, (s, m) => s + m.Position) / members.Count;

    /// <summary>TeamClass::Attack_Order: a target of the kind the mission names (the nearest here), else the next order.</summary>
    void AttackOrder(Team team, TeamType type, List<GameObject> members, string mission)
    {
        if (game.FindObject(team.Target) is { IsEffectivelyDead: false })
            return;
        if (Player(type.House) is not { } owner)
            return;
        var center = Center(members);
        bool Wanted(GameObject o)
        {
            if (o.IsEffectivelyDead || o.Owner == owner || owner.RelationshipTo(o.Owner) != Relationship.Enemies || !o.IsKindOf("TD_OBJECT") || IsWall(o))
                return false;
            bool civilian = o.Owner.Name is "Neutral" or "Special";
            return mission switch
            {
                "Attack Base" => o.IsKindOf(Kind.Structure) && !civilian,
                "Attack Units" => !o.IsKindOf(Kind.Structure) && !civilian,
                "Attack Civil." => civilian,
                _ => true,
            };
        }
        var target = game.Objects.Where(Wanted).OrderBy(o => Vector3.DistanceSquared(o.Position, center)).FirstOrDefault();
        if (target == null)
        {
            team.NextMission = true;
            return;
        }
        team.Target = target.Id;
        foreach (var m in members)
            m.AI?.AttackObject(target, CommandSource.Script);
    }

    // ---- Reinforcements ----

    /// <summary>
    /// Do_Reinforcements: the team type's objects arrive, by air or the beach at waypoint 27 (TD's WAYPT_REINF), or
    /// from the house's map edge. A team with orders is made and set going at once. (The loaner transports, the
    /// Chinook, the hovercraft, the cargo plane, are left out: their passengers arrive where they would land.)
    /// </summary>
    bool Reinforce(TeamType type)
    {
        if (type.Classes.Count == 0 || Player(type.House) is not { } owner)
            return false;
        bool air = type.Classes.Any(c => c.Type is "TRAN" or "C17");
        bool beach = type.Classes.Any(c => c.Type == "LST");
        bool onlyTransports = type.Classes.All(c => c.Type is "TRAN" or "C17" or "LST");
        Team? team = type.Missions.Count > 0 ? new Team { Type = type.Name, ForcedActive = true } : null;

        Vector3 arrive;
        Vector3? inward = null;
        if (beach && BeachCell > 0)
            arrive = CellCenter(BeachCell) + new Vector3(0, Cell, 0);
        else if (air || beach)
            arrive = Waypoint(27) ?? CellCenter((Y + Height / 2) * MapWidth + X + Width / 2);
        else
        {
            var (edge, step) = EdgePoint(Houses.TryGetValue(type.House, out var h) ? h.Edge : "North");
            arrive = edge;
            inward = edge + step;
        }

        var made = new List<GameObject>();
        foreach (var (unitType, count) in type.Classes)
        {
            if (!onlyTransports && unitType is "TRAN" or "C17" or "LST" or "A10")
                continue;
            for (int i = 0; i < count; i++)
            {
                var at = arrive + new Vector3((made.Count % 3 - 1) * Cell * 0.6f, (made.Count / 3) * Cell * 0.6f * (inward is { } d && d.Y < arrive.Y ? 1 : -1), 0);
                if (Create(type.House, unitType, at, 0) is { } obj)
                    made.Add(obj);
            }
        }
        if (made.Count == 0)
            return false;
        if (team != null)
        {
            team.Members.AddRange(made.Select(m => m.Id));
            Teams.Add(team);
        }
        else if (inward is { } towards)
            foreach (var m in made)
                m.AI?.MoveTo(towards, CommandSource.Script);
        return true;
    }

    /// <summary>A point just inside the map on an edge (MapClass::Calculated_Cell), and a step further in.</summary>
    (Vector3 At, Vector3 Step) EdgePoint(string edge)
    {
        int along(int length) => game.Random.Int(1, Math.Max(1, length - 2));
        return edge switch
        {
            "South" => (CellCenter((Y + Height - 1) * MapWidth + X + along(Width)), new Vector3(0, Cell * 2, 0)),
            "East" => (CellCenter((Y + along(Height)) * MapWidth + X + Width - 1), new Vector3(-Cell * 2, 0, 0)),
            "West" => (CellCenter((Y + along(Height)) * MapWidth + X), new Vector3(Cell * 2, 0, 0)),
            _ => (CellCenter(Y * MapWidth + X + along(Width)), new Vector3(0, -Cell * 2, 0)),
        };
    }

    // ---- The computer's production (HouseClass::AI, once Production starts) ----

    void UpdateProduction()
    {
        if (game.Frame % LogicTime.FramesPerSecond != 0)
            return;
        foreach (var house in Houses.Values)
        {
            if (!house.Producing || Player(house.Name) is not { IsHuman: false } owner || game.Frame < house.NextProductionFrame)
                continue;
            var mine = Owned(house.Name).ToList();
            if (RebuildBase(house, owner, mine) || BuildForTeams(house, owner, mine))
                continue;
            // Nothing wanted: a new team, if one is due (Suggested_New_Team).
            if (SuggestedNewTeam(house) is { } next)
                CreateTeam(next, forced: false, byTrigger: false);
        }
    }

    /// <summary>BaseClass: the first building of the base list that isn't standing is rebuilt (with a Construction Yard).</summary>
    bool RebuildBase(HouseState house, Player owner, List<GameObject> mine)
    {
        if (!mine.Any(o => o.IsKindOf("COMMANDCENTER")))
            return false;
        foreach (var (type, at) in BaseList)
        {
            if (mine.Any(o => o.IsKindOf(Kind.Structure) && TdType(o).Equals(type, StringComparison.OrdinalIgnoreCase)
                    && Vector2.Distance(new(o.Position.X, o.Position.Y), new(at.X, at.Y)) < Cell))
                continue;
            if (TemplateFor(house.Name, type) is not { } name || game.Templates.Find(name) is not { } template)
                continue;
            // Something else on the spot: wait.
            if (game.ObjectsInRadius(at, Cell).Any(o => !o.IsEffectivelyDead && o.IsKindOf(Kind.Structure)))
                continue;
            if (!owner.Money.TryWithdraw(template.BuildCost))
                return true;
            Create(house.Name, type, at, 0);
            house.NextProductionFrame = game.Frame + (int)(Math.Max(2, template.BuildTimeSeconds) * LogicTime.FramesPerSecond);
            return true;
        }
        return false;
    }

    /// <summary>A unit a team of the house still needs, from a factory that makes its kind.</summary>
    bool BuildForTeams(HouseState house, Player owner, List<GameObject> mine)
    {
        foreach (var team in Teams.Where(t => FindTeamType(t.Type)?.House.Equals(house.Name, StringComparison.OrdinalIgnoreCase) == true && !t.Moving))
        {
            var type = FindTeamType(team.Type)!;
            foreach (var (unitType, count) in type.Classes)
            {
                int have = team.Members.Count(id => game.FindObject(id) is { } m && TdType(m).Equals(unitType, StringComparison.OrdinalIgnoreCase))
                    + mine.Count(o => !InATeam(o.Id) && TdType(o).Equals(unitType, StringComparison.OrdinalIgnoreCase));
                if (have >= count || TemplateFor(house.Name, unitType) is not { } name || game.Templates.Find(name) is not { } template)
                    continue;
                string factoryKind = template.Is("INFANTRY") ? "FS_BARRACKS" : template.Is("AIRCRAFT") ? "FS_AIRFIELD" : "FS_WARFACTORY";
                var factory = mine.FirstOrDefault(o => o.IsKindOf(factoryKind));
                if (factory == null)
                    continue;
                if (!owner.Money.TryWithdraw(template.BuildCost))
                    return true;
                // Out of the factory's bottom edge, as TD's exit.
                var at = factory.Position + new Vector3(0, -(factory.Geometry.MinorRadius + Cell), 0);
                Create(house.Name, unitType, at, 128);
                house.NextProductionFrame = game.Frame + (int)(Math.Max(2, template.BuildTimeSeconds) * LogicTime.FramesPerSecond);
                return true;
            }
        }
        return false;
    }

    /// <summary>TeamTypeClass::Suggested_New_Team: the house's highest-priority team type below its limit (autocreate ones only when alerted).</summary>
    TeamType? SuggestedNewTeam(HouseState house)
    {
        TeamType? best = null;
        foreach (var type in TeamTypes.Where(t => t.House.Equals(house.Name, StringComparison.OrdinalIgnoreCase)))
        {
            int limit = house.Alerted || !type.Autocreate ? type.MaxAllowed : 0;
            if (TeamCount(type.Name) < limit && (best == null || type.RecruitPriority > best.RecruitPriority))
                best = type;
        }
        return best;
    }
}

/// <summary>A classic INI file (sections of key=value lines, in order; ';' starts a comment).</summary>
public sealed class Ini
{
    readonly Dictionary<string, List<(string Key, string Value)>> sections = new(StringComparer.OrdinalIgnoreCase);

    public static Ini Parse(string text)
    {
        var ini = new Ini();
        List<(string, string)>? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimEnd('\r');
            int comment = line.IndexOf(';');
            if (comment >= 0)
                line = line[..comment].Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string name = line[1..^1].Trim();
                if (!ini.sections.TryGetValue(name, out current))
                    ini.sections[name] = current = new();
                continue;
            }
            if (current != null && line.IndexOf('=') is int eq and > 0)
                current.Add((line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return ini;
    }

    public IReadOnlyList<(string Key, string Value)> Entries(string section) =>
        sections.TryGetValue(section, out var list) ? list : [];

    public string Get(string section, string key, string fallback = "") =>
        Entries(section).LastOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } e ? e.Value : fallback;

    public int Int(string section, string key, int fallback = 0) => int.TryParse(Get(section, key), out int v) ? v : fallback;
}
