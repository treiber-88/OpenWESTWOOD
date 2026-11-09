using OpenWestwood.Client.InGame;
using OpenWestwood.Client.UI;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Scripting;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace OpenWestwood.Client.Shell;

/// <summary>Builds games from maps and the game screens to play them in (missions and skirmishes).</summary>
public sealed class GameLauncher(GL gl, IInputContext input, IWindow window, UiResources ui)
{
    MapFile ReadMap(string map) => TiberianDawn.TdMaps.Read(ui.Content.FileSystem, map);

    byte[]? ReadFile(string path) => ui.Content.FileSystem.Exists(path) ? ui.Content.FileSystem.ReadAllBytes(path) : null;

    /// <summary>A campaign or challenge mission: the map's own players and scripts.</summary>
    public GameScreen StartMission(string map, Difficulty difficulty)
    {
        var mapFile = ReadMap(map);
        var game = GameSetup.FromMap(mapFile, ui.Content.Rules, (ulong)Environment.TickCount64, difficulty: difficulty,
            models: ui.Content.Models, readFile: ReadFile);
        PreloadArt(game, map);
        return new GameScreen(gl, input, ui, game, mapFile) { MapPath = map };
    }

    /// <summary>
    /// A saved game: a fresh game on the save's map (for the rules and terrain the save points at), then the saved state
    /// in place of its own.
    /// </summary>
    public (GameScreen Screen, Logic.Saving.SaveHeader Header, object? Attachment) LoadGame(string path)
    {
        using var file = File.OpenRead(path);
        var header = Logic.Saving.SaveGame.ReadHeader(file);
        file.Position = 0;
        if (!ui.Content.FileSystem.Exists(header.Map) && !header.Map.Contains(':'))
            throw new Logic.Saving.SaveGameException($"The map of this saved game ({header.Map}) isn't installed.");
        var mapFile = ReadMap(header.Map);
        var game = GameSetup.FromMap(mapFile, ui.Content.Rules, 0, models: ui.Content.Models, readFile: ReadFile);
        Logic.Saving.SaveGame.Load(file, game, mapFile, out var attachment);
        PreloadArt(game, header.Map);
        return (new GameScreen(gl, input, ui, game, mapFile) { MapPath = header.Map }, header, attachment);
    }

    /// <summary>A skirmish (or LAN) game: one side per slot plus the standard skirmish scripts.</summary>
    public GameScreen StartSkirmish(string map, IReadOnlyList<PlayerSlot> slots, int startingCash, ulong? seed = null)
    {
        var mapFile = ReadMap(map);
        var game = GameSetup.FromMap(mapFile, ui.Content.Rules, seed ?? (ulong)Environment.TickCount64, slots, startingCash,
            models: ui.Content.Models, readFile: ReadFile);
        PreloadArt(game, map);
        return new GameScreen(gl, input, ui, game, mapFile) { MapPath = map };
    }

    /// <summary>A skirmish set up in the menus: random choices are settled as the game starts.</summary>
    public GameScreen StartSkirmish(Logic.Multiplayer.GameInfo info, Logic.Multiplayer.MapCache mapCache)
    {
        var mapFile = ReadMap(info.Map);
        var game = GameSetup.FromGameInfo(mapFile, ui.Content.Rules, info, mapCache, ui.Content.Models, ReadFile);
        PreloadArt(game, info.Map);
        return new GameScreen(gl, input, ui, game, mapFile) { MapPath = info.Map };
    }

    /// <summary>The other games' art this match needs, read while it loads rather than mid-game (see ArtPreloader).</summary>
    void PreloadArt(GameLogic game, string map) => ArtPreloader.PreloadFor(game, ui.Content, map);

    /// <summary>The game screen for a game built elsewhere (a network game).</summary>
    public GameScreen ScreenFor(GameLogic game, MapFile map, string mapPath = "") => new(gl, input, ui, game, map) { MapPath = mapPath };

    public IWindow Window => window;
}
