using System.Numerics;
using OpenWestwood.Client.UI;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Commands;
using OpenWestwood.Logic.Modules.Production;
using OpenWestwood.Logic.Objects;
using OpenWestwood.Logic.Players;
using OpenWestwood.Logic.Templates;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// Tiberian Dawn's sidebar for a player of a TD faction in a Zero Hour engine match: structures on the
/// left (from the Construction Yard), units on the right (from barracks, factory and helipad), as TD's
/// SidebarClass lays them out. Left click builds (a finished structure is then placed on the map);
/// right click puts the last one of that kind back (cancel). The mouse wheel scrolls a column.
/// Building upgrades (Dune 2000's) sit at the end of the right column until researched.
/// </summary>
public sealed class TdSidebarPanel
{
    const float CameoW = 64, CameoH = 48, Gap = 4;

    readonly GameLogic game;
    readonly Player player;
    readonly UiResources ui;
    readonly int[] scroll = new int[2];

    public Rect Area { get; private set; }

    /// <summary>
    /// How the sidebar looks for the player's house: its colours, its header (Dune 2000's house insignia from the
    /// game's interface art, or TD's house name), its backdrop and its scroll buttons.
    /// </summary>
    sealed record Theme(Vector4 Back, Vector4 Border, Vector4 Accent, string Title, string? Insignia, string? Backdrop, string? Up, string? Down, float Header);

    readonly Theme theme;

    static Theme ThemeFor(string side) => side.ToUpperInvariant() switch
    {
        "D2KATR" => new(new(0.05f, 0.08f, 0.16f, 0.9f), new(0.45f, 0.6f, 1, 1), new(0.6f, 0.75f, 1, 1), "ATREIDES", "D2KCHROME:227,26,100,100", "D2KCHROME:0,295,226,48", "D2KCHROME:381,0,25,25", "D2KCHROME:381,0,25,25", 104),
        "D2KORD" => new(new(0.04f, 0.12f, 0.06f, 0.9f), new(0.4f, 0.85f, 0.45f, 1), new(0.55f, 1, 0.55f, 1), "ORDOS", "D2KCHROME:328,26,100,100", "D2KCHROME:0,295,226,48", "D2KCHROME:381,0,25,25", "D2KCHROME:381,0,25,25", 104),
        "D2KHAR" => new(new(0.16f, 0.04f, 0.04f, 0.9f), new(0.9f, 0.35f, 0.3f, 1), new(1, 0.5f, 0.4f, 1), "HARKONNEN", "D2KCHROME:227,127,100,90", "D2KCHROME:0,295,226,48", "D2KCHROME:381,0,25,25", "D2KCHROME:381,0,25,25", 94),
        "RAALLIES" => new(new(0.04f, 0.07f, 0.14f, 0.9f), new(0.35f, 0.55f, 0.95f, 1), new(0.55f, 0.75f, 1, 1), "ALLIES", null, null, "RASHAPE:STRIPUP:0", "RASHAPE:STRIPDN:0", 26),
        "RASOVIET" => new(new(0.14f, 0.03f, 0.03f, 0.9f), new(0.9f, 0.2f, 0.15f, 1), new(1, 0.35f, 0.25f, 1), "SOVIET", null, null, "RASHAPE:STRIPUP:0", "RASHAPE:STRIPDN:0", 26),
        "KWGDI" => new(new(0.07f, 0.07f, 0.06f, 0.9f), new(0.95f, 0.75f, 0.15f, 1), new(1, 0.85f, 0.3f, 1), "GDI", null, null, null, null, 26),
        "KWNOD" => new(new(0.1f, 0.02f, 0.02f, 0.9f), new(0.8f, 0.1f, 0.08f, 1), new(1, 0.25f, 0.2f, 1), "NOD", null, null, null, null, 26),
        "KWSCRIN" => new(new(0.06f, 0.03f, 0.1f, 0.9f), new(0.65f, 0.3f, 0.95f, 1), new(0.85f, 0.6f, 1, 1), "SCRIN", null, null, null, null, 26),
        "TSGDI" => new(new(0.08f, 0.07f, 0.03f, 0.9f), new(0.9f, 0.75f, 0.2f, 1), new(1, 0.85f, 0.35f, 1), "GDI", null, null, null, null, 26),
        "TSNOD" => new(new(0.12f, 0.02f, 0.02f, 0.9f), new(0.85f, 0.12f, 0.1f, 1), new(1, 0.3f, 0.2f, 1), "NOD", null, null, null, null, 26),
        "RA2ALLIES" => new(new(0.03f, 0.06f, 0.13f, 0.9f), new(0.3f, 0.6f, 1, 1), new(0.6f, 0.85f, 1, 1), "ALLIES", null, null, null, null, 26),
        "RA2SOVIET" => new(new(0.13f, 0.02f, 0.02f, 0.9f), new(0.95f, 0.15f, 0.1f, 1), new(1, 0.4f, 0.2f, 1), "SOVIET", null, null, null, null, 26),
        "RA2YURI" => new(new(0.09f, 0.02f, 0.12f, 0.9f), new(0.7f, 0.3f, 0.95f, 1), new(0.85f, 0.55f, 1, 1), "YURI", null, null, null, null, 26),
        // Emperor: Battle for Dune's houses, in their ArtIni colours.
        "EMATR" => new(new(0.02f, 0.06f, 0.12f, 0.9f), new(0f, 0.52f, 0.89f, 1), new(0.45f, 0.75f, 1, 1), "ATREIDES", null, null, null, null, 26),
        "EMHAR" => new(new(0.12f, 0.03f, 0.02f, 0.9f), new(0.69f, 0.14f, 0.09f, 1), new(1, 0.4f, 0.3f, 1), "HARKONNEN", null, null, null, null, 26),
        "EMORD" => new(new(0.03f, 0.1f, 0.07f, 0.9f), new(0.57f, 0.99f, 0.79f, 1), new(0.7f, 1, 0.85f, 1), "ORDOS", null, null, null, null, 26),
        "TDNOD" => new(new(0.12f, 0.02f, 0.02f, 0.9f), new(0.85f, 0.15f, 0.1f, 1), new(1, 0.25f, 0.2f, 1), "NOD", null, null, "TDSHAPE:BTN-UP:0", "TDSHAPE:BTN-DN:0", 26),
        _ => new(new(0.08f, 0.07f, 0.03f, 0.9f), new(0.85f, 0.7f, 0.25f, 1), new(1, 0.85f, 0.35f, 1), "GDI", null, null, "TDSHAPE:BTN-UP:0", "TDSHAPE:BTN-DN:0", 26),
    };

