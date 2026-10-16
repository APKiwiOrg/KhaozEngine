using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Markers;

public sealed class MapMarkerRegistryTests
{
    [Fact]
    public void DisabledParticipantNameCollisions_ValidateBeforeAnyRead()
    {
        // disabled-child: only the prefab child is disabled.
        MarkerFixture child = MarkerFixtures.DuplicateNames("disabled-child");
        string[] errorsBeforeRename = RefuseBothPaths(child);
        MapPrefabMarkerChild entry = EntryChild(child);
        var renamedChild = MarkerFixtures.WithEntryOverride(child,
            new MapPrefabReplaceOverride(MarkerFixtures.EntryKey, entry with { Value = entry.Value with { Name = "arrival-child" } }));
        MapMarkerRegistry childRenamed = AcceptBothPaths(renamedChild);
        _ = AcceptBothPaths(MarkerFixtures.WithEntryOverride(child, new MapPrefabRemoveOverride(MarkerFixtures.EntryKey)));
        string[] errorsAfterRestore = RefuseBothPaths(MarkerFixtures.WithEntryOverride(renamedChild, null));

        Assert.Contains(errorsBeforeRename, e => e.Contains("duplicate marker name", StringComparison.Ordinal));
        Assert.Equal(new[] { "arrival", "arrival-child" }, childRenamed.All.Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Equal("w", childRenamed.FindByName("arrival")!.Id);
        Assert.Equal("a/entry", childRenamed.FindByName("arrival-child")!.Id);
        Assert.Null(childRenamed.FindByName("Arrival"));
        Assert.Equal(2, childRenamed.All.Count);
        Assert.Equal("w", Assert.Single(childRenamed.Enabled).Id);
        Assert.Equal(errorsBeforeRename, errorsAfterRestore);

        // disabled-world: only the world marker is disabled.
        MarkerFixture world = MarkerFixtures.DuplicateNames("disabled-world");
        errorsBeforeRename = RefuseBothPaths(world);
        MapLocalMarkerDoc original = MarkerFixtures.DuplicateWorldMarker("disabled-world");
        var renamedWorld = MarkerFixtures.WithWorldMarkers(world, original with { Name = "arrival-world" });
        MapMarkerRegistry worldRenamed = AcceptBothPaths(renamedWorld);
        _ = AcceptBothPaths(MarkerFixtures.WithWorldMarkers(world));
        errorsAfterRestore = RefuseBothPaths(MarkerFixtures.WithWorldMarkers(renamedWorld, original));
        IReadOnlyList<MapMarkerFinding> findings = new MapMarkerRoleCatalog(new[] { "entry-role" }).Validate(worldRenamed);

        Assert.Contains(errorsBeforeRename, e => e.Contains("duplicate marker name", StringComparison.Ordinal));
        Assert.Equal(new[] { "arrival", "arrival-world" }, worldRenamed.All.Select(m => m.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "second", "first" }, worldRenamed.FindById("w")!.Tags);
        Assert.Equal("unknown-role", Assert.Single(findings).Code);
        Assert.Equal("w", findings[0].MarkerId);
        Assert.Equal("unregistered-role", worldRenamed.FindById("w")!.Role);
        Assert.Equal("a/entry", Assert.Single(worldRenamed.Enabled).Id);
        Assert.Equal(errorsBeforeRename, errorsAfterRestore);
    }

    [Fact]
    public void DuplicateEffectiveIds_RefuseBeforeAnyRead()
    {
        MarkerFixture basis = MarkerFixtures.DuplicateNames("disabled-world");
        MapLocalMarkerDoc w = MarkerFixtures.DuplicateWorldMarker("disabled-world") with { Name = "arrival-world" };

        // World against prefab-derived: the world marker takes the instance child's world ID under its own name.
        string[] prefabId = RefuseBothPaths(MarkerFixtures.WithWorldMarkers(basis, w with { Id = "a/entry" }));
        Assert.Contains(prefabId, e => e.Contains("duplicate marker ID 'a/entry'", StringComparison.Ordinal));
        // World against world, under distinct names.
        string[] worldId = RefuseBothPaths(MarkerFixtures.WithWorldMarkers(basis, w, w with { Name = "arrival-other" }));
        Assert.Contains(worldId, e => e.Contains("uplicate marker", StringComparison.Ordinal) && e.Contains("'w'", StringComparison.Ordinal));
        // Positive control: distinct IDs and names pass.
        Assert.Equal(new[] { "a/entry", "w", "w2" }, AcceptBothPaths(MarkerFixtures.WithWorldMarkers(basis, w,
            w with { Id = "w2", Name = "arrival-other" })).All.Select(m => m.Id));

        // Over geometry the accepted document samples support, so the zero-read refusal is meaningful.
        MarkerFixture stacked = MarkerFixtures.Stacked();
        var counting = new CountingSurfaceSource(MapDocumentSurfaceSource.Capture(stacked.Document));
        _ = MapResolverV2.Resolve(stacked.Document, stacked.Assets, counting, MarkerFixtures.Options());
        Assert.True(counting.ReadPatchCalls + counting.FindPatchesCalls > 0);
        var markers = stacked.Document.BuildingIntent!.Markers;
        string[] stackedName = RefuseBothPaths(MarkerFixtures.WithWorldMarkers(stacked,
            markers.Select(m => m.Id == "stacked-disabled" ? m with { Name = "stacked-surface-name" } : m).ToArray()));
        Assert.Contains(stackedName, e => e.Contains("duplicate marker name 'stacked-surface-name'", StringComparison.Ordinal));
    }

    [Fact]
    public void HalfOpenRect_ContainsNegativeMinAndExcludesBothMaxEdges()
    {
        MapEffectiveResources resources = MarkerFixtures.Effective(MarkerFixtures.RectPoints());
        var registry = new MapMarkerRegistry(MapEffectiveAnnotations.Markers(resources));
        MapScopedSurfaces view = resources.CompleteView();
        int calls = 0;
        MapMembershipResult Membership(MapEffectiveMarker marker) { calls++; return MapMarkerSpaces.Query(view, marker); }

        var hits = registry.InRect(new(-1, -1, 0, 0), true, Membership);
        Assert.Equal(new[] { "a", "e" }, hits.Select(h => h.Marker.Id));
        // The caller's membership answer is kept as given. Over this geometry-free world that is MissingGeometry, never Outside.
        Assert.All(hits, h => Assert.Equal(MapMembershipStatus.MissingGeometry, h.Space.Status));
        Assert.Equal(2, calls);
        Assert.Same(registry.FindById("a"), hits[0].Marker);

        calls = 0;
        Assert.Empty(registry.InRect(new(-1, -1, -1, 0), false, Membership));
        Assert.Empty(registry.InRect(new(-1, -1, 0, -1), false, Membership));
        Assert.Equal(0, calls);
        foreach (MapMarkerRect invalid in new MapMarkerRect[]
        {
            new(0, -1, -1, 0), new(-1, 0, 0, -1), new(double.NaN, -1, 0, 0), new(-1, -1, double.PositiveInfinity, 0),
            new(-1, double.NegativeInfinity, 0, 0), new(-1, -1, 0, double.NaN),
        })
            Assert.ThrowsAny<ArgumentException>(() => registry.InRect(invalid, false, Membership));
        Assert.Equal(0, calls);

        // Four adjacent quadrants contain every point exactly once, boundary points included.
        var tiles = new MapMarkerRect[] { new(-1, -1, 0, 0), new(0, -1, 1, 0), new(-1, 0, 0, 1), new(0, 0, 1, 1) };
        var seen = tiles.SelectMany(t => registry.InRect(t, false, Membership)).Select(h => h.Marker.Id).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, seen);
        Assert.Throws<ArgumentNullException>(() => registry.InRect(new(-1, -1, 0, 0), false, null!));
    }

