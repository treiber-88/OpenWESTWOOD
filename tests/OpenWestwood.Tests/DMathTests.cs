using OpenWestwood.Core;
using Xunit;

namespace OpenWestwood.Tests;

/// <summary>The deterministic maths agree with the system's to within rounding.</summary>
public class DMathTests
{
    [Fact]
    public void MatchesTheSystemMaths()
    {
        var random = new Random(1);
        for (int i = 0; i < 200_000; i++)
        {
            double x = (random.NextDouble() - 0.5) * (i % 10 == 0 ? 20000 : 20);
            double y = (random.NextDouble() - 0.5) * 20;
            Assert.True(Math.Abs(DMath.Sin(x) - Math.Sin(x)) < 1e-12, $"sin {x}");
            Assert.True(Math.Abs(DMath.Cos(x) - Math.Cos(x)) < 1e-12, $"cos {x}");
            Assert.True(Math.Abs(DMath.Atan(x) - Math.Atan(x)) < 1e-14, $"atan {x}");
            Assert.True(Math.Abs(DMath.Atan2(y, x) - Math.Atan2(y, x)) < 1e-14, $"atan2 {y} {x}");
            double u = random.NextDouble() * 2 - 1;
            Assert.True(Math.Abs(DMath.Asin(u) - Math.Asin(u)) < 1e-12, $"asin {u}");
            Assert.True(Math.Abs(DMath.Acos(u) - Math.Acos(u)) < 1e-12, $"acos {u}");
        }
        Assert.Equal(0.0, DMath.Sin(0.0));
        Assert.Equal(1.0, DMath.Cos(0.0));
        Assert.Equal(Math.PI / 2, DMath.Atan2(1.0, 0.0));
        Assert.Equal(Math.PI, DMath.Atan2(0.0, -1.0));
    }
}