    /// <summary>One cameo: what it makes (an object, or a player upgrade) and the factory that makes it.</summary>
    public sealed record Item(ObjectTemplate? Template, GameObject Factory, ProductionUpdate Production, string? Upgrade = null, string UpgradeImage = "", string UpgradeLabel = "",
        Logic.Templates.CommandButton? Power = null)
    {
        public string Image => Template?.ButtonImage ?? Power?.ButtonImage ?? UpgradeImage;
    }

    // ---- The game's own sidebar (OpenWESTWOOD: SidebarSkin, from the rules' Sidebars) ----

    readonly SidebarSkin? skin;
    float scale = 1;

    /// <summary>Draws the mini-map into a rectangle (the radar's own, from the game screen).</summary>
    public Action<SpriteBatch, Rect>? RadarDrawer { get; set; }
    /// <summary>Whether the player has a working radar now.</summary>
    public Func<bool>? HasRadar { get; set; }
    /// <summary>The player's powers (superweapons and the like): their buttons and buildings.</summary>
    public Func<IEnumerable<(Logic.Templates.CommandButton Button, GameObject Source)>>? Powers { get; set; }
    /// <summary>A power's charge (0..1), null if it doesn't charge.</summary>
    public Func<Logic.Templates.CommandButton, GameObject, float?>? PowerCharge { get; set; }
    /// <summary>A power's cameo was clicked.</summary>
    public Action<Logic.Templates.CommandButton, GameObject>? UsePower { get; set; }

    /// <summary>The sidebar's Repair / Sell buttons: the next click on one of the player's buildings repairs or sells it.</summary>
    public enum ClickMode { None, Repair, Sell }
    public ClickMode Mode { get; set; }

    /// <summary>Whether this is the game's own sidebar (it takes the Zero Hour control bar's place).</summary>
    public bool HasSkin => skin != null;

