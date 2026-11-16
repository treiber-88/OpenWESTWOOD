using System.Numerics;
using OpenWestwood.Client.KanesWrath;
using OpenWestwood.Client.UI;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// Kane's Wrath's own sidebar for its factions: the TacticalHUD movie's right-hand pieces (its "main" sprite's sidebarFrame
/// in the faction's frame, radar, energy meter, resource display, the main command bar with its tabs, selection details)
/// drawn from Apt.big, its 1024 x 768 stage scaled to the screen's height at the right edge. The command bar's pages are
/// filled by the game's scripts in the original; here the cameos are laid out on its page: five tabs (structures,
/// support structures, infantry, vehicles, aircraft) under the tab strip, three across, a page at a time.
/// </summary>
public sealed partial class TdSidebarPanel
{
    readonly AptRenderer? kwHud, kwBar;
    float kwScale = 1;
    Matrix3x2 kwStage = Matrix3x2.Identity;
    int kwTab = 1, kwPage;
    const int KwColumns = 3;
    // Stage positions (TacticalHUD's main sprite): the sidebar's left edge, the radar's map, the money, the energy meter,
    // the command bar and its page.
    const float KwLeft = 776, KwBarX = 799, KwBarY = 244;
    static readonly Vector4 KwRadar = new(812, 41, 179, 180);
    static readonly Vector4 KwCredits = new(851, 220, 120, 18);
    static readonly Vector4 KwEnergy = new(802, 44, 5, 176);
    // The tab strip's tabs (TacticalHUDMainCommandBar "tabs": '1'..'5' 30 wide from x 43.3; '0' the selection's).
    static readonly float[] KwTabX = [43.3f, 73.4f, 103.5f, 133.6f, 163.8f];
    const float KwTabW = 30, KwTabH = 26, KwCameo = 58, KwPageTop = 34, KwPageH = 300;

    static bool IsKwSide(string side) => side.StartsWith("KW", StringComparison.OrdinalIgnoreCase);

    string KwFactionLabel => player.Side.ToUpperInvariant() switch { "KWNOD" => "NOD", "KWSCRIN" => "Alien", _ => "GDI" };

    static readonly string[] KwShown = ["sidebarFrame", "radar", "energyMeter", "resourceDisplay", "mainCommandBar"];

    Vector2 KwToScreen(Vector2 stage) => Vector2.Transform(stage, kwStage);

    Rect KwRect(Vector4 r)
    {
        var a = KwToScreen(new Vector2(r.X, r.Y));
        return new Rect(a.X, a.Y, r.Z * kwScale, r.W * kwScale);
    }

    void KwLayout(int width, int height)
    {
        kwScale = height / 768f;
        kwStage = Matrix3x2.CreateScale(kwScale) * Matrix3x2.CreateTranslation(width - 1024 * kwScale, 0);
        Area = new Rect(width - (1024 - KwLeft) * kwScale, 0, (1024 - KwLeft) * kwScale, height);
    }

    Rect? KwRadarRect => KwRect(KwRadar);

    /// <summary>The chosen tab's items (and the panel's column they come from): main structures, support structures
    /// (defences, then the upgrades and powers), infantry, vehicles, aircraft.</summary>
    (List<Item> Items, int Column) KwItems()
    {
        var structures = Column(0);
        var units = Column(1);
        return kwTab switch
        {
            1 => (structures.Where(i => i.Template != null && !i.Template.Is("FS_BASE_DEFENSE")).ToList(), 0),
            2 => (structures.Where(i => i.Template == null || i.Template.Is("FS_BASE_DEFENSE")).ToList(), 0),
            3 => (units.Where(i => i.Template?.Is("INFANTRY") == true).ToList(), 1),
            4 => (units.Where(i => i.Template?.Is("INFANTRY") != true && i.Template?.Is("AIRCRAFT") != true).ToList(), 1),
            _ => (units.Where(i => i.Template?.Is("AIRCRAFT") == true).ToList(), 1),
        };
    }

    int KwRows => Math.Max(1, (int)(KwPageH / KwCameo));

    Rect KwSlot(int slot) => KwRect(new Vector4(KwBarX + 2 + slot % KwColumns * KwCameo, KwBarY + KwPageTop + slot / KwColumns * KwCameo, KwCameo - 2, KwCameo - 2));

    (int Column, Item Item)? KwItemAt(Vector2 point)
    {
        var (items, column) = KwItems();
        int perPage = KwColumns * KwRows;
        for (int slot = 0; slot < perPage; slot++)
        {
            int index = kwPage * perPage + slot;
            if (index >= items.Count)
                break;
            if (KwSlot(slot).Contains(point))
                return (column, items[index]);
        }
        return null;
    }

    bool KwButtonClick(Vector2 point)
    {
        for (int t = 0; t < KwTabX.Length; t++)
            if (KwRect(new Vector4(KwBarX + KwTabX[t], KwBarY, KwTabW, KwTabH)).Contains(point))
            {
                kwTab = t + 1;
                kwPage = 0;
                return true;
            }
        return false;
    }

    void KwScroll(float amount)
    {
        int perPage = KwColumns * KwRows;
        int pages = Math.Max(1, (KwItems().Items.Count + perPage - 1) / perPage);
        kwPage = Math.Clamp(kwPage - Math.Sign(amount), 0, pages - 1);
    }

