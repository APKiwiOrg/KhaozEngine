using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;
using static KhaozEngine.Tests.Terrain.TileFoliageReleasedFixtures;

namespace KhaozEngine.Tests.Terrain;

/// <summary>Freezes the released tile foliage sampler and corner normals over invented worlds, using only the
/// released Sample and CornerNormal entry points.</summary>
public sealed class TileFoliageReleasedBaselineTests
{
    public static TheoryData<string> All() => Data(Names());

    [Theory]
    [MemberData(nameof(All))]
    public void ReleasedTileFoliageBits_MatchPinnedSource(string name)
    {
        TileFoliageReleasedCase item = Case(name);
        (List<uint[]> samples, List<uint[]> corners) = Measure(item);

        Assert.Equal(item.Points.Count, item.SampleBits.Count);
        Assert.Equal(item.Corners.Count, item.CornerBits.Count);
        Assert.Equal(item.SampleBits, samples);
        Assert.Equal(item.CornerBits, corners);
    }

    public static TheoryData<string> Rejections() => Data(RejectionCases);

    [Theory]
    [MemberData(nameof(Rejections))]
    public void RejectionCase_IsolatesOnePredicate(string name)
    {
        TileFoliageReleasedCase item = Case(name);
        TileFoliageReleasedCase control = Control(name);
        TileFoliageSurface surface = item.Surface();
        TileFoliageSurface open = control.Surface();

        Assert.Equal(name, item.Rejects);
        Assert.Equal(0f, Sample(surface, item.Points[0]).Density);
        Assert.Equal(0f, Sample(surface, item.Points[1]).Density);
        Assert.True(Sample(surface, item.Points[2]).Density > 0f);
        Assert.True(Sample(open, item.Points[0]).Density > 0f);
        Assert.True(Sample(open, item.Points[1]).Density > 0f);
        if (name == "absent-region") return;
        foreach (Vector2 point in item.Points)
            Assert.Equal(Ground(Sample(open, point)), Ground(Sample(surface, point)));
    }

    public static TheoryData<string> Ignored() => Data(IgnoredCases);

    [Theory]
    [MemberData(nameof(Ignored))]
    public void IgnoredPredicate_KeepsControlBits(string name)
    {
        TileFoliageReleasedCase item = Case(name);
        TileFoliageSurface surface = item.Surface();
        TileFoliageSurface open = Control(name).Surface();

        Assert.Null(item.Rejects);
        foreach (Vector2 point in item.Points)
        {
            GroundCoverSample sample = Sample(surface, point);
            Assert.True(sample.Density > 0f);
            Assert.Equal(Row(Sample(open, point)), Row(sample));
        }
    }

    [Fact]
    public void ZeroRasterAndSolidOverlap_PinsPrecedence()
    {
        TileFoliageReleasedCase overlap = Case("zero-raster-solid");
        TileFoliageSurface both = overlap.Surface();
        TileFoliageSurface raster = Case("zero-raster").Surface();
        TileFoliageSurface solid = Case("solid").Surface();

        for (int i = 0; i < 2; i++)
        {
            Vector2 point = overlap.Points[i];
            Assert.Equal(0f, Sample(both, point).Density);
            Assert.Equal(Row(Sample(raster, point)), Row(Sample(both, point)));
            Assert.Equal(Row(Sample(solid, point)), Row(Sample(both, point)));
        }
        Assert.Equal(Row(Sample(raster, overlap.Points[2])), Row(Sample(both, overlap.Points[2])));
    }

    static (List<uint[]> Samples, List<uint[]> Corners) Measure(TileFoliageReleasedCase item)
    {
        TileFoliageSurface surface = item.Surface();
        var samples = new List<uint[]>();
        foreach (Vector2 point in item.Points) samples.Add(Row(Sample(surface, point)));
        var corners = new List<uint[]>();
        foreach (TileFoliageReleasedCorner corner in item.Corners)
        {
            Vector3 normal = TileGroundMesher.CornerNormal(item.Document, corner.X, corner.Z, corner.Plane);
            corners.Add([Bits(normal.X), Bits(normal.Y), Bits(normal.Z)]);
        }
        return (samples, corners);
    }

    static TheoryData<string> Data(IReadOnlyList<string> names)
    {
        var data = new TheoryData<string>();
        foreach (string name in names) data.Add(name);
        return data;
    }

    static GroundCoverSample Sample(TileFoliageSurface surface, Vector2 point) => surface.Sample(point.X, point.Y);

    static uint[] Row(GroundCoverSample sample) =>
        [Bits(sample.Height), Bits(sample.Normal.X), Bits(sample.Normal.Y), Bits(sample.Normal.Z), Bits(sample.Density)];

    static uint[] Ground(GroundCoverSample sample) => Row(sample)[..4];
}
