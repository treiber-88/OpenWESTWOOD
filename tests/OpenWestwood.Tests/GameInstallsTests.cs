using OpenWestwood.Core.Installs;
using Xunit;

namespace OpenWestwood.Tests;

/// <summary>The install detector's folder tests, on made-up folders with the games' file names.</summary>
public class GameInstallsTests
{
    static string MakeFolder(string name, params string[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "owinstalls-" + Guid.NewGuid().ToString("N"), name);
        foreach (string f in files)
        {
            string path = Path.Combine(dir, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
        return dir;
    }

    static readonly string[] KwFiles = ["Core/1.0/StaticStream.big", "Core/1.0/GlobalStream.big", "Core/1.0/Terrain.big", "Meta/1.0/maps.big"];

    [Fact]
    public void ZeroHourAndGeneralsFoldersAreRecognised()
    {
        string zh = MakeFolder("Command and Conquer Generals Zero Hour", "INIZH.big", "W3DZH.big", "WindowZH.big", "MapsZH.big");
        string gen = MakeFolder("Command and Conquer Generals", "INI.big", "W3D.big", "Window.big", "Textures.big");
        Assert.True(GameInstalls.IsZeroHourFolder(zh));
        Assert.False(GameInstalls.IsGeneralsFolder(zh));
        Assert.True(GameInstalls.IsGeneralsFolder(gen));
        Assert.False(GameInstalls.IsZeroHourFolder(gen));
        // A folder missing one archive is not an install.
        File.Delete(Path.Combine(zh, "W3DZH.big"));
        Assert.False(GameInstalls.IsZeroHourFolder(zh));
    }

    [Fact]
    public void KanesWrathIsTellApartFromTiberiumWars()
    {
        Assert.True(GameInstalls.IsKanesWrathFolder(MakeFolder("Command Conquer 3 Kanes Wrath", KwFiles)));
        Assert.True(GameInstalls.IsKanesWrathFolder(MakeFolder("Command & Conquer 3 Kane's Wrath", KwFiles)));
        // Same layout, but Tiberium Wars.
        Assert.False(GameInstalls.IsKanesWrathFolder(MakeFolder("Command Conquer 3 Tiberium Wars", KwFiles)));
        // Any folder name, with Kane's Wrath's program in it.
        Assert.True(GameInstalls.IsKanesWrathFolder(MakeFolder("Game", [.. KwFiles, "cnc3ep1.exe"])));
        // Kane's Wrath's name but not its data.
        Assert.False(GameInstalls.IsKanesWrathFolder(MakeFolder("Kanes Wrath", "Core/1.0/StaticStream.big")));
    }

    [Fact]
    public void FileNamesMatchInAnyCase()
    {
        string dir = MakeFolder("x", "core/1.0/staticstream.BIG");
        Assert.NotNull(GameInstalls.FindFile(dir, "Core/1.0/StaticStream.big"));
        Assert.Null(GameInstalls.FindFile(dir, "Core/1.0/GlobalStream.big"));
    }
}
