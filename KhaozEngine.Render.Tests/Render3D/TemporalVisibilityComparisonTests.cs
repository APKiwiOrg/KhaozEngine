using System;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalVisibilityComparisonTests
{
    [Fact]
    public void ScalarOracleMatchesHandCalculatedFootprintsAndStrictThresholds()
    {
        foreach (VisibilityCase c in TemporalVisibilityReference.Cases())
        {
            (bool valid, float change) = TemporalVisibilityReference.Compare(c);
            Assert.True(valid == c.Valid, c.Name);
            if (!valid) continue;
            Assert.True(MathF.Abs(change - c.Want) < 1e-6f, $"{c.Name}: {change:R} != {c.Want:R}");
            Assert.Equal(MathF.Abs(c.Want) > 0.1f, MathF.Abs(change) > 0.1f);
        }
    }
}
