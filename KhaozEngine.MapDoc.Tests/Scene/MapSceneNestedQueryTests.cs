using System;
using System.Linq;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneNestedQueryTests
{
    [Theory]
    [InlineData(256, true)]
    [InlineData(16, false)]
    [InlineData(32, false)]
    public void NativeDependencyClosure_UsesCallerPageAllowance(int pages, bool accepts)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        MapSceneQueryFixtures.AddNativeChain(fixture.Document);
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        var captured = MapSceneSource.Capture(disk, fixture.Assets);
        Assert.Equal(MapSceneSourceStatus.Ready, captured.Status);
        var queries = new MapQueryLimits(MaxCandidatePatches: 128, MaxInspectedFaces: 8192, MaxSupportIntersections: 128, MaxPageReads: pages, MaxRecordReads: 128, MaxRecordDepth: 128);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(.1f, -.9f), new(.9f, -.1f), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, queries), new(.1, -1, -.9, .9, 1, -.1), MapSceneChannels.All);
        var result = captured.Source!.Acquire(scope);
        if (accepts)
        {
            Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
            Assert.Equal(40, result.Scene!.SurfaceView.Witness.Present.Count);
            Assert.Equal(queries, result.Scene.Scope.Surfaces.Limits);
            Assert.Equal(queries, result.Scene.SurfaceView.Scope.Limits);
        }
        else
        {
            Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
            Assert.Equal(new MapSceneCapacity("MaxPageReads", pages + 1, pages), result.Capacity);
            Assert.Contains("MaxPageReads", Assert.Single(result.Findings));
            Assert.Contains("allowance " + pages, result.Findings[0]);
        }
    }
    [Theory]
    [InlineData("MaxCandidatePatches")]
    [InlineData("MaxRecordReads")]
    [InlineData("MaxRecordDepth")]
    public void NativeDependencyClosure_NamesTheSmallerDeclaredQueryLimit(string name)
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        MapSceneQueryFixtures.AddNativeChain(fixture.Document);
        using var disk = NativeSceneFixtures.OpenTiled(fixture);
        var source = MapSceneSource.Capture(disk, fixture.Assets).Source!;
        var limits = new MapQueryLimits(MaxCandidatePatches: 128, MaxPageReads: 256, MaxRecordReads: 128, MaxRecordDepth: 128);
        limits = name switch
        {
            "MaxCandidatePatches" => limits with { MaxCandidatePatches = 4 },
            "MaxRecordReads" => limits with { MaxRecordReads = 4 },
            _ => limits with { MaxRecordDepth = 4 },
        };
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(.1f, -.9f), new(.9f, -.1f), null, null,
            Enum.GetValues<MapSurfaceRole>(), null, limits), new(.1, -1, -.9, .9, 1, -.1), MapSceneChannels.All);
        var result = source.Acquire(scope);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Equal(new MapSceneCapacity(name, 5, 4), result.Capacity);
        Assert.Contains(name, Assert.Single(result.Findings));
    }

}
