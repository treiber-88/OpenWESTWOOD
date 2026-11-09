using System.Globalization;
using System.Numerics;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;

namespace OpenWestwood.Logic.Missions;

/// <summary>
/// A Tiberian Dawn campaign mission run in the Zero Hour engine: the scenario's units and buildings, its triggers
/// (TRIGGER.CPP: an event and an action, on a cell, an object or a house), its teams (TEAM.CPP: recruited units
/// working through a mission list), reinforcements (Do_Reinforcements) and the computer houses' production and
/// base rebuilding (HouseClass::AI, BaseClass). Data comes from <see cref="Formats.Maps.MapFile.TdMission"/>.
/// Everything is polled each frame from plain fields, so the mission is deterministic and saves with the game.
/// </summary>
public sealed partial class TdMission
{
    // ---- TD's names (TriggerClass's EventText / ActionText, TeamTypeClass's TMissions) ----
    public const string EvPlayerEnters = "Player Enters", EvDiscovered = "Discovered", EvAttacked = "Attacked", EvDestroyed = "Destroyed",
        EvAny = "Any", EvHouseDiscovered = "House Discov.", EvUnitsDestroyed = "Units Destr.", EvBuildingsDestroyed = "Bldgs Destr.",
        EvAllDestroyed = "All Destr.", EvCredits = "Credits", EvTime = "Time", EvNBuildings = "# Bldgs Dstr.", EvNUnits = "# Units Dstr.",
        EvNoFactories = "No Factories", EvCivEvac = "Civ. Evac.", EvBuiltIt = "Built It";

    /// <summary>TD runs 15 ticks a second; a "Time" trigger counts tenths of a minute (TICKS_PER_MINUTE / 10).</summary>
    static int TenthOfAMinute => LogicTime.FramesPerSecond * 6;

    public sealed class Trigger
    {
        public string Name = "", Event = "", Action = "", House = "", Team = "";
        public int Data, DataCopy, Persist, AttachCount;
        public bool Alive = true;
    }

    public sealed class TeamType
    {
        public string Name = "", House = "";
        public bool RoundAbout, Learning, Suicide, Autocreate, Mercenary, Reinforcable = true, Prebuilt = true;
        public int RecruitPriority, MaxAllowed, InitNum, Fear;
        public List<(string Type, int Count)> Classes = new();
        public List<(string Mission, int Argument)> Missions = new();
    }

    public sealed class Team
    {
        public string Type = "";
        public List<uint> Members = new();
        public int Mission = -1;
        public bool Moving, NextMission = true, HasBeen, ForcedActive;
        public int TimeOutFrame;
        public uint Target;
        public Vector3 Destination;
        public bool Arrived;
    }

    public sealed class HouseState
    {
        public string Name = "";
        public string Edge = "North";
        public bool Alerted, Producing, Discovered, DiscoveredSprung;
        public int BuildingsLost, UnitsLost;
        public int NextProductionFrame;
        public HashSet<uint> Known = new();
    }

    readonly GameLogic game;

