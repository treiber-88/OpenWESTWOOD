namespace OpenWestwood.Client.Shell;

/// <summary>
/// OpenWESTWOOD: how the isometric games' maps (Tiberian Sun, Red Alert 2) are looked at, kept in View.ini in the
/// user's data folder. The Westwood view is the original one: tilted 30 degrees, with no perspective, so the map's
/// pictures look as they did in the original games. The Generals view is Zero Hour's own camera on them.
/// </summary>
public static class ViewPreferences
{
    static bool loaded;
    static bool westwoodView = true;

    static string FilePath => Path.Combine(SkirmishPreferences.UserDataFolder, "View.ini");

    /// <summary>True: the original Westwood angle on TS and RA2 maps; false: the Generals camera.</summary>
    public static bool WestwoodView
    {
        get
        {
            Load();
            return westwoodView;
        }
        set
        {
            Load();
            if (westwoodView == value)
                return;
            westwoodView = value;
            Save();
            Changed?.Invoke();
        }
    }

    /// <summary>Raised when the choice changes (a game being played turns its camera at once).</summary>
    public static event Action? Changed;

    static void Load()
    {
        if (loaded)
            return;
        loaded = true;
        try
        {
            if (!File.Exists(FilePath))
                return;
            foreach (string line in File.ReadAllLines(FilePath))
                if (line.Split('=', 2) is [var key, var value] && key.Trim().Equals("IsometricMapView", StringComparison.OrdinalIgnoreCase))
                    westwoodView = !value.Trim().Equals("Generals", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"IsometricMapView = {(westwoodView ? "Westwood" : "Generals")}\n");
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"Could not save the view choice: {e.Message}");
        }
        catch (UnauthorizedAccessException e)
        {
            Console.Error.WriteLine($"Could not save the view choice: {e.Message}");
        }
    }
}
