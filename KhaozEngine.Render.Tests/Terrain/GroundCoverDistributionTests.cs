using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public class GroundCoverDistributionTests
{
    static GroundCoverSettings Settings(float spacing = 1f) => new()
    {
        Seed = 177,
        Spacing = spacing,
        ScaleMin = 0.7f,
        ScaleMax = 1.3f,
        RootOffset = -0.04f,
        Models =
        [
            new GroundCoverModel("grass_short", 3f),
            new GroundCoverModel("grass_tall", 1f),
        ],
    };

    static GroundCoverSample Flat(float x, float z) =>
        new(2f, Vector3.UnitY, 1f);

    static string Key(GroundCoverInstance p) =>
        $"{p.ModelId}|{p.Position.X:R}|{p.Position.Y:R}|{p.Position.Z:R}|{p.ThinningRank:R}";

    [Fact]
    public void Generate_IsStableAcrossAlignedAndUnalignedPartitions()
    {
        GroundCoverSettings settings = Settings(1.25f);
        var wholeArea = new RectArea(-9.4f, -7.1f, 12.8f, 11.3f);
        IReadOnlyList<GroundCoverInstance> whole = GroundCoverDistribution.Generate(wholeArea, settings, Flat);

        IReadOnlyList<GroundCoverInstance> aligned =
            GroundCoverDistribution.Generate(new RectArea(-9.4f, -7.1f, 0f, 11.3f), settings, Flat)
                .Concat(GroundCoverDistribution.Generate(new RectArea(0f, -7.1f, 12.8f, 11.3f), settings, Flat))
                .ToArray();
        IReadOnlyList<GroundCoverInstance> unaligned =
            GroundCoverDistribution.Generate(new RectArea(-9.4f, -7.1f, 1.37f, 11.3f), settings, Flat)
                .Concat(GroundCoverDistribution.Generate(new RectArea(1.37f, -7.1f, 12.8f, 11.3f), settings, Flat))
                .ToArray();

        string[] expected = whole.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, aligned.Select(Key).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(expected, unaligned.Select(Key).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(whole, p =>
        {
            Assert.InRange(p.Position.X, wholeArea.MinX, MathF.BitDecrement(wholeArea.MaxX));
            Assert.InRange(p.Position.Z, wholeArea.MinZ, MathF.BitDecrement(wholeArea.MaxZ));
        });
    }

    [Fact]
    public void Generate_DensityZeroProducesNoCover()
    {
        IReadOnlyList<GroundCoverInstance> result = GroundCoverDistribution.Generate(
            new RectArea(-4f, -4f, 4f, 4f), Settings(),
            static (x, z) => new GroundCoverSample(0f, Vector3.UnitY, 0f));

        Assert.Empty(result);
    }

    [Fact]
    public void Generate_AlignsLocalUpToSlopedSurfaceAtNegativeCoordinates()
    {
        Vector3 slopeNormal = Vector3.Normalize(new Vector3(-0.4f, 1f, 0.25f));
        IReadOnlyList<GroundCoverInstance> result = GroundCoverDistribution.Generate(
            new RectArea(-12f, -10f, -2f, -1f), Settings(),
            (x, z) => new GroundCoverSample(3f + x * 0.4f - z * 0.25f, slopeNormal, 1f));

        Assert.NotEmpty(result);
        Assert.All(result, p =>
        {
            Vector3 transformedUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, p.Transform));
            Assert.True(Vector3.Dot(transformedUp, slopeNormal) > 0.9999f,
                $"local up {transformedUp} did not align with {slopeNormal}");
            Vector3 surfacePoint = p.Position - slopeNormal * Settings().RootOffset;
            Assert.Equal(3f + surfacePoint.X * 0.4f - surfacePoint.Z * 0.25f, surfacePoint.Y, 4);
        });
    }

    [Fact]
    public void Generate_UsesWeightedModelsAndStableNestedRanks()
    {
        IReadOnlyList<GroundCoverInstance> result = GroundCoverDistribution.Generate(
            new RectArea(-50f, -50f, 50f, 50f), Settings(), Flat);

        Assert.Contains(result, p => p.ModelId == "grass_short");
        Assert.Contains(result, p => p.ModelId == "grass_tall");
        Assert.All(result, p => Assert.InRange(p.ThinningRank, 0f, MathF.BitDecrement(1f)));
        Assert.True(result.Count(p => p.ModelId == "grass_short") > result.Count(p => p.ModelId == "grass_tall") * 2);
    }

    [Theory]
    [InlineData(0f, 0.5f, 1f)]
    [InlineData(float.NaN, 0.5f, 1f)]
    [InlineData(1f, 0f, 1f)]
    [InlineData(1f, 2f, 1f)]
    public void Generate_RejectsInvalidSpacingAndScale(float spacing, float scaleMin, float scaleMax)
    {
        GroundCoverSettings settings = Settings(spacing);
        settings.ScaleMin = scaleMin;
        settings.ScaleMax = scaleMax;

        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Generate(
            new RectArea(0f, 0f, 4f, 4f), settings, Flat));
    }

    [Fact]
    public void Generate_RejectsInvalidSurfaceSamplesAndCandidateBudgets()
    {
        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Generate(
            new RectArea(0f, 0f, 4f, 4f), Settings(),
            static (x, z) => new GroundCoverSample(float.NaN, Vector3.UnitY, 1f)));

        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Generate(
            new RectArea(0f, 0f, 1000f, 1000f), Settings(0.01f), Flat));

        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Generate(
            new RectArea(-1_100_000_000f, -1_100_000_000f, 1_100_000_000f, 1_100_000_000f),
            Settings(), Flat));

        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Generate(
            new RectArea(2_147_483_648f, 0f, 2_147_483_904f, 1f), Settings(), Flat));
    }

    [Fact]
    public void Generate_RejectsInvalidModelWeightsAndIds()
    {
        GroundCoverSettings settings = Settings();
        settings.Models = [new GroundCoverModel("", 1f)];
        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Generate(
            new RectArea(0f, 0f, 4f, 4f), settings, Flat));

        settings.Models = [new GroundCoverModel("grass", float.PositiveInfinity)];
        Assert.Throws<ArgumentException>(() => GroundCoverDistribution.Generate(
            new RectArea(0f, 0f, 4f, 4f), settings, Flat));
    }
}
