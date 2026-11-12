namespace OpenWestwood.Client.SoleSurvivor;

/// <summary>
/// The player's Sole Survivor Remix record, kept between games (SoleSurvivor.ini in the user's OpenWESTWOOD folder): the
/// credits earned by kills, the units bought (the cheap ones are free from the start) and the weapon / armour / speed
/// upgrades bought (they apply to every unit), and the setup last used.
/// </summary>
public sealed class SsrProfile
{
    public int Credits { get; set; }
    public HashSet<string> Unlocked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int WeaponLevel { get; set; }
    public int ArmorLevel { get; set; }
    public int SpeedLevel { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public Dictionary<string, string> Setup { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Units this cheap or cheaper need no buying.</summary>
    public const int FreeUnitCost = 700;
    public const int MaxUpgradeLevel = 5;

    public static int UpgradeCost(int level) => 400 * (level + 1);

    public bool Owns(Logic.SoleSurvivor.SsrUnit unit) => unit.Cost <= FreeUnitCost || Unlocked.Contains(unit.Template);

    public bool Buy(Logic.SoleSurvivor.SsrUnit unit)
    {
        if (Owns(unit) || Credits < unit.Cost)
            return false;
        Credits -= unit.Cost;
        Unlocked.Add(unit.Template);
        Save();
        return true;
    }

    /// <summary>Buys the next level of an upgrade (0 weapons, 1 armour, 2 speed).</summary>
    public bool BuyUpgrade(int which)
    {
        int level = which == 0 ? WeaponLevel : which == 1 ? ArmorLevel : SpeedLevel;
        if (level >= MaxUpgradeLevel || Credits < UpgradeCost(level))
            return false;
        Credits -= UpgradeCost(level);
        if (which == 0) WeaponLevel++; else if (which == 1) ArmorLevel++; else SpeedLevel++;
        Save();
        return true;
    }

    static string FilePath => Path.Combine(Shell.SkirmishPreferences.UserDataFolder, "SoleSurvivor.ini");

    static SsrProfile? current;
    public static SsrProfile Current => current ??= Load();

    static SsrProfile Load()
    {
        var profile = new SsrProfile();
        try
        {
            if (!File.Exists(FilePath))
                return profile;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                if (line.Split('=', 2) is not [var rawKey, var rawValue])
                    continue;
                string key = rawKey.Trim(), value = rawValue.Trim();
                int.TryParse(value, out int number);
                switch (key)
                {
                    case "Credits": profile.Credits = number; break;
                    case "WeaponLevel": profile.WeaponLevel = number; break;
                    case "ArmorLevel": profile.ArmorLevel = number; break;
                    case "SpeedLevel": profile.SpeedLevel = number; break;
                    case "Kills": profile.Kills = number; break;
                    case "Deaths": profile.Deaths = number; break;
                    case "Unlocked":
                        foreach (string unit in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            profile.Unlocked.Add(unit);
                        break;
                    default:
                        if (key.StartsWith("Setup.", StringComparison.OrdinalIgnoreCase))
                            profile.Setup[key[6..]] = value;
                        break;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return profile;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var lines = new List<string>
            {
                $"Credits = {Credits}", $"WeaponLevel = {WeaponLevel}", $"ArmorLevel = {ArmorLevel}", $"SpeedLevel = {SpeedLevel}",
                $"Kills = {Kills}", $"Deaths = {Deaths}", $"Unlocked = {string.Join(",", Unlocked.Order())}",
            };
            lines.AddRange(Setup.Select(s => $"Setup.{s.Key} = {s.Value}"));
            File.WriteAllLines(FilePath, lines);
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Could not save the Sole Survivor record: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"Could not save the Sole Survivor record: {e.Message}");
        }
    }
}