    [Fact]
    public void RectangleIndex_OutwardBoundsKeepBoundaryPoints()
    {
        var third = new MapExactValue(1, 3);
        var exact = MapWideRational.From(third);
        double maxX = third.ToDouble();
        while (MapWideRational.FromDouble(maxX).CompareTo(exact) <= 0) maxX = Math.BitIncrement(maxX);
        Assert.True(MapWideRational.FromDouble(Math.BitDecrement(maxX)).CompareTo(exact) < 0);

        var markers = Enumerable.Range(0, 24).Select(k => Point($"p{k:00}", k < 3 ? third : new MapExactValue(3L * k + 1, 3), new(k % 3, 1))).ToArray();
        var registry = new MapMarkerRegistry(markers);
        var rect = new MapMarkerRect(0, -1, maxX, 3);
        var hits = registry.InRect(rect, false, Outside(), out MapMarkerQueryWork work);

        Assert.Equal(new[] { "p00", "p01", "p02" }, hits.Select(h => h.Marker.Id));
        Assert.Equal(Exhaustive(markers, rect), hits.Select(h => h.Marker.Id));
        Assert.True(work.PointsTested >= 3);
    }

    [Fact]
    public void RectangleWork_IsCandidateBounded()
    {
        var markers = Enumerable.Range(0, 257).Select(i => Point($"q{i:000}", new(4L * (i % 16) + 1, 2), new(4L * (i / 16) + 1, 2))).ToArray();
        var registry = new MapMarkerRegistry(markers);
        int calls = 0;
        var outside = Outside();
        var rect = new MapMarkerRect(8.25, 12.25, 8.75, 12.75);
        var hits = registry.InRect(rect, false, m => { calls++; return outside(m); }, out MapMarkerQueryWork work);

        Assert.Equal(new[] { "q100" }, hits.Select(h => h.Marker.Id));
        Assert.Equal(Exhaustive(markers, rect), hits.Select(h => h.Marker.Id));
        Assert.Equal(hits.Count, calls);
        Assert.True(work.PointsTested <= 16, work.ToString());
        Assert.True(work.NodesVisited <= 32, work.ToString());
    }

