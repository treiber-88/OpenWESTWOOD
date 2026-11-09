using Xunit;

namespace OpenWestwood.Tests;

/// <summary>Probe (TD_DUMP=folder): writes Tiberian Dawn's campaign scenario INIs there, to read.</summary>
public class TdScenarioDump
{
    [Fact]
    public void Dump()
    {
        if (Environment.GetEnvironmentVariable("TD_DUMP") is not { Length: > 0 } folder || OpenWestwood.Client.TiberianDawnContent.Get() is not { } td)
            return;
        Directory.CreateDirectory(folder);
        foreach (string name in OpenWestwood.TiberianDawn.Data.TdScenario.List(td.Files))
            File.WriteAllBytes(Path.Combine(folder, name + ".INI"), td.Files.ReadAllBytes(name + ".INI"));
    }
}
