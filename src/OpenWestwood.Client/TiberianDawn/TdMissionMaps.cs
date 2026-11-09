using System.Globalization;
using System.Numerics;
using System.Text;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.TiberianDawn;
using OpenWestwood.TiberianDawn.Data;
using OpenWestwood.TiberianDawn.Logic;
using OpenWestwood.TiberianDawn.Logic.Types;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// Tiberian Dawn's campaign missions (SCG01EA...: "td:SCG01EA") in the Zero Hour engine. The map is made as for a
/// multiplayer map; on top, the scenario's houses become the map's players (GoodGuy GDI, BadGuy Nod, Neutral and
/// Special civilians) and the scenario goes to the game logic's TdMission (<see cref="MapFile.TdMission"/>), which
/// places its units and buildings and runs its triggers and teams. The loader adds what needs TD's own tables:
/// [OW_Geometry] (the cell layout) and [OW_Objects] / [OW_Base] (world positions of the units, buildings and
/// the base list, from each building's occupied cells).
/// </summary>
public static partial class TdMaps
{
    /// <summary>A campaign scenario (not a multiplayer one): GDI's, Nod's or the dinosaur missions.</summary>
    public static bool IsMission(string scenarioName) =>
        scenarioName.Length >= 3 && scenarioName[..3].ToUpperInvariant() is "SCG" or "SCB" or "SCJ";

    static readonly string[] Houses = ["GoodGuy", "BadGuy", "Neutral", "Special"];

    /// <summary>
    /// MapClass::Beach_Cell: where the hovercraft lands on the south coast (the land cell above a column of water
    /// at the map's bottom, from a run of such cells; one row further down in the desert). 0: none.
    /// </summary>
    static int BeachCell(TdScenario s, TdTypes types)
    {
        var templates = types.Templates.GroupBy(t => (int)t.Type).ToDictionary(g => g.Key, g => g.First());
        var trees = s.Terrain.Select(t => t.Cell).ToHashSet();
        LandType Land(int cell)
        {
            if (cell < 0 || cell >= s.Cells.Length)
                return LandType.LAND_ROCK;
            var (template, icon) = s.Cells[cell];
            return s.IsEmpty(template) ? LandType.LAND_CLEAR : templates.TryGetValue(template, out var t) ? t.LandOf(icon) : LandType.LAND_CLEAR;
        }
        int mcw = s.MapWidth;
        int Xy(int x, int y) => y * mcw + x;
        var found = new List<int>();
        for (int x = 0; x < s.Width; x++)
        {
            if (Land(Xy(x + s.X, s.Height + s.Y)) != LandType.LAND_WATER || Land(Xy(x + s.X, s.Height + s.Y - 1)) != LandType.LAND_WATER)
                continue;
            int cell = 0;
            for (int y = s.Height; y >= 0; y--)
            {
                cell = Xy(x + s.X, y + s.Y);
                if (Land(cell) != LandType.LAND_WATER)
                    break;
            }
            var land = Land(cell);
            if (land is LandType.LAND_BEACH or LandType.LAND_CLEAR or LandType.LAND_ROAD
                && !trees.Contains(cell) && !trees.Contains(cell - mcw) && !trees.Contains(cell - mcw * 2))
            {
                found.Add(cell);
                if (found.Count >= mcw)
                    break;
            }
        }
        var alternate = new List<int>();
        for (int i = 1; i < found.Count - 1; i++)
            if (found[i - 1] % mcw + 1 == found[i] % mcw && found[i + 1] % mcw - 1 == found[i] % mcw)
                alternate.Add(found[i]);
        int beach = alternate.Count != 0 ? (alternate.Count < 4 ? alternate[^1] : alternate[alternate.Count - alternate.Count / 4])
            : found.Count != 0 ? (found.Count < 4 ? found[^1] : found[found.Count - found.Count / 4]) : 0;
        if (beach != 0 && s.Theater == "DESERT")
            beach += mcw;
        return beach;
    }

