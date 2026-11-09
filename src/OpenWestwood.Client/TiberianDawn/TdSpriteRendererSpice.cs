using System.Numerics;
using OpenWestwood.Client.Effects;
using OpenWestwood.Logic.Modules.Behaviors;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// Dune 2000 spice lying on the ground: every spice cell of every pile, drawn flat on the terrain with the
/// edge frame its neighbours call for (OpenRA's D2kResourceRenderer: the sides that have no spice next to them
/// pick one of the BLOXBASE edge tiles, so fields have round borders). A pile loses cells as it is harvested.
/// </summary>
public sealed partial class TdSpriteRenderer
{
    readonly HashSet<GameObject> spicePiles = new();
    readonly Dictionary<GameObject, (int I, int J, bool Rich)[]> spiceCells = new();

    [Flags]
    enum ClearSides : byte
    {
        None = 0, Left = 0x1, Top = 0x2, Right = 0x4, Bottom = 0x8,
        TopLeft = 0x10, TopRight = 0x20, BottomLeft = 0x40, BottomRight = 0x80,
        All = 0xFF,
    }

    const ClearSides L = ClearSides.Left, T = ClearSides.Top, R = ClearSides.Right, B = ClearSides.Bottom,
        TL = ClearSides.TopLeft, TR = ClearSides.TopRight, BL = ClearSides.BottomLeft, BR = ClearSides.BottomRight;

    /// <summary>D2kResourceRenderer.SpriteMap: clear sides to the spice sequence's frame.</summary>
    static readonly Dictionary<ClearSides, int> SpiceSpriteMap = new()
    {
        { L | T | TL | TR | BL | BR, 2 }, { T | R | TL | TR | BL | BR, 3 }, { L | B | TL | TR | BL | BR, 4 }, { R | B | TL | TR | BL | BR, 5 },
        { L | TL | TR | BL | BR, 6 }, { R | TL | TR | BL | BR, 7 }, { T | TL | TR | BL | BR, 8 }, { B | TL | TR | BL | BR, 9 },
        { L | T | TL | TR | BL, 10 }, { T | R | TL | TR | BR, 11 }, { L | B | TL | BL | BR, 12 }, { R | B | TR | BL | BR, 13 },
        { L | T | R | TL | TR | BL | BR, 14 }, { L | R | B | TL | TR | BL | BR, 15 }, { L | T | B | TL | TR | BL | BR, 16 }, { T | R | B | TL | TR | BL | BR, 17 },
        { T | TL | TR, 18 }, { R | TR | BR, 19 }, { L | TL | BL, 20 }, { B | BL | BR, 21 },
        { TL, 22 }, { TR, 23 }, { BL, 24 }, { BR, 25 },
        { L | TL | BL | BR, 26 }, { R | TR | BL | BR, 27 }, { T | TL | TR | BR, 28 }, { T | TL | TR | BL, 29 },
        { TR | BL | BR, 30 }, { TL | BL | BR, 31 }, { TL | TR | BR, 32 }, { TL | TR | BL, 33 },
        { TR | BR, 34 }, { TL | TR, 35 }, { TR | BL, 36 }, { TL | BL, 37 }, { BL | BR, 38 }, { TL | BR, 39 },
        { TL | TR | BL | BR, 40 }, { L | R | TL | TR | BL | BR, 41 }, { T | B | TL | TR | BL | BR, 42 }, { ClearSides.All, 44 },
        { L | TL | TR | BL, 46 }, { R | TL | TR | BR, 47 }, { B | TR | BL | BR, 48 }, { B | TL | BL | BR, 49 },
    };

    static readonly string[] SpiceLooks = ["spicea", "spiceb", "spicec", "spiced"];

    (int I, int J, bool Rich)[] CellsOf(GameObject pile)
    {
        if (spiceCells.TryGetValue(pile, out var cells))
            return cells;
        cells = pile.MapData.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Split(',')).Where(p => p.Length >= 2)
            .Select(p => (int.Parse(p[0]), int.Parse(p[1]), p.Length > 2 && p[2] == "1")).ToArray();
        // Harvested from the outside in: the cells furthest from the pile's middle go first.
        var middle = new Vector2((float)cells.Average(c => c.Item1), (float)cells.Average(c => c.Item2));
        cells = cells.OrderBy(c => Vector2.DistanceSquared(new(c.Item1, c.Item2), middle)).ToArray();
        return spiceCells[pile] = cells;
    }

    /// <summary>Draws the spice cells flat on the terrain (called by the effects' ground pass).</summary>
    public void RenderSpice(EffectsRenderer renderer, Logic.GameLogic game)
    {
        RenderRa2Ore(renderer, game);
        if (d2k == null || spicePiles.Count == 0)
            return;
        spicePiles.RemoveWhere(p => p.IsDestroyed);
        // The live cells: each pile keeps a share of its cells as large as its share of spice left.
        var live = new Dictionary<(int, int), bool>();
        foreach (var pile in spicePiles)
        {
            var cells = CellsOf(pile);
            int keep = cells.Length;
            if (pile.FindModule<Logic.Modules.Supply.SupplyWarehouseDockUpdate>() is { } dock)
                keep = dock.Boxes <= 0 ? 0 : Math.Clamp((int)MathF.Ceiling(cells.Length * (float)dock.Boxes / Math.Max(1, Math.Max(dock.StartingBoxes, dock.Boxes))), 1, cells.Length);
            for (int k = 0; k < keep; k++)
                live[(cells[k].I, cells[k].J)] = cells[k].Rich;
        }

        const float cell = TdScale.Cell;
        foreach (var ((i, j), rich) in live)
        {
            bool Has(int di, int dj) => live.ContainsKey((i + di, j + dj));
            // Up the map is +j.
            var clear = ClearSides.None;
            if (!Has(0, 1)) clear |= T | TL | TR;
            if (!Has(-1, 0)) clear |= L | TL | BL;
            if (!Has(1, 0)) clear |= R | TR | BR;
            if (!Has(0, -1)) clear |= B | BL | BR;
            if (!Has(-1, 1)) clear |= TL;
            if (!Has(1, 1)) clear |= TR;
            if (!Has(-1, -1)) clear |= BL;
            if (!Has(1, -1)) clear |= BR;
            int index = clear == ClearSides.None ? (rich ? 1 : 0) : SpiceSpriteMap.TryGetValue(clear, out int edge) ? edge : 0;
            string look = SpiceLooks[(int)((uint)(i * 73856093 ^ j * 19349663) % 4)];
            if (d2k.Sequence("resources", look) is not { } seq || d2k.SpriteFile(seq.File) is not { } file)
                continue;
            int frame = seq.FrameIndex(0, index);
            if (frame < 0 || frame >= file.Frames.Count || D2kTexture(file, frame, 0xFFFFFFFF, 0) is not { } texture)
                continue;
            Vector3 Corner(float x, float y) => new(x, y, game.Terrain.GroundHeight(x, y) + 0.3f);
            float x0 = i * cell, x1 = (i + 1) * cell, y0 = j * cell, y1 = (j + 1) * cell;
            // The tile's top row lies at the cell's north edge.
            var a = Corner(x0, y1); var b = Corner(x1, y1); var c = Corner(x1, y0); var d = Corner(x0, y0);
            renderer.Triangle(texture, EffectBlend.Alpha, a, new(0, 0), b, new(1, 0), c, new(1, 1), Vector4.One);
            renderer.Triangle(texture, EffectBlend.Alpha, a, new(0, 0), c, new(1, 1), d, new(0, 1), Vector4.One);
        }
    }
}
