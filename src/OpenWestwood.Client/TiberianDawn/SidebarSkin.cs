using System.Numerics;
using OpenWestwood.Core.Rules;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// OpenWESTWOOD: a Westwood game's own sidebar (rules section "Sidebars", one entry per skin, chosen by the player's
/// Side): the original pictures and where they go, in the original screen's pixels (Width x Height: Red Alert's 160 x 400
/// sidebar of its 640 x 400 screen ...). It is drawn to the screen's height, at the right edge. All positions are from
/// the sidebar's top left corner.
/// <code>
/// Sidebars:
///   RAAllies:
///     Sides: RAAllies               # the factions' Side values that use it
///     Width: 160
///     Height: 400
///     Pieces:                       # drawn in order: an image name (see ImageCatalog) at X, Y
///     - Image: RASHAPE:SIDE1NA:0
///       X: 0
///       Y: 16
///     Radar: 0 21 160 133           # where the radar (mini-map) goes: X Y W H
///     RadarOff: RASHAPE:NATORADR:0  # what shows there without a radar building
///     Credits: 80 1                 # the money: its middle top
///     CreditsColor: 255 255 255
///     Strip: RASHAPE:STRIPNA:0      # each column's backdrop, at the column's X + StripX, Y
///     StripX: 4
///     Columns: 16 86                # the cameo columns' X
///     ColumnY: 164
///     Cameo: 64 48                  # a cameo's size
///     Rows: 4                       # cameos seen at a time in a column
///     Clock: RASHAPE:CLOCK          # build progress: frames 1..ClockFrames over the cameo ("{image}:{frame}")
///     ClockFrames: 54
///     ScrollUp: RASHAPE:STRIPUP     # under each column (frame 0 up, 1 pressed): X Y from the column's top left
///     ScrollUpAt: 0 194
///     ScrollDown: RASHAPE:STRIPDN
///     ScrollDownAt: 32 194
///     Repair: RASHAPE:REPAIR        # the buttons (frame 0, 1 while on): X Y
///     RepairAt: 4 142
///     Sell: RASHAPE:SELL
///     SellAt: ...
///     Map: RASHAPE:MAP
///     MapAt: ...
///     PowerBar: 0 160 20 240        # the power gauge's area: X Y W H (the level drawn as a bar, the drain as a mark)
///     PowerMark: RASHAPE:POWER:0    # the drain mark
/// </code>
/// </summary>
public sealed class SidebarSkin
{
    public sealed record Piece(string Image, float X, float Y);

    public string Name { get; init; } = "";
    public float Width { get; init; }
    public float Height { get; init; }
    public List<Piece> Pieces { get; } = new();
    public Vector4? Radar { get; init; }
    /// <summary>The mini-map's margin inside the radar's area (left/right, top/bottom).</summary>
    public Vector2 RadarInset { get; init; }
    public string? RadarOff { get; init; }
    public Vector2? Credits { get; init; }
    public Vector4 CreditsColor { get; init; } = Vector4.One;
    public string? Strip { get; init; }
    public float StripX { get; init; }
    public float[] Columns { get; init; } = [];
    public float ColumnY { get; init; }
    public Vector2 Cameo { get; init; } = new(64, 48);
    public int Rows { get; init; } = 4;
    public string? Clock { get; init; }
    public int ClockFrames { get; init; }
    public string? ScrollUp { get; init; }
    public Vector2 ScrollUpAt { get; init; }
    public string? ScrollDown { get; init; }
    public Vector2 ScrollDownAt { get; init; }
    public string? Repair { get; init; }
    public Vector2 RepairAt { get; init; }
    public string? Sell { get; init; }
    public Vector2 SellAt { get; init; }
    public string? Map { get; init; }
    public Vector2 MapAt { get; init; }
    public Vector4? PowerBar { get; init; }
    /// <summary>Tiberian Sun's / Red Alert 2's strips: a piece for each row of cameos (RowPiece, RowHeight tall, from
    /// ColumnY), as many rows as fit above the BottomPiece; then Rows is worked out from the height.</summary>
    public string? RowPiece { get; init; }
    public float RowHeight { get; init; }
    public string? BottomPiece { get; init; }
    public float BottomHeight { get; init; }
    /// <summary>The cameo's place in a row (from the row's top left: the column's X is added).</summary>
    public float CameoY { get; init; }
    /// <summary>One pair of scroll buttons for both columns (positions from the bottom piece's top left).</summary>
    public bool ScrollShared { get; init; }
    public string? PowerMark { get; init; }