    [Fact]
    public void Registry_IsDefensiveAndRefusesAmbiguousInput()
    {
        var a = Point("a", new(0, 1), new(0, 1));
        var source = new List<MapEffectiveMarker> { a, Point("b", new(1, 1), new(0, 1)) with { Enabled = false, Tags = new[] { "t", "t" } } };
        var registry = new MapMarkerRegistry(source);
        source.Clear();

        Assert.Equal(new[] { "a", "b" }, registry.All.Select(m => m.Id));
        Assert.Equal(new[] { "a" }, registry.Enabled.Select(m => m.Id));
        Assert.Equal(new[] { "b" }, registry.ByTag("t", false).Select(m => m.Id));
        Assert.Empty(registry.ByTag("t", true));
        Assert.Equal(new[] { "t", "t" }, registry.FindById("b")!.Tags);
        Assert.Equal(new[] { "a", "b" }, registry.ByRole("point-role", false).Select(m => m.Id));
        Assert.Empty(registry.ByRole("Point-role", false));
        Assert.Throws<ArgumentException>(() => new MapMarkerRegistry(new[] { a, a with { Name = "other" } }));
        Assert.Throws<ArgumentException>(() => new MapMarkerRegistry(new[] { a, a with { Id = "c" } }));
        Assert.ThrowsAny<ArgumentException>(() => new MapMarkerRegistry(new MapEffectiveMarker[] { null! }));
    }

    static MapEffectiveMarker Point(string id, MapExactValue x, MapExactValue z) =>
        new(1, id, id + "-name", new(x, new(1, 1), z), 0, "point-role", true, Array.Empty<string>(), null);

    static IEnumerable<string> Exhaustive(IEnumerable<MapEffectiveMarker> markers, MapMarkerRect rect)
    {
        bool Inside(MapExactValue value, double min, double max) =>
            MapWideRational.From(value).CompareTo(MapWideRational.FromDouble(min)) >= 0 && MapWideRational.From(value).CompareTo(MapWideRational.FromDouble(max)) < 0;
        return markers.Where(m => Inside(m.Position.X, rect.MinX, rect.MaxX) && Inside(m.Position.Z, rect.MinZ, rect.MaxZ))
            .Select(m => m.Id).Order(StringComparer.Ordinal).ToArray();
    }

    // Real membership over a geometry-free authored world.
    static Func<MapEffectiveMarker, MapMembershipResult> Outside()
    {
        MapScopedSurfaces view = MarkerFixtures.Effective(MarkerFixtures.Create()).CompleteView();
        return m => MapMarkerSpaces.Query(view, m);
    }

    static (string Id, string Name, MapExactPoint Position, bool Enabled, string Role, string Tags) Row(MapEffectiveMarker m) =>
        (m.Id, m.Name, m.Position, m.Enabled, m.Role, string.Join("|", m.Tags));

    static MapPrefabMarkerChild EntryChild(MarkerFixture fixture) =>
        fixture.Assets.GetPrefab(MarkerFixtures.DuplicateResource).Children.OfType<MapPrefabMarkerChild>().Single();

    /// <summary>The loaded expansion and the stored resolver both refuse before any patch read, the loaded one also
    /// before stage ExpandedGeometry, with one message.</summary>
    static string[] RefuseBothPaths(MarkerFixture fixture)
    {
        var loaded = new CountingSurfaceSource(MapDocumentSurfaceSource.Capture(fixture.Document));
        var stages = new List<string>();
        var error = Assert.Throws<MapDocumentException>(() =>
            MapPrefabResolver.Expand(fixture.Document, fixture.Assets, new(), new(stages.Add), loaded));
        Assert.Equal(0, loaded.ReadPatchCalls);
        Assert.Equal(0, loaded.FindPatchesCalls);
        Assert.DoesNotContain("ExpandedGeometry", stages);
        var stored = new CountingSurfaceSource(MapDocumentSurfaceSource.Capture(fixture.Document));
        var storedError = Assert.Throws<MapDocumentException>(() =>
            MapResolverV2.Resolve(fixture.Document, fixture.Assets, stored, MarkerFixtures.Options()));
        Assert.Equal(0, stored.ReadPatchCalls);
        Assert.Equal(0, stored.FindPatchesCalls);
        Assert.Equal(error.Message, storedError.Message);
        return error.Message.Split('\n');
    }

    static MapMarkerRegistry AcceptBothPaths(MarkerFixture fixture)
    {
        var stages = new List<string>();
        var counting = new CountingSurfaceSource(MapDocumentSurfaceSource.Capture(fixture.Document));
        MapEffectiveResources loaded = MapPrefabResolver.Expand(fixture.Document, fixture.Assets, new(), new(stages.Add), counting);
        Assert.Contains("ExpandedGeometry", stages);
        var stored = MapResolverV2.Resolve(fixture.Document, fixture.Assets,
            new CountingSurfaceSource(MapDocumentSurfaceSource.Capture(fixture.Document)), MarkerFixtures.Options());
        var registry = new MapMarkerRegistry(MapEffectiveAnnotations.Markers(loaded));
        Assert.Equal(registry.All.Select(Row), MapEffectiveAnnotations.Markers(stored.Document.EffectiveResources!).Select(Row));
        return registry;
    }
}
