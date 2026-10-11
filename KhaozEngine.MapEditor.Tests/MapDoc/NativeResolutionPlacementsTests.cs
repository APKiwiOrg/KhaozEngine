using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

// MapNativeResolution.ResolvePlacements is internal to MapDoc, which grants this project, not MapDoc.Tests, its
// internals. The MapDoc fixtures are linked here.
public sealed class NativeResolutionPlacementsTests
{
    [Fact]
    public void ResolvePlacements_MatchesWholeDocumentResolution()
    {
        MapDocument cave = SupportFixtures.CaveWithPlacements();
        MapResolvedDocument whole = MapNativeResolution.Resolve(cave, SupportFixtures.Assets(), SupportFixtures.V2);
        var some = MapNativeResolution.ResolvePlacements(cave, SupportFixtures.Assets(), SupportFixtures.V2,
            new[] { "p-surface", "p-space" });
        Assert.Equal(new[] { "p-space", "p-surface" }, some.Select(p => p.PlacementId));
        Assert.All(some, p => Assert.Null(cave.Placements.Single(a => a.Id == p.PlacementId).Y));
        Assert.Equal(Snapshot(whole.Placements.Where(p => p.PlacementId is "p-space" or "p-surface")), Snapshot(some));

        var f = NativeResolverFixtures.Create();
        f.Document.Placements[0].Y = null;
        string id = f.Document.Placements[0].Id;
        Func<float, float, float> field = MapRuntime.BuildField(f.Document, MapDocRegistry.CreateDefault()).SampleHeight;
        MapResolvedDocument analytic = MapNativeResolution.Resolve(f.Document, f.Assets, f.Options, field);
        var one = MapNativeResolution.ResolvePlacements(f.Document, f.Assets, f.Options, new[] { id }, field);
        Assert.Equal(Snapshot(analytic.Placements.Where(p => p.PlacementId == id)), Snapshot(one));
    }

    static (string, string, string, long?, MapTransform, string)[] Snapshot(IEnumerable<MapResolvedPlacement> placements) =>
        placements.Select(p => (p.PlacementId, p.Kind, p.AssetId, p.NumericId, p.Transform, string.Join("|", p.Tags))).ToArray();
}
