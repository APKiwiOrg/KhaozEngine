using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneSeatQueryTests
{
    [Theory]
    [InlineData(false, 128, true)]
    [InlineData(true, 128, true)]
    [InlineData(false, 16, false)]
    [InlineData(true, 16, false)]
    public void SeatResolution_UsesDeclaredRecordDepth(bool marker, int depth, bool accepts)
    {
        using var fixture = Create(marker);
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        var source = MapSceneSource.Capture(disk, fixture.Assets).Source!;
        var queries = new MapQueryLimits(MaxCandidatePatches: 128, MaxInspectedFaces: 8192,
            MaxSupportIntersections: 128, MaxPageReads: 256, MaxRecordReads: 128, MaxRecordDepth: depth);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(.1f, -.9f), new(.9f, -.1f), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, queries), new(.1, -1, -.9, .9, 1, -.1), MapSceneChannels.All);
        var result = source.Acquire(scope);
        if (accepts)
        {
            Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
            if (marker) Assert.Equal(0f, Assert.Single(result.Scene!.EffectiveMarkers).Position.Y.ToSingle());
            else Assert.Equal(0f, Assert.Single(result.Scene!.Placements).Transform.Position.Y);
        }
        else
        {
            Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
            Assert.Equal(new MapSceneCapacity("MaxRecordDepth", depth + 1, depth), result.Capacity);
            Assert.Contains("allowance " + depth, Assert.Single(result.Findings));
        }
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    public void SeatResolution_RefusalAdapterRetainsCapacity(bool marker, int depth)
    {
        using var fixture = Create(marker);
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        var input = MapEffectiveResolutionInput.FromDocument(fixture.Document) with { Surfaces = new() };
        var queries = new MapQueryLimits(MaxCandidatePatches: 128, MaxPageReads: 256, MaxRecordReads: 128, MaxRecordDepth: depth);
        var error = Assert.Throws<MapDocumentException>(() => MapEffectiveResolutionKernel.Resolve(input, fixture.Assets, disk.SceneSurfaces, queryLimits: queries));
        Assert.Equal(new MapSceneCapacity("MaxRecordDepth", depth + 1, depth), MapSceneCapacity.From(error));
        Assert.Equal(MapSceneSourceStatus.CapacityExceeded, MapSceneSource.Classify(error));
        Assert.Contains(marker ? "Marker 'seat'" : "Placement 'seat'", error.Message);
    }

    static NativeSceneFixture Create(bool marker)
    {
        var fixture = NativeSceneFixtures.Create("empty");
        MapSceneQueryFixtures.AddNativeSeat(fixture.Document, marker, "branch");
        return fixture;
    }
}
