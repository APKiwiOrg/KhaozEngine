using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceQueryTraversalRegressionTests
{
    [Fact]
    public void ExcludedRolesStillConsumeTheFiniteMetadataAllowance()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(0);
        MapSurfaceRef ceiling = doc.Surfaces.Refs[0] with { Role = MapSurfaceRole.Ceiling };
        doc.Surfaces.Refs.Clear();
        doc.Surfaces.Refs.Add(ceiling with { Id = "ceiling-a" });
        doc.Surfaces.Refs.Add(ceiling with { Id = "ceiling-b" });
        var scope = new MapSurfaceScope(WorldFrame.Origin, Vector2.Zero, Vector2.One, null, null,
            new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits(MaxCandidatePatches: 1, MaxPageReads: 0));
        MapPatchFindResult limited = MapDocumentSurfaceSource.Capture(doc).FindPatches(scope);
        Assert.Equal(MapFindStatus.CapacityExceeded, limited.Status);
        Assert.Equal(0, limited.PagesRead);
        Assert.Empty(limited.Patches);
        Assert.Empty(limited.KnownEmpty);
        Assert.Empty(limited.Unavailable);

        MapPatchFindResult enough = MapDocumentSurfaceSource.Capture(doc).FindPatches(scope with
        {
            Limits = new MapQueryLimits(MaxCandidatePatches: 1, MaxPageReads: 1),
        });
        Assert.Equal(MapFindStatus.Complete, enough.Status);
        Assert.Equal(0, enough.PagesRead);
        Assert.Empty(enough.Patches);
        Assert.Empty(enough.KnownEmpty);
        Assert.Empty(enough.Unavailable);
    }

    [Fact]
    public void EmptyLookupGapsStillConsumeTheFiniteMetadataAllowance()
    {
        MapDocument doc = SurfaceStorageFixtures.FlatPatches(0);
        MapSurfaceRef ground = doc.Surfaces.Refs[0];
        doc.Surfaces.Refs[0] = ground with { Frame = ground.Frame with { CellUnitMetres = new(1, 16) } };
        for (int z = 0; z < 4; z++)
            for (int x = 0; x < 4; x++)
            {
                var key = new MapPatchKey("ground", 512 * x, 512 * z);
                doc.Surfaces.Patches.Add(key, SurfaceStorageFixtures.FlatPatch(key, 1000));
            }
        var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(1, 1201), new Vector2(7167, 1202),
            null, null, new[] { MapSurfaceRole.SupportFloor }, null,
            new MapQueryLimits(MaxCandidatePatches: 1, MaxPageReads: 0));
        MapDocumentSurfaceSource source = MapDocumentSurfaceSource.Capture(doc);
        MapPatchFindResult limited = source.FindPatches(scope);
        Assert.Equal(MapFindStatus.CapacityExceeded, limited.Status);
        Assert.Equal(0, limited.PagesRead);
        Assert.Empty(limited.Patches);
        Assert.Empty(limited.KnownEmpty);
        Assert.Empty(limited.Unavailable);

        MapPatchFindResult enough = source.FindPatches(scope with
        {
            Limits = new MapQueryLimits(MaxCandidatePatches: 1, MaxPageReads: 1),
        });
        Assert.Equal(MapFindStatus.Complete, enough.Status);
        Assert.Equal(0, enough.PagesRead);
        Assert.Empty(enough.Patches);
        Assert.NotEmpty(enough.KnownEmpty);
        Assert.Empty(enough.Unavailable);
    }
}
