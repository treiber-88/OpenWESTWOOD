using OpenWestwood.Client.Network;
using Xunit;

namespace OpenWestwood.Tests;

public class NetFingerprintTests
{
    /// <summary>A copy of the mods folder with Windows line endings (as git or a zip tool may give) still matches; an edit doesn't.</summary>
    [Fact]
    public void LineEndingsDontCountButEditsDo()
    {
        string root = Path.Combine(Path.GetTempPath(), "ow-fp-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Make(string name, string text)
            {
                string dir = Path.Combine(root, name, "mods", "zh", "rules");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "a.yaml"), text);
                File.WriteAllText(Path.Combine(dir, "b.yaml"), "B:\n\tx: 1\n");
                return Path.Combine(root, name);
            }
            string lf = Make("lf", "A:\n\tSpeed: 30\n\tArmor: 2\n");
            string crlf = Make("crlf", "A:\r\n\tSpeed: 30\r\n\tArmor: 2\r\n");
            string edited = Make("edited", "A:\n\tSpeed: 31\n\tArmor: 2\n");
            Assert.Equal(NetHost.ModFiles(lf).Hash, NetHost.ModFiles(crlf).Hash);
            Assert.NotEqual(NetHost.ModFiles(lf).Hash, NetHost.ModFiles(edited).Hash);
            Assert.Equal(2, NetHost.ModFiles(lf).Files.Count);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MismatchSaysWhatDiffers()
    {
        Assert.Contains("OpenWESTWOOD.exe", NetHost.Mismatch("4/AAAA/1111", "4/BBBB/1111"));
        Assert.Contains("mods folder", NetHost.Mismatch("4/AAAA/1111", "4/AAAA/2222"));
    }
}
