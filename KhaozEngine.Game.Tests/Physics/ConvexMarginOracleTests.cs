using System;
using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexMarginOracleTests
{
    const float M = 0.001f;
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, M, 0, M / 2, M, M);

    [Fact]
    public void MarginNormalConeRetainsExactAxialAndCircularWitnesses()
    {
        foreach (var (point, normal) in new[]
        {
            (new Vector3(0, -M, 0), new Vector3(0, -7, 0)),
            (new Vector3(2 * M, M / 2, 0), new Vector3(3, 0, 0)),
            (new Vector3(2 * M, 0.75f * M, 0), new Vector3(1, 0, 0)),
            (new Vector3(0, 2 * M, 0), new Vector3(0, 2, 0)),
            (new Vector3(M + 3 * (M / 5), M + 4 * (M / 5), 0), new Vector3(3, 4, 0))
        })
            Assert.True(ShortBodyShellOracle.NormalConeContains(Shape, point, normal), $"{point}/{normal}");
        var cone = new ConvexQueryShape(ConvexQueryKind.ConeCylinder, 4 * (M / 5), 0, 3 * (M / 5), 6 * (M / 5), M);
        Assert.True(ShortBodyShellOracle.NormalConeContains(cone, new(M, -M / 2, 0), new(3, -4, 0)));
    }

    [Fact]
    public void MarginOracleDoesNotRepairAdjacentFloatOrWrongConeWitnesses()
    {
        Assert.False(ShortBodyShellOracle.NormalConeContains(Shape, new(MathF.BitIncrement(2 * M), M / 2, 0), Vector3.UnitX));
        Assert.False(ShortBodyShellOracle.NormalConeContains(Shape, new(MathF.BitDecrement(2 * M), M / 2, 0), Vector3.UnitX));
        Assert.False(ShortBodyShellOracle.NormalConeContains(Shape, new(2 * M, M / 2, 0), Vector3.UnitY));
        Assert.False(ShortBodyShellOracle.NormalConeContains(Shape, new(0, 2 * M, 0), Vector3.UnitX));
        Assert.True(ShortBodyShellOracle.NormalConeContains(Shape, Vector3.Zero, Vector3.Zero));
        Assert.True(ShortBodyShellOracle.NormalConeContains(Shape, new(2 * M, M / 2, 0), Vector3.Zero));
        Assert.False(ShortBodyShellOracle.NormalConeContains(Shape, new(4 * M, 0, 0), Vector3.Zero));
    }
}