    void DrawKw(SpriteBatch batch)
    {
        string label = KwFactionLabel;
        kwHud!.Label = label;
        kwHud.IgnoreTints = false;
        kwHud.Opaque = path => path.Equals("main", StringComparison.OrdinalIgnoreCase);
        kwHud.Hide = path => path.Split('.') is var parts && (parts[0] != "main" || parts.Length >= 2 && !KwShown.Contains(parts[1], StringComparer.OrdinalIgnoreCase));
        kwHud.Draw(batch, kwStage);
        if (kwBar != null)
        {
            kwBar.Label = label;
            kwBar.IgnoreTints = false;
            kwBar.Draw(batch, Matrix3x2.CreateTranslation(KwBarX, KwBarY) * kwStage);
        }
        // The chosen tab, lit.
        var tab = KwRect(new Vector4(KwBarX + KwTabX[kwTab - 1], KwBarY, KwTabW, KwTabH));
        batch.SetAdditive(true);
        batch.FillRect(tab, new Vector4(0.35f, 0.3f, 0.15f, 1));
        batch.SetAdditive(false);

        if (HasRadar?.Invoke() == true && RadarDrawer != null && KwRadarRect is { } radar)
        {
            batch.FillRect(radar, new Vector4(0, 0, 0, 1));
            RadarDrawer(batch, radar);
        }

        // Power: the energy meter filled to what is made (red when short).
        float made = player.Energy.Production, used = player.Energy.Consumption;
        var meter = KwRect(KwEnergy);
        float level = made <= 0 ? 0 : Math.Clamp(made / Math.Max(Math.Max(made, used), 1), 0, 1);
        batch.FillRect(new Rect(meter.X, meter.Y + meter.Height * (1 - level), meter.Width, meter.Height * level),
            used > made ? new Vector4(0.9f, 0.15f, 0.1f, 0.9f) : new Vector4(0.25f, 0.8f, 1, 0.9f));

        // The money.
        var box = KwRect(KwCredits);
        var creditFont = ui.Fonts.Get("Arial", Math.Max(8, (int)(13 * kwScale)), bold: true);
        string money = "$" + player.Money.Amount;
        creditFont.DrawWithShadow(batch, money, new Vector2(MathF.Round(box.X + (box.Width - creditFont.Measure(money)) / 2), MathF.Round(box.Y)),
            new Vector4(1, 0.85f, 0.35f, 1), new Vector4(0, 0, 0, 1));

        // The cameos of the chosen tab's page.
        var font = ui.Fonts.Get("Arial", Math.Max(8, (int)(9 * kwScale)), bold: true);
        var (items, column) = KwItems();
        int perPage = KwColumns * KwRows;
        int pages = Math.Max(1, (items.Count + perPage - 1) / perPage);
        kwPage = Math.Clamp(kwPage, 0, pages - 1);
        for (int slot = 0; slot < perPage; slot++)
        {
            int index = kwPage * perPage + slot;
            if (index >= items.Count)
                break;
            var r = KwSlot(slot);
            var item = items[index];
            batch.FillRect(r, new Vector4(0, 0, 0, 0.6f));
            if (item.Power != null)
            {
                float charge = PowerCharge?.Invoke(item.Power, item.Factory) ?? 1f;
                ui.Images.Draw(batch, item.Image, r);
                if (charge < 1f)
                    batch.FillRect(new Rect(r.X, r.Y + r.Height * charge, r.Width, r.Height * (1 - charge)), new Vector4(0, 0, 0, 0.55f));
                else
                    Label(batch, font, "READY", r);
                batch.DrawRect(r, new Vector4(0.55f, 0.55f, 0.6f, 0.8f));
                continue;
            }
            bool affordable = player.Money.Amount >= CostOf(item);
            ui.Images.Draw(batch, item.Image, r, affordable ? Vector4.One : new Vector4(0.6f, 0.6f, 0.6f, 1));
            batch.DrawRect(r, item.Upgrade != null ? new Vector4(0.3f, 0.9f, 0.3f, 1) : new Vector4(0.55f, 0.55f, 0.6f, 0.8f));
            var entries = Entries(item, column).ToList();
            if (entries.Count == 0)
                continue;
            var first = entries[0].Entry;
            if (first.ReadyToPlace)
                Label(batch, font, "READY", r);
            else
                batch.FillRectClock(r, Math.Clamp(first.Progress, 0, 1), new Vector4(0, 0, 0, 0.55f));
            if (entries.Count > 1)
                font.DrawWithShadow(batch, entries.Count.ToString(), new Vector2(r.X + r.Width - 12 * kwScale, r.Y + 2), Vector4.One, new Vector4(0, 0, 0, 1));
        }
        if (pages > 1)
        {
            string page = $"{kwPage + 1}/{pages}";
            var last = KwSlot(perPage - 1);
            font.DrawWithShadow(batch, page, new Vector2(last.Right - font.Measure(page), last.Bottom + 2), new Vector4(0.8f, 0.9f, 1, 1), new Vector4(0, 0, 0, 1));
        }
    }
}
