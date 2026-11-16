using System.Numerics;
using OpenWestwood.Client.Emperor;
using OpenWestwood.Client.UI;

namespace OpenWestwood.Client.TiberianDawn;

/// <summary>
/// Emperor: Battle for Dune's own sidebar (EmperorSidebarModel) for its houses: the 3D sidebar drawn to the screen's
/// height at its right edge, the icons in its fifteen slots (glow00..14, a page at a time, the arrows turning pages), its
/// tabs (Buildings, Units, Infantry, Upgrades; the Starport's market is not in the game yet), the Repair and Sell buttons,
/// the radar in its Radar mesh (the house's logo there without a radar), the power arc lit by the power made, the credits
/// in the number box.
/// </summary>
public sealed partial class TdSidebarPanel
{
    readonly EmperorSidebarModel? emperor;
    // The part of the interface plane the sidebar takes (model units: x right, y up).
    const float EmX0 = 150, EmX1 = 322, EmY0 = -178, EmY1 = 254;
    const int EmSlots = 15;
    float emScale = 1;
    int emTab, emPage;
    readonly Dictionary<string, Rect> emRects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tabs: the mesh clicked, the mesh shown while it is the chosen one.</summary>
    static readonly (string Mesh, string Selected)[] EmTabs =
        [("buildingtab", "buildingtab01"), ("tanktab", "tanktab01"), ("infantrytab", "infantrytab01"), ("upgradetab", "upgradetab01"), ("starporttab", "starporttab01")];

    static readonly HashSet<string> EmHidden = new(StringComparer.OrdinalIgnoreCase)
    {
        // Shown by the game only now and then: the radar's alert, the chat line, the "new item" marker.
        "Alert", "pagetext", "loadboxedge", "cursor01x", "newitemVech",
    };

    Vector2 EmToScreen(Vector3 v) => new(Area.X + (v.X - EmX0) * emScale, Area.Y + (EmY1 - v.Y) * emScale);

    void EmperorLayout(int width, int height)
    {
        emScale = height / (EmY1 - EmY0);
        float w = (EmX1 - EmX0) * emScale;
        Area = new Rect(width - w, 0, w, height);
        // The controls' places, with the sidebar open (frame 1).
        emperor!.Pose(1);
        emRects.Clear();
        foreach (string name in emperor.NodeNames)
            if (emperor.Box(name) is { } box && box.Max.X - box.Min.X > 0.5f && box.Max.Y - box.Min.Y > 0.5f)
            {
                var a = EmToScreen(new Vector3(box.Min.X, box.Max.Y, 0));
                var b = EmToScreen(new Vector3(box.Max.X, box.Min.Y, 0));
                emRects[name] = new Rect(a.X, a.Y, b.X - a.X, b.Y - a.Y);
            }
    }

    Rect? EmRect(string name) => emRects.TryGetValue(name, out var r) ? r : null;

    Rect? EmperorRadarRect => EmRect("Radar");

    /// <summary>The chosen tab's items, and which of the panel's columns they come from (0 structures, 1 units).</summary>
    (List<Item> Items, int Column) EmperorItems() => emTab switch
    {
        0 => (Column(0).Where(i => i.Upgrade == null).ToList(), 0),
        1 => (Column(1).Where(i => i.Template?.Is("INFANTRY") != true).ToList(), 1),
        2 => (Column(1).Where(i => i.Template?.Is("INFANTRY") == true).ToList(), 1),
        3 => (Column(0).Where(i => i.Upgrade != null).ToList(), 0),
        _ => (new List<Item>(), 0),
    };

    Rect? EmSlot(int slot) => EmRect($"glow{slot:00}");

    (int Column, Item Item)? EmperorItemAt(Vector2 point)
    {
        var (items, column) = EmperorItems();
        for (int slot = 0; slot < EmSlots; slot++)
        {
            int index = emPage * EmSlots + slot;
            if (index >= items.Count)
                break;
            if (EmSlot(slot) is { } r && r.Contains(point))
                return (column, items[index]);
        }
        return null;
    }

    bool EmperorButtonClick(Vector2 point)
    {
        for (int t = 0; t < EmTabs.Length; t++)
            if (EmRect(EmTabs[t].Mesh) is { } tab && tab.Contains(point))
            {
                if (t < 4)
                {
                    emTab = t;
                    emPage = 0;
                }
                return true;
            }
        // (The script's NextPage is the RArrow mesh, PrevPage the LArrow.)
        if (EmRect("rarrow") is { } next && next.Contains(point))
        {
            EmperorScroll(-1);
            return true;
        }
        if (EmRect("larrow") is { } previous && previous.Contains(point))
        {
            EmperorScroll(1);
            return true;
        }
        if (EmRect("#buttonrepair") is { } repair && repair.Contains(point))
        {
            Mode = Mode == ClickMode.Repair ? ClickMode.None : ClickMode.Repair;
            return true;
        }
        if (EmRect("#buttonsell") is { } sell && sell.Contains(point))
        {
            Mode = Mode == ClickMode.Sell ? ClickMode.None : ClickMode.Sell;
            return true;
        }
        return false;
    }

    void EmperorScroll(float amount)
    {
        int pages = Math.Max(1, (EmperorItems().Items.Count + EmSlots - 1) / EmSlots);
        emPage = Math.Clamp(emPage - Math.Sign(amount), 0, pages - 1);
    }

