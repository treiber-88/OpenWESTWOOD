using System.Text;

namespace OpenWestwood.Tools;

/// <summary>
/// The burnt-out husks the Westwood games' vehicles leave (an OpenWESTWOOD addition, as Zero Hour's tanks leave their
/// wrecks): run over an exporter's finished objects.yaml text, it gives every ground vehicle (KindOf VEHICLE, not
/// AIRCRAFT) a WestwoodHuskDie module and adds its NAME_Husk object: the vehicle's own Draw module marked "Husk: Yes"
/// (drawn burnt, standing still, on fire), there for 20 to 25 seconds, then fading away; and for a turreted one its
/// NAME_HuskTurret, the turret alone ("Husk: Turret"), which the death may throw off.
/// </summary>
public static class WestwoodHusks
{
    const string HuskModules =
        "      ModuleTag_02:\n        Module: Behavior\n        Class: LifetimeUpdate\n        MinLifetime: 20000\n        MaxLifetime: 25000\n"
        + "      ModuleTag_03:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 2000\n"
        + "      ModuleTag_04:\n        Module: Behavior\n        Class: WestwoodHusk\n";

    /// <summary>A blown-off turret: thrown clear (PhysicsBehavior), burning a little smaller.</summary>
    const string TurretModules =
        "      ModuleTag_02:\n        Module: Behavior\n        Class: LifetimeUpdate\n        MinLifetime: 20000\n        MaxLifetime: 25000\n"
        + "      ModuleTag_03:\n        Module: Behavior\n        Class: SlowDeathBehavior\n        DeathTypes: ALL\n        DestructionDelay: 2000\n"
        + "      ModuleTag_04:\n        Module: Behavior\n        Class: WestwoodHusk\n        FireParticleSystem: FireSmallContinuous\n"
        + "      ModuleTag_05:\n        Module: Behavior\n        Class: PhysicsBehavior\n        Mass: 1.0\n        AllowBouncing: Yes\n";

    /// <summary>"Vehicles" that are no machines (Tiberian Dawn's dinosaurs) or blow themselves up whole (the hunter-seekers).</summary>
    static bool NoHusk(string name) =>
        name.EndsWith("_STEG") || name.EndsWith("_RAPT") || name.EndsWith("_TREX") || name.EndsWith("_TRIC") || name.EndsWith("HUNTER");

    public static string Add(string yaml)
    {
        string nl = yaml.Contains("\r\n") ? "\r\n" : "\n";
        var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
        var husks = new StringBuilder();
        // Object blocks: "  NAME:" lines under "Objects:".
        var starts = new List<int>();
        bool inObjects = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                inObjects = line.TrimEnd() == "Objects:";
            else if (inObjects && line.StartsWith("  ") && !line.StartsWith("   ") && line.TrimEnd().EndsWith(':'))
                starts.Add(i);
        }
        // From the last block back, so inserted lines don't move the blocks still to do.
        for (int s = starts.Count - 1; s >= 0; s--)
        {
            int start = starts[s];
            int end = s + 1 < starts.Count ? starts[s + 1] : lines.Count;
            for (int i = start + 1; i < end; i++)
                if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]))
                {
                    end = i;
                    break;
                }
            string name = lines[start].Trim().TrimEnd(':');
            var block = lines.GetRange(start, end - start);
            string? kindOf = block.FirstOrDefault(l => l.StartsWith("    KindOf:"));
            if (kindOf == null)
                continue;
            var kinds = kindOf["    KindOf:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!kinds.Contains("VEHICLE") || kinds.Contains("AIRCRAFT") || name.EndsWith("_Husk") || NoHusk(name))
                continue;
            // The Draw module: from its "      ModuleTag_..:" line to the next module.
            int modules = block.FindIndex(l => l.TrimEnd() == "    Modules:");
            int draw = block.FindIndex(l => l.Trim() == "Module: Draw");
            if (modules < 0 || draw < 1)
                continue;
            int drawStart = draw - 1, drawEnd = draw + 1;
            while (drawEnd < block.Count && block[drawEnd].StartsWith("        "))
                drawEnd++;
            var drawLines = block.GetRange(drawStart, drawEnd - drawStart);
            drawLines[0] = "      ModuleTag_01:";

            husks.Append($"  {name}_Husk:\n    EditorSorting: DEBRIS\n    KindOf: NO_COLLIDE HULK INERT\n");
            if (block.FirstOrDefault(l => l.StartsWith("    TdArtOffset:")) is { } offset)
                husks.Append(offset).Append('\n');
            husks.Append("    Modules:\n");
            foreach (string l in drawLines)
                husks.Append(l).Append('\n');
            husks.Append("        Husk: Yes\n").Append(HuskModules);
            // A turret (the AI's Turret block): it may be blown off, as its own piece.
            bool turreted = block.Any(l => l.TrimEnd() == "        Turret:");
            if (turreted)
            {
                husks.Append($"  {name}_HuskTurret:\n    EditorSorting: DEBRIS\n    KindOf: NO_COLLIDE HULK INERT\n");
                if (block.FirstOrDefault(l => l.StartsWith("    TdArtOffset:")) is { } turretOffset)
                    husks.Append(turretOffset).Append('\n');
                husks.Append("    Modules:\n");
                foreach (string l in drawLines)
                    husks.Append(l).Append('\n');
                husks.Append("        Husk: Turret\n").Append(TurretModules);
            }

            lines.InsertRange(start + modules + 1,
            [
                "      ModuleTag_Husk:",
                "        Module: Behavior",
                "        Class: WestwoodHuskDie",
                "        DeathTypes: ALL -CRUSHED",
                $"        Husk: {name}_Husk",
                .. (turreted ? new[] { $"        TurretPiece: {name}_HuskTurret" } : []),
            ]);
        }
        if (husks.Length == 0)
            return yaml;
        // The husks go at the end of the Objects section.
        int objectsEnd = lines.Count;
        bool inside = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                if (inside)
                {
                    objectsEnd = i;
                    break;
                }
                inside = line.TrimEnd() == "Objects:";
            }
        }
        while (objectsEnd > 0 && lines[objectsEnd - 1].Length == 0)
            objectsEnd--;
        lines.Insert(objectsEnd, "  # The vehicles' husks (OpenWESTWOOD, see WestwoodHusks)." + "\n" + husks.ToString().TrimEnd('\n'));
        return string.Join("\n", lines).Replace("\n", nl);
    }
}
