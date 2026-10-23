using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class ConvexSweepOracleTests
{
    static readonly ConvexQueryShape Shape = new(ConvexQueryKind.ConeCylinder, 0.25f, 0.25f, 0.5f, 0.75f);

    [Fact]
    public void NonzeroEntryEnclosesFirstContactAndDyadicTangency()
    {
        using var face = new ConvexPlanarFixture(new(0.03125f, 1, 1), Pose.At(new(0.78125f, 0.625f, 0)));
        var entry = ShortBodyShellOracle.Entry(Shape, Pose.Identity, face.Oracle, Vector3.UnitX);
        Assert.True(entry.IsComplete);
        Assert.True(entry.Lower <= 0.5 && entry.Upper >= 0.5);
        using var tangent = new ConvexPlanarFixture(new(0.03125f, 0.0625f, 0.03125f), Pose.At(new(0.28125f, 0.625f, 0.03125f)));
        entry = ShortBodyShellOracle.Entry(Shape, Pose.At(new(0, 0, -0.5f)), tangent.Oracle, Vector3.UnitZ);
        Assert.True(entry.IsComplete);
        Assert.Equal(0.5, entry.Lower);
        Assert.Equal(0.5, entry.Upper);
    }

    [Fact]
    public void IndependentSweepHandlesAThinBlockerAndTheRequestedMargin()
    {
        using var thin = new ConvexPlanarFixture(new(0.00001f, 0.03125f, 0.00001f), Pose.At(new(0.14995f, 0.625f, 0.19995f)));
        var entry = ShortBodyShellOracle.Entry(Shape, Pose.At(new(-0.5f, 0, 0)), thin.Oracle, Vector3.UnitX);
        Assert.True(entry.IsComplete);
        Assert.True(entry.Upper < 0.5);
        using var wall = new ConvexPlanarFixture(new(0.03125f, 1, 1), Pose.At(new(0.78125f, 0.625f, 0)));
        var grown = new ConvexQueryShape(Shape.Kind, Shape.Radius, Shape.LowerY, Shape.RingY, Shape.UpperY, 0.001f);
        entry = ShortBodyShellOracle.Entry(grown, Pose.Identity, wall.Oracle, Vector3.UnitX);
        Assert.True(entry.IsComplete);
        Assert.True(entry.Lower <= 0.5 - (double)0.001f && entry.Upper >= 0.5 - (double)0.001f);
    }
}
