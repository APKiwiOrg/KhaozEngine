using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class MapCapacityDemandBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MetadataProbe_PreservesPredicateAndBaseline(bool scratch, bool refused)
    {
        var scan = new MapSceneScan(new(MaxScannedBytes: 10, MaxMetadataBytes: 10));
        scan.Scan(7);
        scan.RetainBytes(7);
        using var probe = new CapacityDemandProbe();
        Action run = () => { if (scratch) scan.Scratch(refused ? 4 : 3); else scan.Scan(refused ? 4 : 3); };
        if (refused) Assert.Throws<MapSceneCapacityException>(run); else run();
        var row = Assert.Single(probe.For(scratch ? "MaxMetadataBytes" : "MaxScannedBytes"));
        Assert.Equal(7, Read<decimal>(row, "CurrentDemand"));
        Assert.Equal(refused ? 4 : 3, Read<decimal>(row, "ProposedIncrement"));
        Assert.Equal(10, Read<decimal>(row, "Allowance"));
        Assert.Equal(refused, Read<bool>(row, "Refused"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugeFoliageCellRange_RefusesCapacityWithoutDiagnosticOverflow(bool observed)
    {
        var fixture = FoliageFixtures.Create();
        var layer = FoliageComparisonFixtures.Change(fixture.Document.Foliage!.Layers[0], json => json["cellSize"] = MathF.Pow(2, 44));
        var view = MapScopedSurfaces.CompleteView(fixture.Document.Surfaces);
        var builder = new MapFoliageSupportBuilder(view, new System.Collections.Generic.Dictionary<string, MapPaintRegionSet>(),
            view.Surfaces, layer, default, new(), null);
        using var probe = observed ? new CapacityDemandProbe() : null;
        var error = Assert.Throws<TargetInvocationException>(() => typeof(MapFoliageSupportBuilder)
            .GetMethod("Grid", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(builder,
                new object[] { view.Surfaces.First(s => s.Role == MapSurfaceRole.SupportFloor),
                    new MapExactXz(MapExactValue.FromSingle(0), MapExactValue.FromSingle(0)),
                    new MapExactXz(MapExactValue.FromSingle(MathF.Pow(2, 49)), MapExactValue.FromSingle(MathF.Pow(2, 49))) }));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, Assert.IsType<MapFoliageRefusal>(error.InnerException).Status);
        if (!observed) return;
        var row = Assert.Single(probe!.For("MaxFaces"));
        Assert.Equal(decimal.MaxValue, Read<decimal>(row, "Requested"));
        Assert.True(Read<bool>(row, "Saturated"));
        Assert.True(Read<bool>(row, "Refused"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstanceMinimum_PublishesEachOriginalAllowance(bool samplesIsSmaller)
    {
        var layer = FoliageFixtures.BasicLayer();
        using var builder = new MapFoliageInstanceBuilder(layer.Id, layer.Policy, default,
            new(MaxSamples: samplesIsSmaller ? 1 : 100, MaxQuerySamples: samplesIsSmaller ? 100 : 1));
        using var probe = new CapacityDemandProbe();
        Assert.True(builder.TryPrepare(null, "reed-a"));
        builder.AppendPrepared(0, 0, new("reed-a", Vector3.Zero, Matrix4x4.Identity, 0));
        Assert.False(builder.TryPrepare(null, "reed-a"));
        foreach (string limit in new[] { "MaxSamples", "MaxQuerySamples" })
        {
            bool smaller = (limit == "MaxSamples") == samplesIsSmaller;
            var rows = probe.For(limit).ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal(new decimal[] { 0, 1 }, rows.Select(r => Read<decimal>(r, "CurrentDemand")));
            Assert.All(rows, r => Assert.Equal(1, Read<decimal>(r, "ProposedIncrement")));
            Assert.All(rows, r => Assert.Equal(smaller ? 1 : 100, Read<decimal>(r, "Allowance")));
            Assert.False(Read<bool>(rows[0], "Refused"));
            Assert.Equal(smaller, Read<bool>(rows[1], "Refused"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaterSingleCell_PublishesBeforeNegativeSentinel(bool refused)
    {
        var (view, footprint) = BoundFaceContextFixtures.EightByEight();
        using var probe = new CapacityDemandProbe();
        var result = MapWaterBoundReads.Read(view, footprint, footprint.Lower, new(MapExactValue.FromSingle(0), MapExactValue.FromSingle(0)), new(MapExactValue.FromSingle(2), MapExactValue.FromSingle(1)),
            new(MaxSourceFacesPerBound: refused ? 1 : 2), new(new()), upper: false);
        Assert.Equal(refused ? MapRefinementStatus.CapacityExceeded : MapRefinementStatus.Complete, result.Status);
        var rows = probe.For("MaxSourceFacesPerBound").Where(r => Read<string>(r, "Site") == "MapWaterBoundReads.CellFaces").ToArray();
        Assert.Equal(refused ? 1 : 2, rows.Length);
        var row = rows[0];
        Assert.Equal(0, Read<decimal>(row, "CurrentDemand"));
        Assert.Equal(2, Read<decimal>(row, "ProposedIncrement"));
        Assert.Equal(refused ? 1 : 2, Read<decimal>(row, "Allowance"));
        Assert.Equal(refused, Read<bool>(row, "Refused"));
    }
}
