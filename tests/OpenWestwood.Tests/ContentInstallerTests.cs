using OpenWestwood.Core.Installs;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>
/// The installer copies only the original files: a copy with modified ones (a community patch) can't supply them, a
/// clean one installs the game, which then counts as there. INSTALL_TEST=1 (it copies a few GB).
/// </summary>
[Collection("GameInstalls")]
public class ContentInstallerTests(ITestOutputHelper output)
{
    [Fact]
    public void OnlyOriginalFilesAreInstalled()
    {
        if (Environment.GetEnvironmentVariable("INSTALL_TEST") == null)
            return;
        string realRoot = GameInstalls.ContentRoot;
        string temp = Path.Combine(Path.GetTempPath(), "ow-install-test");
        try
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
            Directory.CreateDirectory(Path.Combine(temp, "mods", "zh"));
            File.Copy(Path.Combine(realRoot, "mods", "zh", "original-files.txt"), Path.Combine(temp, "mods", "zh", "original-files.txt"));
            GameInstalls.ContentRoot = temp;
            Assert.False(GameInstalls.Has(OriginalGame.ZeroHour));

            // The player's own install (its Window archives patched): refused, nothing half done.
            const string origin = @"C:\Program Files (x86)\Origin Games\Command and Conquer Generals Zero Hour\Command and Conquer Generals Zero Hour";
            if (Directory.Exists(origin))
            {
                GameInstalls.Override(zeroHour: origin);
                var refused = ContentInstaller.Install(OriginalGame.ZeroHour);
                output.WriteLine($"from the Origin install: {refused.Success} - {refused.Message} [{string.Join("; ", refused.Problems)}]");
                Assert.False(refused.Success);
                Assert.Contains(refused.Problems, p => p.Contains("WindowZH.big"));
            }

            // A clean copy (OpenWESTWOOD's own, here): installed.
            GameInstalls.Override(zeroHour: Path.Combine(realRoot, "content", "zerohour"));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = ContentInstaller.Install(OriginalGame.ZeroHour);
            output.WriteLine($"from a clean copy: {result.Success} in {sw.Elapsed.TotalSeconds:0}s - {result.Message} [{string.Join("; ", result.Problems.Take(5))}]");
            Assert.True(result.Success);
            Assert.True(GameInstalls.Has(OriginalGame.ZeroHour));
        }
        finally
        {
            GameInstalls.ContentRoot = realRoot;
            GameInstalls.ClearOverrides();
            try { Directory.Delete(temp, true); } catch (IOException) { }
        }
    }
}