    /// <summary>The skin for this side, if the rules have one.</summary>
    public static SidebarSkin? For(RuleSet rules, string side)
    {
        foreach (var (name, node) in rules.Section("Sidebars").Entries)
            if (RuleValue.Words(node.GetString("Sides")).Contains(side, StringComparer.OrdinalIgnoreCase))
                return Read(name, node);
        return null;
    }

    static float[] Numbers(RuleNode node, string key) =>
        RuleValue.Words(node.GetString(key)).Select(w => RuleValue.Float(w)).ToArray();

    static Vector2 V2(RuleNode node, string key) => Numbers(node, key) is { Length: >= 2 } n ? new(n[0], n[1]) : Vector2.Zero;

    static Vector4? V4(RuleNode node, string key) => Numbers(node, key) is { Length: >= 4 } n ? new(n[0], n[1], n[2], n[3]) : null;

    static string? Text(RuleNode node, string key) => node.GetString(key) is { Length: > 0 } s ? s : null;

    static SidebarSkin Read(string name, RuleNode node)
    {
        var color = Numbers(node, "CreditsColor");
        var skin = new SidebarSkin
        {
            Name = name,
            Width = RuleValue.Float(node.GetString("Width"), 160),
            Height = RuleValue.Float(node.GetString("Height"), 400),
            Radar = V4(node, "Radar"),
            RadarInset = V2(node, "RadarInset"),
            RadarOff = Text(node, "RadarOff"),
            Credits = Numbers(node, "Credits") is { Length: >= 2 } c ? new Vector2(c[0], c[1]) : null,
            CreditsColor = color.Length >= 3 ? new Vector4(color[0] / 255f, color[1] / 255f, color[2] / 255f, 1) : Vector4.One,
            Strip = Text(node, "Strip"),
            StripX = RuleValue.Float(node.GetString("StripX")),
            Columns = Numbers(node, "Columns"),
            ColumnY = RuleValue.Float(node.GetString("ColumnY")),
            Cameo = Numbers(node, "Cameo") is { Length: >= 2 } cm ? new Vector2(cm[0], cm[1]) : new Vector2(64, 48),
            Rows = RuleValue.Int(node.GetString("Rows"), 4),
            Clock = Text(node, "Clock"),
            ClockFrames = RuleValue.Int(node.GetString("ClockFrames")),
            ScrollUp = Text(node, "ScrollUp"),
            ScrollUpAt = V2(node, "ScrollUpAt"),
            ScrollDown = Text(node, "ScrollDown"),
            ScrollDownAt = V2(node, "ScrollDownAt"),
            Repair = Text(node, "Repair"),
            RepairAt = V2(node, "RepairAt"),
            Sell = Text(node, "Sell"),
            SellAt = V2(node, "SellAt"),
            Map = Text(node, "Map"),
            MapAt = V2(node, "MapAt"),
            PowerBar = V4(node, "PowerBar"),
            PowerMark = Text(node, "PowerMark"),
            RowPiece = Text(node, "RowPiece"),
            RowHeight = RuleValue.Float(node.GetString("RowHeight")),
            BottomPiece = Text(node, "BottomPiece"),
            BottomHeight = RuleValue.Float(node.GetString("BottomHeight")),
            CameoY = RuleValue.Float(node.GetString("CameoY")),
            ScrollShared = RuleValue.Bool(node.GetString("ScrollShared")),
        };
        if (node.TryGet("Pieces", out var pieces))
            foreach (var piece in pieces.AsList())
                skin.Pieces.Add(new Piece(piece.GetString("Image"), RuleValue.Float(piece.GetString("X")), RuleValue.Float(piece.GetString("Y"))));
        return skin;
    }
}
