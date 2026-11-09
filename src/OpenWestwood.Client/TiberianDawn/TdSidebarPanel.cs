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
    public sealed record Item(ObjectTemplate? Template, GameObject Factory, ProductionUpdate Production, string? Upgrade = null, string UpgradeImage = "", string UpgradeLabel = "")
    {
        public string Image => Template?.ButtonImage ?? UpgradeImage;
    }

    int CostOf(Item item) => item.Template != null ? game.Costs.BuildCost(player, item.Template)
        : Core.Rules.RuleValue.Int(game.Catalog.Upgrade(item.Upgrade!)?.GetString("BuildCost") ?? "0");

    public TdSidebarPanel(GameLogic game, Player player, UiResources ui)
    {
        this.game = game;
        this.player = player;
        this.ui = ui;
        theme = ThemeFor(player.Side);
    }

    /// <summary>Whether this player's faction is a Tiberian Dawn one.</summary>
    public static bool IsTdPlayer(Player player) =>
        Core.Rules.RuleValue.Bool(player.Faction?.GetString("TdFaction") ?? "");

    public void Layout(int width, int height)
    {
        float scale = height / 720f;
        float w = CameoW * 2 + Gap * 3;
        float top = 8 * scale, bottom = height - 225 * scale;
        Area = new Rect(width - w - 4, top, w, Math.Max(CameoH + Gap * 2, bottom - top));
    }

    public bool Contains(Vector2 point) =>
        point.X >= Area.X && point.X < Area.X + Area.Width && point.Y >= Area.Y && point.Y < Area.Y + Area.Height;

    int VisibleRows => Math.Max(1, (int)((Area.Height - theme.Header - Gap * 2 - 12) / (CameoH + Gap)));

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
        if (column == 1)
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

    Rect CameoRect(int column, int row) =>
        new(Area.X + Gap + column * (CameoW + Gap), Area.Y + theme.Header + Gap + 6 + row * (CameoH + Gap), CameoW, CameoH);

    // ---- Input ----

    /// <summary>
    /// A click on the sidebar. Returns a finished structure to place (its factory and queue entry), else
    /// null; <paramref name="message"/> says why nothing happened, if anything should be said.
    /// </summary>
    public (ObjectTemplate Template, GameObject Factory, uint EntryId)? Click(Vector2 point, bool right, out string? message)
    {
        message = null;
        if (ItemAt(point) is not { } hit)
            return null;
        var (column, item) = hit;
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
            // The yard makes one structure at a time.
            if (Factories(true).All(f => f.FindModule<ProductionUpdate>()!.Queue.Count > 0))
            {
                message = "Unable to comply, building in progress.";
                return null;
            }
            var yard = Factories(true).First(f => f.FindModule<ProductionUpdate>()!.Queue.Count == 0);
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
        ? $"{(hit.Item.Template != null ? ui.Strings.Get(hit.Item.Template.DisplayName) : hit.Item.UpgradeLabel)}  ${CostOf(hit.Item)}" : null;

    // ---- Drawing ----

    public void Draw(SpriteBatch batch)
    {
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