    static void AddMission(MapFile map, TdContent content, TdScenario s)
    {
        string iniName = s.Name + ".INI";
        if (!content.Files.Exists(iniName))
            return;
        var types = new TdTypes(content.Rules);
        var ini = s.Ini;
        string player = ini.Get("BASIC", "Player") is { Length: > 0 } p ? p : "GoodGuy";
        var inv = CultureInfo.InvariantCulture;
        string F(float v) => v.ToString("0.##", inv);

        // ---- The players: a neutral side first (Zero Hour's), then the scenario's houses ----
        map.Sides.Sides.Add(new MapSide { Properties = new(StringComparer.OrdinalIgnoreCase) { ["playerName"] = "", ["playerIsHuman"] = false, ["playerDisplayName"] = "Neutral", ["playerFaction"] = "" } });
        var allies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string house in Houses)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { house };
            foreach (string a in (ini.Get(house, "Allies") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                set.Add(a);
            // HouseClass::Read_INI: GDI without an ally list is the civilians' ally.
            if (house == "GoodGuy" && (ini.Get(house, "Allies") ?? "").Length == 0)
                set.Add("Neutral");
            allies[house] = set;
        }
        foreach (string house in Houses)
        {
            string faction = house switch { "GoodGuy" => "FactionTDGDI", "BadGuy" => "FactionTDNod", _ => "FactionCivilian" };
            // Allied both ways (Zero Hour's relations are one-sided; TD's Is_Ally is checked from either side).
            var friends = Houses.Where(h => allies[house].Contains(h) || allies[h].Contains(house)).ToList();
            var enemies = Houses.Where(h => !friends.Contains(h)).ToList();
            map.Sides.Sides.Add(new MapSide
            {
                Properties = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["playerName"] = house, ["playerDisplayName"] = house switch { "GoodGuy" => "GDI", "BadGuy" => "Nod", _ => house },
                    ["playerFaction"] = faction, ["playerIsHuman"] = house.Equals(player, StringComparison.OrdinalIgnoreCase),
                    ["playerAllies"] = string.Join(' ', friends.Where(f => f != house)), ["playerEnemies"] = string.Join(' ', enemies),
                },
            });
        }

        // ---- What the logic can't work out itself ----
        var text = new StringBuilder(content.Files.ReadAllText(iniName));
        float c = TdScale.Cell;
        text.Append($"\n[OW_Geometry]\nX={s.X}\nY={s.Y}\nWidth={s.Width}\nHeight={s.Height}\nMapWidth={s.MapWidth}\nCell={F(c)}\nBeach={BeachCell(s, types)}\n");

        var buildings = types.Buildings.ToDictionary(b => b.IniName, StringComparer.OrdinalIgnoreCase);
        Vector3 BuildingCenter(string type, int cell)
        {
            if (!buildings.TryGetValue(type, out var b))
                return CellCenter(s, cell);
            var cells = new List<int>();
            foreach (short offset in b.Occupy_List(types, types.Special))
            {
                if (offset == TdMath.RefreshEol)
                    break;
                cells.Add(cell + offset);
            }
            if (cells.Count == 0)
                return CellCenter(s, cell);
            var sum = Vector3.Zero;
            foreach (int oc in cells)
                sum += CellCenter(s, oc);
            return sum / cells.Count;
        }

        // Where a soldier stands in its cell (TD's five spots: the middle and the four quarters).
        Vector3 Spot(int subCell) => subCell switch
        {
            1 => new Vector3(-c / 4, c / 4, 0),
            2 => new Vector3(c / 4, c / 4, 0),
            3 => new Vector3(-c / 4, -c / 4, 0),
            4 => new Vector3(c / 4, -c / 4, 0),
            _ => Vector3.Zero,
        };

        text.Append("\n[OW_Objects]\n");
        int n = 0;
        void Line(string kind, TdScenario.PlacedObject o, Vector3 at) =>
            text.Append($"{n++}={kind},{o.House},{o.Type},{F(at.X)},{F(at.Y)},{o.Facing},{o.Strength},{o.Mission},{o.Trigger}\n");
        foreach (var o in s.Structures)
            Line("Building", o, BuildingCenter(o.Type, o.Cell));
        foreach (var o in s.Units)
            Line("Unit", o, CellCenter(s, o.Cell));
        foreach (var o in s.Infantry)
            Line("Infantry", o, CellCenter(s, o.Cell) + Spot(o.SubCell));
        foreach (var o in s.Aircraft)
            Line("Aircraft", o, CellCenter(s, o.Cell));

        // The computer's base list (BaseClass): its buildings and where they stand, to rebuild them.
        text.Append("\n[OW_Base]\n");
        int count = int.TryParse(ini.Get("Base", "Count"), out int bc) ? bc : 0;
        for (int i = 0; i < count; i++)
        {
            if (ini.Get("Base", i.ToString("000")) is not { } entry || entry.Split(',') is not [var type, var coordText] || !uint.TryParse(coordText, out uint coord))
                continue;
            // A COORDINATE: x in the low word, y in the high word, 256 leptons a cell.
            int cell = (int)((coord >> 16) >> 8) * s.MapWidth + (int)((coord & 0xFFFF) >> 8);
            var at = BuildingCenter(type, cell);
            text.Append($"{i}={type},{F(at.X)},{F(at.Y)}\n");
        }
        map.TdMission = text.ToString();
    }
}
