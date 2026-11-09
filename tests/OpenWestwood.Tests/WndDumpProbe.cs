using OpenWestwood.Client.UI.Gui;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

public class WndDumpProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void Dump()
    {
        string? path = Environment.GetEnvironmentVariable("WND_DUMP");
        if (path == null || !content.Available) return;
        var layout = GuiLayout.Load(content.FileSystem!, path);
        void Walk(GuiWindow w, int depth)
        {
            output.WriteLine($"{new string(' ', depth * 2)}{w.GetType().Name} {w.Name} {w.Definition.X},{w.Definition.Y} {w.Definition.Width}x{w.Definition.Height} status [{string.Join(",", w.Definition.Status)}] text '{w.Definition.Text}'");
            foreach (var c in w.Children) Walk(c, depth + 1);
        }
        foreach (var r in layout.Roots) Walk(r, 0);
    }
}
