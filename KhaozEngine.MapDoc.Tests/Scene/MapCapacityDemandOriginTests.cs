using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Spaces;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

[Collection("AllocSensitive")]
public sealed class MapCapacityDemandOriginTests
{
    [Fact]
    public void ColdMembership_ContextIncludesPrismBaselineAndQueryPatchAllowance()
    {
        var resolved = PrefabFixtures.Effective(PrefabFixtures.Create());
        var view = resolved.CompleteView();
        using var probe = new CapacityDemandProbe();
        var result = new MapSpaceMembership(view).Query(PrefabFixtures.Point(resolved, 2, 1, -2));
        Assert.Equal(MapMembershipStatus.Resolved, result.Status);
        // This invented room has one rectangular prism, four footprint vertices.
        var faces = probe.For("MaxInspectedFaces").Where(r => Read<string>(r, "Site") == "MapBoundFaceContext.MaxContextFaces.1").ToArray();
        Assert.NotEmpty(faces);
        Assert.All(faces, r => Assert.Equal(4, Read<decimal>(r, "CurrentDemand")));
        Assert.All(faces, r => Assert.Equal(view.Scope.Limits.MaxInspectedFaces, Read<decimal>(r, "Allowance")));
        Assert.All(faces, r => Assert.True(Read<decimal>(r, "Requested") > 4));
        var patches = probe.For("MaxCandidatePatches").Where(r => Read<string>(r, "Site") == "MapBoundPreparation.MaxContextPatches.1").ToArray();
        Assert.NotEmpty(patches);
        Assert.All(patches, r => Assert.Equal(view.Scope.Limits.MaxCandidatePatches, Read<decimal>(r, "Allowance")));
    }

    [Fact]
    public void FoliageContext_ReportsTheOriginalFaceProfile()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        using var probe = new CapacityDemandProbe();
        var result = MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], default, new(MaxFaces: 12345));
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        var rows = probe.For("MaxFaces").Where(r => Read<string>(r, "Site") == "MapBoundFaceContext.MaxContextFaces.1").ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(12345, Read<decimal>(r, "Allowance")));
        Assert.All(rows, r => Assert.Equal(0, Read<decimal>(r, "CurrentDemand")));
        Assert.All(rows, r => Assert.False(Read<bool>(r, "Refused")));
    }
    [Fact]
    public void ExceptionalOwnerContext_KeepsTheOriginalFaceCeiling()
    {
        using var probe = new CapacityDemandProbe();
        new MapFoliageOwnerTests().OwnerLocalClearance_UnpackRemapAndRemovalAreAtomic();
        var rows = probe.For("MaxFaces").Where(r => Read<string>(r, "Site") == "MapBoundFaceContext.MaxContextFaces.1").ToArray();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(262144, Read<decimal>(row, "Allowance")));
        Assert.Contains(rows, row => Read<decimal>(row, "CurrentDemand") > 0);
    }

    [Fact]
    public void DerivedInstanceAllowance_ReportsPreviousChunksAgainstTheOriginalQueryCeiling()
    {
        using var probe = new CapacityDemandProbe();
        var original = new MapFoliageLimits(MaxSamples: 300, MaxQuerySamples: 100);
        var remaining = original with { MaxQuerySamples = 1 };
        MapFoliageDemandOrigins.Carry(remaining, original, samples: 99);
        using var builder = new MapFoliageInstanceBuilder("layer", new("legacy-tile", 1, 0), default, remaining);
        Assert.True(builder.TryPrepare(null, "model"));
        builder.AppendPrepared(0, 0, new("model", Vector3.Zero, Matrix4x4.Identity, 0));
        Assert.False(builder.TryPrepare(null, "model"));
        var rows = probe.For("MaxQuerySamples").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new decimal[] { 99, 100 }, rows.Select(row => Read<decimal>(row, "CurrentDemand")));
        Assert.All(rows, row => Assert.Equal(1, Read<decimal>(row, "ProposedIncrement")));
        Assert.All(rows, row => Assert.Equal(100, Read<decimal>(row, "Allowance")));
        Assert.False(Read<bool>(rows[0], "Refused"));
        Assert.True(Read<bool>(rows[1], "Refused"));
    }

    [Fact]
    public void PaintTargetRemainder_ReportsAlreadyAdmittedPaintPatches()
    {
        var keys = new[] { new MapPatchKey("ground", 0, 0), new("ground", 1, 0), new("ground", 2, 0) };
        var bounds = MapWaterPolygons.Bounds.Of(new(MapWideRational.From(0), MapWideRational.From(-1)),
            new(MapWideRational.From(192), MapWideRational.From(0)));
        using var probe = new CapacityDemandProbe();
        Assert.Null(MapPartialPaintCompiler.CandidateCells(MapLatticeFrame.ImportedMetreCentimetre, [bounds], keys, 1,
            new("MaxContextPatches", 2, 3)));
        var rows = probe.For("MaxContextPatches").ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new decimal[] { 2, 3 }, rows.Select(row => Read<decimal>(row, "CurrentDemand")));
        Assert.All(rows, row => Assert.Equal(1, Read<decimal>(row, "ProposedIncrement")));
        Assert.All(rows, row => Assert.Equal(3, Read<decimal>(row, "Allowance")));
        Assert.False(Read<bool>(rows[0], "Refused"));
        Assert.True(Read<bool>(rows[1], "Refused"));
    }

}
