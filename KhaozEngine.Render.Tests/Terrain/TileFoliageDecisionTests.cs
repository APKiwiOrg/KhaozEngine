using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;
using static KhaozEngine.Tests.Terrain.TileFoliageReleasedFixtures;

namespace KhaozEngine.Tests.Terrain;

/// <summary>The additive legacy diagnostic seam. The preservation oracle is the literal bits Task 3 froze from the
/// released sampler at f1cd6656b. Equality between the wrapper and the detailed answer only checks routing.</summary>
public sealed class TileFoliageDecisionTests
{
    public static TheoryData<string> All()
    {
        var data = new TheoryData<string>();
        foreach (string name in Names()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(All))]
    public void LegacySurface_DetailedAndWrapperMatchFrozenReleasedBits(string name)
    {
        TileFoliageReleasedCase item = Case(name);
        TileFoliageSurface surface = item.Surface();
        var frozenReleasedSampleBits = new List<uint[]>();
        var detailedSampleBits = new List<uint[]>();
        for (int i = 0; i < item.Points.Count; i++)
        {
            Vector2 point = item.Points[i];
            TileFoliageDecision detailed = surface.SampleDetailed(point.X, point.Y);
            GroundCoverSample legacySample = surface.Sample(point.X, point.Y);
            frozenReleasedSampleBits.Add(item.SampleBits[i]);
            detailedSampleBits.Add(Row(detailed.Sample));
            Assert.Equal(legacySample, detailed.Sample);
            // A detailed answer names a stage exactly when the released density is zero.
            Assert.Equal(detailed.Sample.Density > 0f, detailed.Reason == TileFoliageRejection.None);
        }
        Assert.Equal(frozenReleasedSampleBits, detailedSampleBits);
    }

    public static TheoryData<string, TileFoliageRejection, ushort> Stages() => new()
    {
        { "void-underlay", TileFoliageRejection.VoidUnderlay, 0 },
        { "allowed-underlay", TileFoliageRejection.UnderlayNotAllowed, 2 },
        { "water", TileFoliageRejection.Water, 4 },
        { "indoors", TileFoliageRejection.Indoors, 1 },
        { "solid", TileFoliageRejection.Solid, 1 },
        { "roof", TileFoliageRejection.Roof, 1 },
        { "door", TileFoliageRejection.Door, 1 },
        { "zero-raster", TileFoliageRejection.ZeroRaster, 0 },
        { "absent-region", TileFoliageRejection.AbsentRegion, 0 },
        { "zero-raster-solid", TileFoliageRejection.ZeroRaster, 0 },
    };

    [Theory]
    [MemberData(nameof(Stages))]
    public void RejectionCase_NamesItsReleasedStageAndVisibleMaterial(string name, TileFoliageRejection stage, ushort visible)
    {
        TileFoliageReleasedCase item = Case(name);
        TileFoliageSurface surface = item.Surface();

        TileFoliageDecision first = surface.SampleDetailed(item.Points[0].X, item.Points[0].Y);
        TileFoliageDecision open = surface.SampleDetailed(item.Points[2].X, item.Points[2].Y);

        Assert.Equal(stage, first.Reason);
        Assert.Equal(visible, first.VisibleMaterial);
        Assert.Equal(0f, first.Sample.Density);
        Assert.Equal(TileFoliageRejection.None, open.Reason);
    }

    [Theory]
    [InlineData("nodraw")]
    [InlineData("blocked")]
    [InlineData("edge-fade")]
    public void IgnoredOrFadedPredicate_PassesEveryStage(string name)
    {
        TileFoliageReleasedCase item = Case(name);
        TileFoliageSurface surface = item.Surface();
        foreach (Vector2 point in item.Points)
        {
            TileFoliageDecision decision = surface.SampleDetailed(point.X, point.Y);
            Assert.Equal(TileFoliageRejection.None, decision.Reason);
            Assert.Equal((ushort)1, decision.VisibleMaterial);
        }
    }

    [Fact]
    public void ZeroEdgeFactor_NamesTheFadeStage()
    {
        // Tile (31, 30) is dirt where the layer allows grass only, and its fade is 0.5 m over 1 m tiles. On the west rim
        // of tile (32, 30) the released edge factor toward it is exactly zero while every earlier stage passes.
        TileFoliageReleasedCase item = Case("edge-fade");
        TileFoliageSurface surface = item.Surface();
        float x = TileWorldSpace.WorldX(32f, 1f), z = TileWorldSpace.WorldZ(30.5f, 1f);

        TileFoliageDecision decision = surface.SampleDetailed(x, z);

        Assert.Equal(surface.Sample(x, z), decision.Sample);
        Assert.Equal(TileFoliageRejection.EdgeFade, decision.Reason);
        Assert.Equal((ushort)1, decision.VisibleMaterial);
        Assert.Equal(0f, decision.Sample.Density);
    }

    public static TheoryData<string> Cuts()
    {
        var data = new TheoryData<string>();
        foreach (string name in Names())
            if (name.StartsWith("cut-", System.StringComparison.Ordinal)) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cuts))]
    public void CutCase_ReadsTheVisibleMaterialAtThePoint(string name)
    {
        // Every raster byte is nonzero and the layer allows grass only, so a frozen zero density means the point lies
        // on the road overlay and a positive one means it lies on grass.
        TileFoliageReleasedCase item = Case(name);
        TileFoliageSurface surface = item.Surface();
        for (int i = 0; i < item.Points.Count; i++)
        {
            bool road = item.SampleBits[i][4] == Bits(0f);
            TileFoliageDecision decision = surface.SampleDetailed(item.Points[i].X, item.Points[i].Y);
            Assert.Equal(road ? (ushort)6 : (ushort)1, decision.VisibleMaterial);
            Assert.Equal(road ? TileFoliageRejection.UnderlayNotAllowed : TileFoliageRejection.None, decision.Reason);
        }
    }

    static uint[] Row(GroundCoverSample sample) =>
        [Bits(sample.Height), Bits(sample.Normal.X), Bits(sample.Normal.Y), Bits(sample.Normal.Z), Bits(sample.Density)];
}