    /// <summary>Debug: log each trigger that fires.</summary>
    public static bool Trace;
    public int X, Y, Width, Height, MapWidth;
    public float Cell;
    public string PlayerHouse = "GoodGuy";
    public List<Trigger> Triggers = new();
    public Dictionary<int, string> CellTriggers = new();
    public Dictionary<uint, string> ObjectTriggers = new();
    public Dictionary<uint, int> LastDamageSeen = new();
    public HashSet<uint> SeenByPlayer = new();
    public List<TeamType> TeamTypes = new();
    public List<Team> Teams = new();
    public Dictionary<int, int> Waypoints = new();
    public Dictionary<string, HouseState> Houses = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Type, Vector3 At)> BaseList = new();
    public int NextTimeTick;
    public bool Ended;
    public int WinFrame = -1, LoseFrame = -1;

    public TdMission(GameLogic game, string text)
    {
        this.game = game;
        var ini = Ini.Parse(text);
        X = ini.Int("OW_Geometry", "X");
        Y = ini.Int("OW_Geometry", "Y");
        Width = ini.Int("OW_Geometry", "Width");
        Height = ini.Int("OW_Geometry", "Height");
        MapWidth = ini.Int("OW_Geometry", "MapWidth", 64);
        Cell = float.Parse(ini.Get("OW_Geometry", "Cell", "32.4"), CultureInfo.InvariantCulture);
        PlayerHouse = ini.Get("BASIC", "Player", "GoodGuy");
        BeachCell = ini.Int("OW_Geometry", "Beach");

        foreach (string house in new[] { "GoodGuy", "BadGuy", "Neutral", "Special" })
        {
            var state = new HouseState { Name = house, Edge = ini.Get(house, "Edge", "North") };
            Houses[house] = state;
            if (Player(house) is { } p)
                p.Money.Deposit(ini.Int(house, "Credits") * 100);
        }

        foreach (var (key, value) in ini.Entries("Waypoints"))
            if (int.TryParse(key, out int w) && int.TryParse(value, out int cell) && cell >= 0)
                Waypoints[w] = cell;

        foreach (var (key, value) in ini.Entries("TeamTypes"))
            TeamTypes.Add(ParseTeamType(key, value));

        foreach (var (key, value) in ini.Entries("Triggers"))
        {
            var f = value.Split(',');
            var t = new Trigger
            {
                Name = key.Length > 4 ? key[..4] : key,
                Event = f.ElementAtOrDefault(0)?.Trim() ?? "",
                Action = f.ElementAtOrDefault(1)?.Trim() ?? "",
                Data = Atoi(f.ElementAtOrDefault(2)),
                House = f.ElementAtOrDefault(3)?.Trim() ?? "",
                Team = f.ElementAtOrDefault(4)?.Trim() ?? "",
                Persist = Atoi(f.ElementAtOrDefault(5)),
            };
            t.DataCopy = t.Data;
            // A "Player Enters" trigger with no house is the player's (TriggerClass::Fill_In).
            if (t.House.Equals("None", StringComparison.OrdinalIgnoreCase) || t.House.Length == 0)
                t.House = t.Event == EvPlayerEnters ? PlayerHouse : "";
            if (t.House.Length > 0)
                t.AttachCount++;
            Triggers.Add(t);
        }

        foreach (var (key, value) in ini.Entries("CellTriggers"))
            if (int.TryParse(key, out int cell) && FindTrigger(value) is { } t)
            {
                CellTriggers[cell] = t.Name;
                t.AttachCount++;
            }

        foreach (var (_, value) in ini.Entries("OW_Base"))
            if (value.Split(',') is [var type, var x, var y])
                BaseList.Add((type, new Vector3(float.Parse(x, CultureInfo.InvariantCulture), float.Parse(y, CultureInfo.InvariantCulture), 0)));

        PlaceObjects(ini);
        foreach (var house in Houses.Values)
            foreach (var obj in Owned(house.Name))
                house.Known.Add(obj.Id);
        NextTimeTick = game.Frame + TenthOfAMinute;
        EndCountDownFrame = game.Frame + LogicTime.FramesPerSecond * 30;
    }

    public int EndCountDownFrame;

    /// <summary>Where the hovercraft lands (MapClass::Beach_Cell, worked out by the loader; 0: none).</summary>
    public int BeachCell;

    static int Atoi(string? s) => int.TryParse(s?.Trim(), out int v) ? v : 0;

    static TeamType ParseTeamType(string name, string value)
    {
        var f = value.Split(',').Select(x => x.Trim()).ToList();
        int i = 0;
        string Next() => i < f.Count ? f[i++] : "";
        var t = new TeamType
        {
            Name = name, House = Next(), RoundAbout = Atoi(Next()) != 0, Learning = Atoi(Next()) != 0, Suicide = Atoi(Next()) != 0,
            Autocreate = Atoi(Next()) != 0, Mercenary = Atoi(Next()) != 0, RecruitPriority = Atoi(Next()), MaxAllowed = Atoi(Next()),
            InitNum = Atoi(Next()), Fear = Atoi(Next()),
        };
        int classes = Atoi(Next());
        for (int c = 0; c < classes; c++)
            if (Next().Split(':') is [var type, var count])
                t.Classes.Add((type, Atoi(count)));
        int missions = Atoi(Next());
        for (int m = 0; m < missions; m++)
            if (Next().Split(':') is [var mission, var arg])
                t.Missions.Add((mission, Atoi(arg)));
        if (i < f.Count)
            t.Reinforcable = Atoi(Next()) != 0;
        if (i < f.Count)
            t.Prebuilt = Atoi(Next()) != 0;
        return t;
    }

    Trigger? FindTrigger(string name)
    {
        string key = name.Length > 4 ? name[..4] : name;
        return Triggers.FirstOrDefault(t => t.Alive && t.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    TeamType? FindTeamType(string name) => TeamTypes.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    Player? Player(string house) => house.Length == 0 ? null : game.Players.FindByNameKey(house);

    IEnumerable<GameObject> Owned(string house) =>
        Player(house) is { } p ? game.Objects.Where(o => o.Owner == p && !o.IsEffectivelyDead && !o.IsDestroyed && o.IsKindOf("TD_OBJECT")) : [];

    // ---- Cells ----

    /// <summary>The middle of a TD cell (TdMaps.CellCenter).</summary>
    public Vector3 CellCenter(int cell)
    {
        int x = cell % MapWidth - X, y = cell / MapWidth - Y;
        var at = new Vector3((x + 0.5f) * Cell, (Height - y - 0.5f) * Cell, 0);
        return at with { Z = game.Terrain.GroundHeight(at) };
    }

    public int CellOf(Vector3 position)
    {
        int x = (int)MathF.Floor(position.X / Cell), y = Height - 1 - (int)MathF.Floor(position.Y / Cell);
        return (Math.Clamp(y, 0, Height - 1) + Y) * MapWidth + Math.Clamp(x, 0, Width - 1) + X;
    }

    Vector3? Waypoint(int index) => Waypoints.TryGetValue(index, out int cell) ? CellCenter(cell) : null;

    // ---- The scenario's objects ----

    /// <summary>The Zero Hour engine template for a TD type owned by a house: its own side's first.</summary>
    string? TemplateFor(string house, string type)
    {
        string[] prefixes = house switch
        {
            "GoodGuy" => ["TDGDI_", "TDNOD_", "TDCIV_"],
            "BadGuy" => ["TDNOD_", "TDGDI_", "TDCIV_"],
            _ => ["TDCIV_", "TDGDI_", "TDNOD_"],
        };
        foreach (string prefix in prefixes)
            if (game.Templates.Find(prefix + type.ToUpperInvariant()) != null)
                return prefix + type.ToUpperInvariant();
        return null;
    }

    void PlaceObjects(Ini ini)
    {
        foreach (var (_, value) in ini.Entries("OW_Objects"))
        {
            var f = value.Split(',');
            if (f.Length < 9)
                continue;
            string house = f[1], type = f[2];
            var at = new Vector3(float.Parse(f[3], CultureInfo.InvariantCulture), float.Parse(f[4], CultureInfo.InvariantCulture), 0);
            int facing = Atoi(f[5]), strength = Atoi(f[6]);
            string mission = f[7], trigger = f[8];
            var obj = Create(house, type, at, facing);
            if (obj == null)
                continue;
            if (strength is > 0 and < 256 && obj.Body != null)
                obj.Body.SetHealth(obj.MaxHealth * strength / 256f);
            if (!trigger.Equals("None", StringComparison.OrdinalIgnoreCase) && FindTrigger(trigger) is { } t)
            {
                if (Trace)
                    Log.Info($"[td] placed {obj.Template.Name} #{obj.Id} at {obj.Position} with trigger {t.Name}");
                ObjectTriggers[obj.Id] = t.Name;
                t.AttachCount++;
            }
            StartMission(obj, mission);
        }
    }

    GameObject? Create(string house, string type, Vector3 at, int facing)
    {
        if (Player(house) is not { } owner || TemplateFor(house, type) is not { } name)
            return null;
        // TD facing: 0 north, clockwise, 256 a turn; the map's north is +Y.
        float angle = MathF.PI / 2 - facing / 256f * MathF.Tau;
        at = at with { Z = game.Terrain.GroundHeight(at) };
        return game.CreateObject(name, owner, at, angle);
    }

    /// <summary>A placed object's starting mission (Guard, Area Guard, Hunt, Sleep...).</summary>
    void StartMission(GameObject obj, string mission)
    {
        if (obj.AI is not { } ai)
            return;
        switch (mission)
        {
            case "Hunt":
                ai.Hunt(CommandSource.Script);
                break;
            case "Sleep":
                ai.Attitude = Attitude.Passive;
                break;
            case "Area Guard":
            case "Guard":
                if (!obj.IsKindOf(Kind.Structure))
                    ai.Guard(obj.Position, CommandSource.Script);
                break;
        }
    }

    // ---- Each frame ----

    public void Update()
    {
        if (Ended)
            return;
        CheckObjectTriggers();
        CheckCellTriggers();
        CheckHouses();
        UpdateTeams();
        UpdateProduction();
        if (WinFrame >= 0 && game.Frame >= WinFrame)
            End(victory: true);
        else if (LoseFrame >= 0 && game.Frame >= LoseFrame)
            End(victory: false);
    }

    void End(bool victory)
    {
        Ended = true;
        game.Events.ScriptClientAction(new Scripting.ScriptAction { Name = "DISABLE_INPUT" });
        game.Events.ScriptClientAction(new Scripting.ScriptAction { Name = victory ? "VICTORY" : "DEFEAT" });
        game.Scripts?.EndGame(victory, game.Players.Local);
        game.Scripts?.StartEndGameTimer();
    }

    /// <summary>Destroyed, Attacked, Discovered (and Any) triggers on objects.</summary>
    void CheckObjectTriggers()
    {
        var local = game.Players.Local;
        foreach (var (id, triggerName) in ObjectTriggers.ToList())
        {
            var obj = game.FindObject(id);
            var t = FindTrigger(triggerName);
            if (t == null)
            {
                ObjectTriggers.Remove(id);
                continue;
            }
            if (obj == null || obj.IsEffectivelyDead)
            {
                if (Trace)
                    Log.Info($"[td] frame {game.Frame}: {obj?.Template.Name ?? "object #" + id} at {obj?.Position} died (health {obj?.Health}, last hit by {(obj?.Body is { } b ? game.FindObject(b.LastDamageSourceId)?.Template.Name : null)})");
                ObjectTriggers.Remove(id);
                Spring(t, EvDestroyed, objectTrigger: true);
                continue;
            }
            if (obj.Body is { } body && body.LastDamageFrame >= 0 && LastDamageSeen.GetValueOrDefault(id, -1000) != body.LastDamageFrame)
            {
                LastDamageSeen[id] = body.LastDamageFrame;
                if (body.LastDamageFrame > 0 && Spring(t, EvAttacked, objectTrigger: true) && !t.Alive)
                    continue;
            }
            if (local != null && obj.Owner != local && !SeenByPlayer.Contains(id) && game.Shroud.IsVisibleTo(obj, local))
            {
                SeenByPlayer.Add(id);
                Spring(t, EvDiscovered, objectTrigger: true);
            }
        }
    }

    /// <summary>"Player Enters" on cells: a unit of the trigger's house (not cloaked) in the cell (FootClass::Per_Cell_Process).</summary>
    void CheckCellTriggers()
    {
        if (CellTriggers.Count == 0)
            return;
        // (A copy: a trigger may bring reinforcements.)
        foreach (var obj in game.Objects.ToList())
        {
            if (obj.IsEffectivelyDead || obj.IsKindOf(Kind.Structure) || obj.IsKindOf("AIRCRAFT") || obj.HasStatus("STEALTHED") || obj.ContainedById != 0)
                continue;
            if (!CellTriggers.TryGetValue(CellOf(obj.Position), out string? name) || FindTrigger(name) is not { } t)
                continue;
            if (!obj.Owner.Name.Equals(t.House, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Spring(t, EvPlayerEnters, objectTrigger: false, cell: CellOf(obj.Position)) && !t.Alive)
                continue;
        }
    }

    /// <summary>HouseClass::AI's trigger checks, and the "Time" ticks every tenth of a minute.</summary>
    void CheckHouses()
    {
        bool isTime = game.Frame >= NextTimeTick;
        if (isTime)
            NextTimeTick = game.Frame + TenthOfAMinute;
        var local = game.Players.Local;
        foreach (var house in Houses.Values)
        {
            var mine = Owned(house.Name).ToList();
            // Losses since the last look.
            var alive = mine.Select(o => o.Id).ToHashSet();
            foreach (uint id in house.Known.ToList())
                if (!alive.Contains(id))
                {
                    house.Known.Remove(id);
                    if (WasBuilding.Contains(id))
                        house.BuildingsLost++;
                    else
                        house.UnitsLost++;
                }
            foreach (var o in mine)
                if (house.Known.Add(o.Id) && o.IsKindOf(Kind.Structure))
                    WasBuilding.Add(o.Id);
                else if (o.IsKindOf(Kind.Structure))
                    WasBuilding.Add(o.Id);
            if (!house.Discovered && local != null && Player(house.Name) != local && mine.Any(o => game.Shroud.IsVisibleTo(o, local)))
                house.Discovered = true;

            int buildings = mine.Count(o => o.IsKindOf(Kind.Structure) && !IsWall(o));
            // (TD leaves the gunboat and the transport, cargo and A-10 aircraft out of "units".)
            int units = mine.Count(o => !o.IsKindOf(Kind.Structure) && !IsLoaner(o));
            bool factories = mine.Any(o => o.IsKindOf("FS_FACTORY") && !o.IsKindOf("COMMANDCENTER"));
            var money = Player(house.Name)?.Money.Amount ?? 0;

            foreach (var t in Triggers.Where(t => t.Alive && t.House.Equals(house.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (!t.Alive)
                    continue;
                if (t.Event == EvNBuildings && house.BuildingsLost >= t.Data && Spring(t, t.Event))
                    continue;
                if (t.Event == EvNUnits && house.UnitsLost >= t.Data && Spring(t, t.Event))
                    continue;
                if (t.Event == EvHouseDiscovered && house.Discovered && !house.DiscoveredSprung && Spring(t, t.Event))
                {
                    house.DiscoveredSprung = true;
                    continue;
                }
                // (Clear_Scenario's EndCountDown: nobody counts as wiped out in the first 30 seconds, while the opening reinforcements come.)
                if (WinFrame < 0 && LoseFrame < 0 && game.Frame >= EndCountDownFrame)
                {
                    if (t.Event == EvBuildingsDestroyed && buildings == 0 && Spring(t, t.Event))
                        continue;
                    if (t.Event == EvUnitsDestroyed && units == 0 && Spring(t, t.Event))
                        continue;
                    if (t.Event == EvAllDestroyed && buildings == 0 && units == 0 && Spring(t, t.Event))
                        continue;
                }
                if (t.Event == EvCredits && money >= t.Data && Spring(t, t.Event))
                    continue;
                if (t.Event == EvTime && isTime && Spring(t, t.Event))
                    continue;
                if (t.Event == EvNoFactories && !factories && Spring(t, t.Event))
                    continue;
            }
        }
    }

    public HashSet<uint> WasBuilding = new();

    static bool IsWall(GameObject o) => o.IsKindOf("WALK_ON_TOP_OF_WALL");

    static bool IsLoaner(GameObject o) =>
        o.Template.Name.EndsWith("_BOAT") || o.Template.Name.EndsWith("_TRAN") || o.Template.Name.EndsWith("_C17") || o.Template.Name.EndsWith("_A10");

    // ---- Springing a trigger (TriggerClass::Spring) ----

    bool Spring(Trigger t, string e, bool objectTrigger = false, int cell = -1)
    {
        if (t.Event != e && t.Event != EvAny)
            return false;
        // A time trigger counts down its tenths of a minute.
        if (t.Event == EvTime)
        {
            t.Data--;
            if (t.Data > 0)
                return false;
            t.Data = t.DataCopy;
        }
        // Semi-persistent: fires when its last attached object or cell has.
        if (t.Persist == 1 && (objectTrigger || cell >= 0))
        {
            if (cell >= 0)
                CellTriggers.Remove(cell);
            t.AttachCount--;
            if (t.AttachCount > 0)
                return false;
            t.Persist = 0;
        }
        bool success = DoAction(t, e);
        if (Trace)
            Log.Info($"[td] frame {game.Frame}: trigger {t.Name} ({t.Event} -> {t.Action} {t.Team}) {(success ? "" : "failed")}");
        if (!success && t.Event == EvTime)
            t.Data = 1;
        if (success && t.Persist == 0)
            Remove(t);
        return true;
    }

    void Remove(Trigger t)
    {
        t.Alive = false;
        foreach (var cell in CellTriggers.Where(kv => kv.Value.Equals(t.Name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            CellTriggers.Remove(cell);
        foreach (var id in ObjectTriggers.Where(kv => kv.Value.Equals(t.Name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
            ObjectTriggers.Remove(id);
    }

    bool DoAction(Trigger t, string e)
    {
        switch (t.Action)
        {
            case "Win":
                FlagToWin();
                return true;
            case "Lose":
                FlagToLose();
                return true;
            case "Cap=Win/Des=Lose":
                if (e == EvDestroyed)
                    FlagToLose();
                else if (e == EvPlayerEnters)
                    FlagToWin();
                else
                    return false;
                return true;
            case "Production":
                if (Houses.TryGetValue(t.House.Length > 0 ? t.House : OtherHouse(PlayerHouse), out var producer))
                    producer.Producing = true;
                return true;
            case "Autocreate":
                foreach (var h in Houses.Values.Where(h => t.House.Length == 0 || h.Name.Equals(t.House, StringComparison.OrdinalIgnoreCase)))
                    h.Alerted = true;
                return true;
            case "Create Team":
                if (FindTeamType(t.Team) is { } createType)
                    CreateTeam(createType, forced: false);
                return true;
            case "Dstry Teams":
                Teams.RemoveAll(team => team.Type.Equals(t.Team, StringComparison.OrdinalIgnoreCase));
                return true;
            case "Reinforce.":
                return FindTeamType(t.Team) is { } reinforcements && Reinforce(reinforcements);
            case "All to Hunt":
                AllToHunt();
                return true;
            case "Dstry Trig 'XXXX'":
                if (FindTrigger("XXXX") is { } x)
                    Remove(x);
                return true;
            case "Dstry Trig 'YYYY'":
                if (FindTrigger("YYYY") is { } y)
                    Remove(y);
                return true;
            case "Dstry Trig 'ZZZZ'":
                if (FindTrigger("ZZZZ") is { } z)
                    Remove(z);
                return true;
            case "DZ at 'Z'":
                if (Waypoint(25) is { } dz && game.Players.Local is { } local)
                    game.Events.RadarEvent("INFORMATION", dz, local);
                return true;
            case "Nuclear Missile":
                LaunchNuke();
                return true;
            case "Ion Cannon":
            case "Airstrike":
            case "Allow Win":
            case "None":
            default:
                return true;
        }
    }

    static string OtherHouse(string house) => house.Equals("GoodGuy", StringComparison.OrdinalIgnoreCase) ? "BadGuy" : "GoodGuy";

    /// <summary>HouseClass::Flag_To_Win / Flag_To_Lose: the end comes a moment later, as in TD.</summary>
    void FlagToWin()
    {
        if (WinFrame < 0 && LoseFrame < 0)
            WinFrame = game.Frame + LogicTime.FramesPerSecond * 3;
    }

    void FlagToLose()
    {
        if (LoseFrame < 0 && WinFrame < 0)
            LoseFrame = game.Frame + LogicTime.FramesPerSecond * 3;
    }

    void AllToHunt()
    {
        foreach (var obj in game.Objects.ToList())
            if (!obj.IsEffectivelyDead && !obj.Owner.IsHuman && !obj.IsKindOf(Kind.Structure) && obj.AI is { } ai && obj.IsKindOf("TD_OBJECT"))
            {
                foreach (var team in Teams)
                    team.Members.Remove(obj.Id);
                ai.Hunt(CommandSource.Script);
            }
    }

    /// <summary>Nod's nuke, charged at once (Superweapon_Action), fired at the player's base.</summary>
    void LaunchNuke()
    {
        var local = game.Players.Local;
        if (local == null)
            return;
        var target = game.ObjectsOwnedBy(local).Where(o => o.IsKindOf(Kind.Structure)).OrderByDescending(o => o.IsKindOf("COMMANDCENTER")).FirstOrDefault()
            ?? game.ObjectsOwnedBy(local).FirstOrDefault();
        // The blast is Nod's: fired from one of Nod's buildings (the Temple if it stands).
        var source = Owned("BadGuy").OrderByDescending(o => o.Template.Name.EndsWith("_TMPL")).ThenByDescending(o => o.IsKindOf(Kind.Structure)).FirstOrDefault();
        if (target == null || source == null)
            return;
        game.ObjectCreation.Run("OCL_TD_Nuke", source, target.Position);
    }
}