    Rect P(float x, float y, float w, float h) => new(Area.X + x * scale, Area.Y + y * scale, w * scale, h * scale);

    /// <summary>Where the mini-map is drawn on the skin's radar (null without one).</summary>
    public Rect? RadarMapRect => skin?.Radar is { } r
        ? P(r.X + skin.RadarInset.X, r.Y + skin.RadarInset.Y, r.Z - 2 * skin.RadarInset.X, r.W - 2 * skin.RadarInset.Y) : null;

    int CostOf(Item item) => item.Template != null ? game.Costs.BuildCost(player, item.Template)
        : Core.Rules.RuleValue.Int(game.Catalog.Upgrade(item.Upgrade!)?.GetString("BuildCost") ?? "0");

    public TdSidebarPanel(GameLogic game, Player player, UiResources ui)
    {
        this.game = game;
        this.player = player;
        this.ui = ui;
        theme = ThemeFor(player.Side);
        skin = SidebarSkin.For(game.Rules, player.Side);
    }

    /// <summary>Whether this player's faction is a Tiberian Dawn one.</summary>
    public static bool IsTdPlayer(Player player) =>
        Core.Rules.RuleValue.Bool(player.Faction?.GetString("TdFaction") ?? "");

    public void Layout(int width, int height)
    {
        if (skin != null)
        {
            // The game's own sidebar, to the screen's height at its right edge.
            scale = height / skin.Height;
            Area = new Rect(width - skin.Width * scale, 0, skin.Width * scale, height);
            return;
        }
        float uiScale = height / 720f;
        float w = CameoW * 2 + Gap * 3;
        // (Down to just above the control bar's power bar: the room under the radar's height was going unused.)
        float top = 8 * uiScale, bottom = height - 150 * uiScale;
        Area = new Rect(width - w - 4, top, w, Math.Max(CameoH + Gap * 2, bottom - top));
    }

    public bool Contains(Vector2 point) =>
        point.X >= Area.X && point.X < Area.X + Area.Width && point.Y >= Area.Y && point.Y < Area.Y + Area.Height;

    int VisibleRows => skin != null ? SkinRows(skin) : Math.Max(1, (int)((Area.Height - theme.Header - Gap * 2 - 12) / (CameoH + Gap)));

    /// <summary>A skin's rows of cameos: fixed, or (repeated row pieces) as many as fit the screen's height.</summary>
    int SkinRows(SidebarSkin s) => s.RowPiece != null && s.RowHeight > 0
        ? Math.Max(1, (int)((Area.Height / scale - s.ColumnY - s.BottomHeight) / s.RowHeight)) : s.Rows;

    /// <summary>Where a skin's bottom piece goes (under the last row).</summary>
    float SkinBottomY(SidebarSkin s) => s.ColumnY + SkinRows(s) * (s.RowPiece != null ? s.RowHeight : s.Cameo.Y);

    // ---- What can be built ----

    IEnumerable<GameObject> Factories(bool structures) =>
        game.ObjectsOwnedBy(player).Where(o => !o.IsEffectivelyDead && !o.IsUnderConstruction
            && o.FindModule<ProductionUpdate>() is { } p && p.PlacesStructures == structures);