    string EmHouse => player.Side.ToUpperInvariant() switch { "EMHAR" => "HK", "EMORD" => "OR", _ => "AT" };

    void DrawEmperor(SpriteBatch batch)
    {
        var model = emperor!;
        // Its idle loop (frames 1..99, the game's ActiveEvent), at 15 frames a second.
        model.Pose(1 + (float)(Environment.TickCount64 / 1000.0 * 15 % 98));
        float made = player.Energy.Production, used = player.Energy.Consumption;
        int lit = made <= 0 ? 0 : (int)MathF.Round(16 * Math.Clamp(made / Math.Max(made, Math.Max(used, 1)), 0, 1));
        bool shortOfPower = used > made;
        bool hasRadar = HasRadar?.Invoke() == true && RadarDrawer != null;
        var logo = ui.Textures.Get($"{Formats.Emperor.EmperorArtPackage.Folder}Textures/{EmHouse}logo.tga", repeat: false);
        string chosen = EmTabs[emTab].Selected;

        bool Shown(string name)
        {
            if (EmperorSidebarModel.IsHelper(name) || EmHidden.Contains(name) || name.StartsWith("blip", StringComparison.OrdinalIgnoreCase))
                return false;
            if (name.StartsWith("flare", StringComparison.OrdinalIgnoreCase) || name.StartsWith("glow", StringComparison.OrdinalIgnoreCase))
                return name.Equals("flarerepair", StringComparison.OrdinalIgnoreCase) && Mode == ClickMode.Repair
                    || name.Equals("flaresell", StringComparison.OrdinalIgnoreCase) && Mode == ClickMode.Sell;
            if (EmTabs.Any(t => t.Selected.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return name.Equals(chosen, StringComparison.OrdinalIgnoreCase);
            // (The slots' own icon meshes stand edge on: the build icons are drawn into the slots instead.)
            if (name.Length == 6 && name.StartsWith("icon", StringComparison.OrdinalIgnoreCase) && char.IsDigit(name[4]))
                return false;
            return true;
        }
        Vector4 Tint(string name)
        {
            if (name.Length == 7 && name.StartsWith("power", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), out int n))
                return n < lit ? (shortOfPower ? new Vector4(1, 0.35f, 0.3f, 1) : Vector4.One) : new Vector4(0.15f, 0.15f, 0.15f, 1);
            return Vector4.One;
        }
        model.Draw(batch, EmToScreen, Shown, name => name.Equals("Radar", StringComparison.OrdinalIgnoreCase) && !hasRadar ? logo : null, Tint);

        if (hasRadar && EmperorRadarRect is { } radar)
        {
            batch.FillRect(radar, new Vector4(0, 0, 0, 1));
            RadarDrawer!(batch, radar);
        }

        // The icons of the chosen tab's page.
        var font = ui.Fonts.Get("Arial", Math.Max(8, (int)(7 * emScale)), bold: true);
        var (items, column) = EmperorItems();
        int pages = Math.Max(1, (items.Count + EmSlots - 1) / EmSlots);
        emPage = Math.Clamp(emPage, 0, pages - 1);
        for (int slot = 0; slot < EmSlots; slot++)
        {
            int index = emPage * EmSlots + slot;
            if (index >= items.Count || EmSlot(slot) is not { } glow)
                break;
            // The icon inside its glow (the glow mesh is a little larger than the icon).
            var r = new Rect(glow.X + glow.Width * 0.12f, glow.Y + glow.Height * 0.12f, glow.Width * 0.76f, glow.Height * 0.76f);
            var item = items[index];
            if (item.Power != null)
            {
                float charge = PowerCharge?.Invoke(item.Power, item.Factory) ?? 1f;
                ui.Images.Draw(batch, item.Image, r);
                if (charge < 1f)
                    batch.FillRect(new Rect(r.X, r.Y + r.Height * charge, r.Width, r.Height * (1 - charge)), new Vector4(0, 0, 0, 0.55f));
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
            {
                float progress = Math.Clamp(first.Progress, 0, 1);
                batch.FillRect(new Rect(r.X, r.Y + r.Height * progress, r.Width, r.Height * (1 - progress)), new Vector4(0, 0, 0, 0.55f));
            }
            if (entries.Count > 1)
                font.DrawWithShadow(batch, entries.Count.ToString(), new Vector2(r.X + r.Width - 10 * emScale, r.Y + 2), Vector4.One, new Vector4(0, 0, 0, 1));
        }
        if (pages > 1)
        {
            string page = $"{emPage + 1}/{pages}";
            if (EmRect("back34") is { } back)
                font.DrawWithShadow(batch, page, new Vector2(back.X + back.Width - font.Measure(page) - 2, back.Y + back.Height + 1), new Vector4(0.8f, 0.9f, 1, 1), new Vector4(0, 0, 0, 1));
        }

        // The credits, in the number box under the radar.
        if (EmRect("numberbackground") is { } box)
        {
            var creditFont = ui.Fonts.Get("Arial", Math.Max(8, (int)(box.Height * 0.8f)), bold: true);
            string money = player.Money.Amount.ToString();
            float tw = creditFont.Measure(money);
            creditFont.DrawWithShadow(batch, money, new Vector2(MathF.Round(box.X + (box.Width - tw) / 2), MathF.Round(box.Y + box.Height * 0.05f)),
                new Vector4(1, 0.85f, 0.45f, 1), new Vector4(0, 0, 0, 1));
        }
    }
}