    /// <summary>Column 0: structures; column 1: units (each from the least busy factory that makes it).</summary>
    public List<Item> Column(int column)
    {
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in Factories(column == 0))
        {
            var production = factory.FindModule<ProductionUpdate>()!;
            foreach (string name in CommandSets.BuildableObjects(game.Catalog, factory.CommandSetName, "UNIT_BUILD"))
            {
                if (game.Templates.Find(name) is not { } template || !player.CanBuild(template)
                    || !Prerequisites.AreMet(game, player, template.Rules))
                    continue;
                if (!items.TryGetValue(name, out var existing) || production.Queue.Count < existing.Production.Queue.Count)
                    items[name] = new Item(template, factory, production);
            }
        }
        var list = items.Values.ToList();
        // (The game's own sidebar: the powers first in the structures strip, as Westwood's special weapon cameos.)
        if (column == 0 && skin != null && Powers != null)
            list.InsertRange(0, Powers().Select(p => new Item(null, p.Source, null!, Power: p.Button)));
        // Upgrades (Dune 2000's factory upgrades) go in the structures strip, as in Dune 2000, not at the bottom of
        // the long units list, where they were out of sight (and the units they unlock never appeared).
        if (column == 0)
            foreach (var factory in Factories(true).Concat(Factories(false)))
                foreach (var (_, button) in CommandSets.Buttons(game.Catalog, factory.CommandSetName))
                    if (button.CommandType == "PLAYER_UPGRADE" && button.Upgrade.Length > 0 && !player.HasUpgrade(button.Upgrade)
                        && list.All(i => i.Upgrade != button.Upgrade))
                        list.Add(new Item(null, factory, factory.FindModule<ProductionUpdate>()!, button.Upgrade, button.ButtonImage, button.TextLabel));
        return list;
    }

    /// <summary>The queue entries for this kind of object, at any of the player's factories of that column.</summary>
    IEnumerable<(ProductionUpdate Production, GameObject Factory, ProductionUpdate.Entry Entry)> Entries(Item item, int column) =>
        (item.Upgrade != null ? Factories(true).Concat(Factories(false)) : Factories(column == 0)).SelectMany(f => f.FindModule<ProductionUpdate>()!.Queue
            .Where(e => item.Upgrade != null ? e.Upgrade == item.Upgrade : e.Unit?.Name == item.Template!.Name).Select(e => (f.FindModule<ProductionUpdate>()!, f, e)));

    (int Column, Item Item)? ItemAt(Vector2 point)
    {
        for (int column = 0; column < 2; column++)
        {
            var list = Column(column);
            for (int row = 0; row < VisibleRows; row++)
            {
                int index = row + scroll[column];
                if (index >= list.Count)
                    break;
                var r = CameoRect(column, row);
                if (point.X >= r.X && point.X < r.X + r.Width && point.Y >= r.Y && point.Y < r.Y + r.Height)
                    return (column, list[index]);
            }
        }
        return null;
    }

    Rect CameoRect(int column, int row) => skin != null
        ? P(skin.Columns[Math.Min(column, skin.Columns.Length - 1)] + skin.StripX,
            skin.ColumnY + skin.CameoY + row * (skin.RowPiece != null ? skin.RowHeight : skin.Cameo.Y), skin.Cameo.X, skin.Cameo.Y)
        : new(Area.X + Gap + column * (CameoW + Gap), Area.Y + theme.Header + Gap + 6 + row * (CameoH + Gap), CameoW, CameoH);

    /// <summary>A skin's button (image frame 0) at a spot, as a screen rectangle; null if the skin hasn't it.</summary>
    /// <summary>A button's picture in a state (0 up, 1 down/on): "a|b" names each state's picture; otherwise the shape's
    /// frame of that number ("image:frame").</summary>
    static string StateImage(string image, int state) =>
        image.Contains('|') ? image.Split('|')[Math.Min(state, image.Split('|').Length - 1)] : $"{image}:{state}";

    Rect? ButtonRect(string? image, Vector2 at, float columnX = 0, float columnY = 0)
    {
        if (image == null || ui.Images.Find(StateImage(image, 0)) is not { } found)
            return null;
        return P(columnX + at.X, columnY + at.Y, found.PixelWidth, found.PixelHeight);
    }

    bool SkinButtonClick(Vector2 point)
    {
        if (skin == null)
            return false;
        if (skin.ScrollShared)
        {
            float bottom = SkinBottomY(skin);
            int step = 0;
            if (ButtonRect(skin.ScrollUp, skin.ScrollUpAt, 0, bottom) is { } sharedUp && sharedUp.Contains(point))
                step = -1;
            else if (ButtonRect(skin.ScrollDown, skin.ScrollDownAt, 0, bottom) is { } sharedDown && sharedDown.Contains(point))
                step = 1;
            if (step != 0)
            {
                for (int c = 0; c < 2; c++)
                    scroll[c] = Math.Clamp(scroll[c] + step, 0, Math.Max(0, Column(c).Count - VisibleRows));
                return true;
            }
        }
        else for (int column = 0; column < Math.Min(2, skin.Columns.Length); column++)
        {
            if (ButtonRect(skin.ScrollUp, skin.ScrollUpAt, skin.Columns[column], skin.ColumnY) is { } up && up.Contains(point))
            {
                scroll[column] = Math.Max(0, scroll[column] - 1);
                return true;
            }
            if (ButtonRect(skin.ScrollDown, skin.ScrollDownAt, skin.Columns[column], skin.ColumnY) is { } down && down.Contains(point))
            {
                scroll[column] = Math.Min(Math.Max(0, Column(column).Count - VisibleRows), scroll[column] + 1);
                return true;
            }
        }
        if (ButtonRect(skin.Repair, skin.RepairAt) is { } repair && repair.Contains(point))
        {
            Mode = Mode == ClickMode.Repair ? ClickMode.None : ClickMode.Repair;
            return true;
        }
        if (ButtonRect(skin.Sell, skin.SellAt) is { } sell && sell.Contains(point))
        {
            Mode = Mode == ClickMode.Sell ? ClickMode.None : ClickMode.Sell;
            return true;
        }
        return ButtonRect(skin.Map, skin.MapAt) is { } map && map.Contains(point);
    }

    // ---- Input ----

    /// <summary>
    /// A click on the sidebar. Returns a finished structure to place (its factory and queue entry), else
    /// null; <paramref name="message"/> says why nothing happened, if anything should be said.
    /// </summary>
    public (ObjectTemplate Template, GameObject Factory, uint EntryId)? Click(Vector2 point, bool right, out string? message)
    {
        message = null;
        if (!right && SkinButtonClick(point))
            return null;
        if (ItemAt(point) is not { } hit)
            return null;
        var (column, item) = hit;
        if (item.Power != null)
        {
            if (!right && (PowerCharge?.Invoke(item.Power, item.Factory) ?? 1f) >= 1f)
                UsePower?.Invoke(item.Power, item.Factory);
            return null;
        }
        var entries = Entries(item, column).ToList();
        if (right)
        {
            // TD's right click: hold, then cancel. Here: cancel the last one of this kind.
            if (entries.Count > 0)
            {
                var last = entries[^1];
                game.Commands.Enqueue(new CancelProductionCommand(player.Index, last.Factory.Id, last.Entry.Id));
            }
            return null;
        }
        if (item.Upgrade != null)
        {
            // An upgrade is researched once.
            if (entries.Count == 0)
            {
                if (player.Money.Amount < CostOf(item))
                    message = "Insufficient funds.";
                else
                    game.Commands.Enqueue(new ProduceUpgradeCommand(player.Index, item.Factory.Id, item.Upgrade));
            }
            return null;
        }
        if (column == 0)
        {
            if (entries.FirstOrDefault(e => e.Entry.ReadyToPlace) is { Entry: { } ready } done)
                return (item.Template!, done.Factory, ready.Id);
            // OpenWESTWOOD (the user's choice): structures queue up, at the yard with the fewest on order; each is built in
            // turn and waits to be placed. (The original made one at a time.)
            var yard = Factories(true).OrderBy(f => f.FindModule<ProductionUpdate>()!.Queue.Count(e => e.Unit != null)).First();
            Produce(yard, item.Template!, out message);
            return null;
        }
        Produce(item.Factory, item.Template!, out message);
        return null;
    }

    void Produce(GameObject factory, ObjectTemplate template, out string? message)
    {
        message = factory.FindModule<ProductionUpdate>()!.CanQueueUnit(template) switch
        {
            "NotEnoughMoney" => "Insufficient funds.",
            "QueueFull" => "Unable to comply, building in progress.",
            _ => null,
        };
        if (message == null)
            game.Commands.Enqueue(new ProduceUnitCommand(player.Index, factory.Id, template.Name));
    }

    public void Scroll(Vector2 point, float amount)
    {
        int column = point.X < Area.X + Area.Width / 2 ? 0 : 1;
        int max = Math.Max(0, Column(column).Count - VisibleRows);
        scroll[column] = Math.Clamp(scroll[column] - Math.Sign(amount), 0, max);
    }

    /// <summary>The cameo's name under the mouse (for a tooltip), if any.</summary>
    public string? TooltipAt(Vector2 point) => ItemAt(point) is { } hit
        ? hit.Item.Power != null ? ui.Strings.Get(hit.Item.Power.TextLabel)
        : $"{(hit.Item.Template != null ? ui.Strings.Get(hit.Item.Template.DisplayName) : hit.Item.UpgradeLabel)}  ${CostOf(hit.Item)}" : null;

    // ---- Drawing ----

    public void Draw(SpriteBatch batch)
    {
        if (skin != null)
        {
            DrawSkin(batch, skin);
            return;
        }
        batch.FillRect(Area, theme.Back);
        // The house's backdrop (Dune 2000: the sidebar's icon rows), its header and frame.
        if (theme.Backdrop != null)
            for (float y = Area.Y + theme.Header; y < Area.Y + Area.Height; y += 48)
                ui.Images.Draw(batch, theme.Backdrop, new Rect(Area.X, y, Area.Width, Math.Min(48, Area.Y + Area.Height - y)), new Vector4(1, 1, 1, 0.55f));
        var header = new Rect(Area.X, Area.Y, Area.Width, theme.Header);
        batch.FillRect(header, theme.Back with { W = 1 });
        if (theme.Insignia != null && ui.Images.Find(theme.Insignia) is { } insignia)
        {
            float h = theme.Header - 4, w = h * insignia.PixelWidth / insignia.PixelHeight;
            ui.Images.Draw(batch, insignia, new Rect(Area.X + (Area.Width - w) / 2, Area.Y + 2, w, h));
        }
        else
        {
            var titleFont = ui.Fonts.Get("Arial", 14, bold: true);
            float tw = titleFont.Measure(theme.Title);
            titleFont.DrawWithShadow(batch, theme.Title, new Vector2(Area.X + (Area.Width - tw) / 2, Area.Y + 4), theme.Accent, new Vector4(0, 0, 0, 1));
        }
        batch.FillRect(new Rect(Area.X, Area.Y + theme.Header, Area.Width, 2), theme.Border);
        batch.DrawRect(Area, theme.Border);
        var font = ui.Fonts.Get("Arial", 9, bold: true);
        for (int column = 0; column < 2; column++)
        {
            var list = Column(column);
            scroll[column] = Math.Clamp(scroll[column], 0, Math.Max(0, list.Count - VisibleRows));
            for (int row = 0; row < VisibleRows; row++)
            {
                int index = row + scroll[column];
                if (index >= list.Count)
                    break;
                var item = list[index];
                var r = CameoRect(column, row);
                bool affordable = player.Money.Amount >= CostOf(item);
                ui.Images.Draw(batch, item.Image, r, affordable ? Vector4.One : new Vector4(0.6f, 0.6f, 0.6f, 1));
                batch.DrawRect(r, theme.Border with { W = 0.6f });
                if (item.Upgrade != null)
                    batch.DrawRect(r, new Vector4(0.3f, 0.9f, 0.3f, 1)); // upgrades: a green frame
                var entries = Entries(item, column).ToList();
                if (entries.Count == 0)
                    continue;
                var first = entries[0].Entry;
                if (first.ReadyToPlace)
                {
                    Label(batch, font, "READY", r);
                }
                else
                {
                    // TD's clock: the part still to build is darkened.
                    float progress = Math.Clamp(first.Progress, 0, 1);
                    batch.FillRect(new Rect(r.X, r.Y + r.Height * progress, r.Width, r.Height * (1 - progress)), new Vector4(0, 0, 0, 0.55f));
                }
                if (entries.Count > 1)
                    font.DrawWithShadow(batch, entries.Count.ToString(), new Vector2(r.X + r.Width - 12, r.Y + 2), Vector4.One, new Vector4(0, 0, 0, 1));
            }
            // Scroll hints: the house's up and down buttons (TD's BTN-UP/BTN-DN, Dune 2000's sidebar button).
            float cx = Area.X + Gap + column * (CameoW + Gap) + CameoW / 2;
            if (scroll[column] > 0)
                ScrollMark(batch, theme.Up, new Rect(cx - 8, Area.Y + theme.Header + 2, 16, 8), up: true);
            if (list.Count - scroll[column] > VisibleRows)
                ScrollMark(batch, theme.Down, new Rect(cx - 8, Area.Y + Area.Height - 10, 16, 8), up: false);
        }
    }

    void DrawImage(SpriteBatch batch, string? name, float x, float y, Vector4? tint = null)
    {
        if (name != null && ui.Images.Find(name) is { } image)
            ui.Images.Draw(batch, image, P(x, y, image.PixelWidth, image.PixelHeight), tint);
    }

    /// <summary>The game's own sidebar: its pieces, radar, credits, power gauge, strips, cameos and buttons.</summary>
    void DrawSkin(SpriteBatch batch, SidebarSkin s)
    {
        foreach (var piece in s.Pieces)
            DrawImage(batch, piece.Image, piece.X, piece.Y);
        // Repeated rows down to the bottom piece (Tiberian Sun, Red Alert 2), and under it whatever is left (the bottom
        // piece's last line stretched, so the screen's edge shows no gap).
        if (s.RowPiece != null && s.RowHeight > 0)
        {
            for (int row = 0; row < SkinRows(s); row++)
                DrawImage(batch, s.RowPiece, 0, s.ColumnY + row * s.RowHeight);
            float bottom = SkinBottomY(s);
            if (s.BottomPiece != null && ui.Images.Find(s.BottomPiece) is { } end)
            {
                ui.Images.Draw(batch, end, P(0, bottom, end.PixelWidth, end.PixelHeight));
                float below = Area.Height / scale - bottom - end.PixelHeight;
                if (below > 0)
                    batch.FillRect(P(0, bottom + end.PixelHeight, s.Width, below), new Vector4(0, 0, 0, 1));
            }
        }
        if (s.Radar is { } radar)
        {
            if (HasRadar?.Invoke() == true && RadarDrawer != null && RadarMapRect is { } map)
            {
                batch.FillRect(map, new Vector4(0, 0, 0, 1));
                RadarDrawer(batch, map);
            }
            else if (s.RadarOff != null && ui.Images.Find(s.RadarOff) is { } emblem)
            {
                // The side's emblem, in the middle of the radar (Dune 2000's insignia are smaller than its radar).
                float ex = radar.X + Math.Max(0, (radar.Z - emblem.PixelWidth) / 2), ey = radar.Y + Math.Max(0, (radar.W - emblem.PixelHeight) / 2);
                ui.Images.Draw(batch, emblem, P(ex, ey, Math.Min(emblem.PixelWidth, radar.Z), Math.Min(emblem.PixelHeight, radar.W)));
            }
        }
        if (s.Credits is { } credits)
        {
            var creditFont = ui.Fonts.Get("Arial", Math.Max(8, (int)(10 * scale)), bold: true);
            string money = player.Money.Amount.ToString();
            float tw = creditFont.Measure(money);
            creditFont.DrawWithShadow(batch, money, new Vector2(MathF.Round(Area.X + credits.X * scale - tw / 2), MathF.Round(Area.Y + credits.Y * scale)), s.CreditsColor, new Vector4(0, 0, 0, 1));
        }
        if (s.PowerBar is { } bar)
        {
            // The level made (green; yellow when nearly all used, red when short) and the drain's mark, out of a little
            // more than the larger of the two.
            float made = player.Energy.Production, used = player.Energy.Consumption;
            float top = Math.Max(Math.Max(made, used), 100) * 1.1f;
            float madeH = bar.W * made / top, usedH = bar.W * used / top;
            var color = made <= 0 ? new Vector4(0.8f, 0.1f, 0.1f, 1) : used > made ? new Vector4(0.9f, 0.15f, 0.1f, 1)
                : used > made * 0.8f ? new Vector4(0.95f, 0.8f, 0.1f, 1) : new Vector4(0.2f, 0.85f, 0.2f, 1);
            batch.FillRect(P(bar.X, bar.Y + bar.W - madeH, bar.Z, madeH), color);
            if (s.PowerMark != null && ui.Images.Find(s.PowerMark) is { } mark)
                ui.Images.Draw(batch, mark, P(bar.X - 2, bar.Y + bar.W - usedH - mark.PixelHeight / 2, mark.PixelWidth, mark.PixelHeight));
        }
        var font = ui.Fonts.Get("Arial", Math.Max(8, (int)(9 * scale)), bold: true);
        for (int column = 0; column < Math.Min(2, s.Columns.Length); column++)
        {
            float cx = s.Columns[column];
            if (s.Strip != null)
                DrawImage(batch, s.Strip.Replace("{column}", column.ToString()), cx + s.StripX, s.ColumnY);
            var list = Column(column);
            scroll[column] = Math.Clamp(scroll[column], 0, Math.Max(0, list.Count - VisibleRows));
            for (int row = 0; row < VisibleRows; row++)
            {
                int index = row + scroll[column];
                if (index >= list.Count)
                    break;
                var item = list[index];
                var r = CameoRect(column, row);
                if (item.Power != null)
                {
                    float charge = PowerCharge?.Invoke(item.Power, item.Factory) ?? 1f;
                    ui.Images.Draw(batch, item.Image, r);
                    if (charge < 1f)
                        DrawClock(batch, s, r, charge);
                    else
                        Label(batch, font, "READY", r);
                    continue;
                }
                bool affordable = player.Money.Amount >= CostOf(item);
                ui.Images.Draw(batch, item.Image, r, affordable ? Vector4.One : new Vector4(0.6f, 0.6f, 0.6f, 1));
                var entries = Entries(item, column).ToList();
                if (entries.Count == 0)
                    continue;
                var first = entries[0].Entry;
                if (first.ReadyToPlace)
                    Label(batch, font, "READY", r);
                else
                    DrawClock(batch, s, r, Math.Clamp(first.Progress, 0, 1));
                if (entries.Count > 1)
                    font.DrawWithShadow(batch, entries.Count.ToString(), new Vector2(r.X + r.Width - 12 * scale, r.Y + 2), Vector4.One, new Vector4(0, 0, 0, 1));
            }
            if (!s.ScrollShared)
            {
                if (s.ScrollUp != null)
                    DrawImage(batch, StateImage(s.ScrollUp, 0), cx + s.ScrollUpAt.X, s.ColumnY + s.ScrollUpAt.Y);
                if (s.ScrollDown != null)
                    DrawImage(batch, StateImage(s.ScrollDown, 0), cx + s.ScrollDownAt.X, s.ColumnY + s.ScrollDownAt.Y);
            }
        }
        if (s.ScrollShared)
        {
            float bottom = SkinBottomY(s);
            if (s.ScrollUp != null)
                DrawImage(batch, StateImage(s.ScrollUp, 0), s.ScrollUpAt.X, bottom + s.ScrollUpAt.Y);
            if (s.ScrollDown != null)
                DrawImage(batch, StateImage(s.ScrollDown, 0), s.ScrollDownAt.X, bottom + s.ScrollDownAt.Y);
        }
        if (s.Repair != null)
            DrawImage(batch, StateImage(s.Repair, Mode == ClickMode.Repair ? 1 : 0), s.RepairAt.X, s.RepairAt.Y);
        if (s.Sell != null)
            DrawImage(batch, StateImage(s.Sell, Mode == ClickMode.Sell ? 1 : 0), s.SellAt.X, s.SellAt.Y);
        if (s.Map != null)
            DrawImage(batch, StateImage(s.Map, 0), s.MapAt.X, s.MapAt.Y);
    }

    /// <summary>A cameo's build clock: the game's own (its clock frames, see-through) or the darkened part still to go.</summary>
    void DrawClock(SpriteBatch batch, SidebarSkin s, Rect r, float progress)
    {
        if (s.Clock != null && s.ClockFrames > 0 && ui.Images.Find($"{s.Clock}:{1 + Math.Min(s.ClockFrames - 1, (int)(progress * s.ClockFrames))}") is { } clock)
            ui.Images.Draw(batch, clock, r, new Vector4(1, 1, 1, 0.6f));
        else
            batch.FillRect(new Rect(r.X, r.Y + r.Height * progress, r.Width, r.Height * (1 - progress)), new Vector4(0, 0, 0, 0.55f));
    }

    void ScrollMark(SpriteBatch batch, string? image, Rect r, bool up)
    {
        if (image != null && ui.Images.Find(image) is { } found)
            ui.Images.Draw(batch, found, r);
        else
            batch.FillRect(new Rect(r.X + 2, up ? r.Y : r.Y + r.Height - 2, r.Width - 4, 2), theme.Accent);
    }

    static void Label(SpriteBatch batch, GameFont font, string text, Rect r)
    {
        float w = font.Measure(text);
        font.DrawWithShadow(batch, text, new Vector2(r.X + (r.Width - w) / 2, r.Y + r.Height / 2 - 6), new Vector4(1, 1, 1, 1), new Vector4(0, 0, 0, 1));
    }
}
