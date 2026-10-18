using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>Room-free support-edit fixtures with one bound world marker, and a renamed payload-1 asset. Nothing here
/// reads a committed file.</summary>
internal static partial class MarkerFixtures
{
    internal const string BoundId = "bound";
    internal const string FixedId = "fixed";

    /// <summary>One bound world marker <c>bound</c> over released support geometry. Variants are <c>stacked-bound</c>,
    /// <c>deck-bound</c>, <c>floor-bound</c> and <c>room-bound</c>. Without the marker the fixture is otherwise identical,
    /// the marker-absent control.</summary>
    internal static MarkerFixture SupportEdits(string variant, bool withMarker = true)
    {
        switch (variant)
        {
            case "stacked-bound":
                {
                    // T3-R1. Stacked's surfaces carry only bound and the explicit control, never Task 2's own Support markers.
                    MarkerFixture stacked = Stacked();
                    var fixedMarker = MapMarkerHeights.Apply(new(1, FixedId, FixedId + "-name", new(1.25f, 0, 1.75f), 0, "guard-role", true,
                        Array.Empty<string>()), ExplicitHeight());
                    MapLocalMarkerDoc[] markers = withMarker
                        ? [Marker(BoundId, 0.5f, 0.5f, new(null, SurfaceBinding("floor-2"))), fixedMarker]
                        : [fixedMarker];
                    stacked.Document.BuildingIntent = new(1, [], [], [], markers);
                    return stacked;
                }
            case "deck-bound":
                return WithWorldMarker(PrefabFixtures.RoomFreeSupportResource(), withMarker,
                    _ => Marker(BoundId, 1.5f, -1.5f, new(null, SurfaceBinding(PrefabFixtures.SupportFloorId))));
            case "floor-bound":
                return WithWorldMarker(PrefabFixtures.Create(), withMarker, document => Marker(BoundId, 1.5f, -1.5f,
                    new(null, SurfaceBinding(InstanceWorldId(document, new(MapPrefabChildKind.Surface, "floor"))))));
            case "room-bound":
                {
                    PrefabFixture prism = PrefabFixtures.RoomFreeStandalonePrism();
                    return WithWorldMarker(prism, withMarker, document =>
                    {
                        var floor = document.Surfaces.Patches.Values.Single(p => p.Records.Any(r => r.Id == "a/room")).Key.SurfaceId;
                        MapSupportResult sample = new MapSupportQuery(Effective(new(document, prism.Assets)).CompleteView())
                            .SampleSurfaceExact(Exact(1.5f, 0, -1.5f), floor);
                        if (sample.Status != MapSupportStatus.Supported) throw new InvalidOperationException("The room floor sample is " + sample.Status);
                        return Marker(BoundId, 1.5f, -1.5f, new(null, SpaceBinding("a/room", sample.WorldY, 0.5f, 0.5f)));
                    });
                }
            default: throw new ArgumentException("Unknown support edit variant " + variant);
        }
    }

    /// <summary>The fixture with world marker <paramref name="id"/> made Explicit at its resolved point.</summary>
    internal static MarkerFixture WithExplicit(MarkerFixture fixture, string id)
    {
        MapEffectiveMarker resolved = MapEffectiveAnnotations.Markers(Effective(fixture)).Single(m => m.Id == id);
        var intent = fixture.Document.BuildingIntent!;
        fixture.Document.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups, intent.Lights,
            intent.Markers.Select(m => m.Id == id ? MapMarkerHeights.Apply(m, new(resolved.Position.Y.ToSingle(), null)) : m).ToArray())
        { Roofs = intent.Roofs, Storeys = intent.Storeys };
        return fixture;
    }

    /// <summary>A closure holding the golden payload-1 asset and its copy <c>golden-marker-2</c>, whose entry child is
    /// renamed to <paramref name="name"/>.</summary>
    internal static MapAssetClosure RenamedAsset(MarkerFixture fixture, string name)
    {
        MapPrefabAsset golden = fixture.Assets.GetPrefab(GoldenResource);
        MapPrefabAsset renamed = golden with
        {
            Id = GoldenResource + "-2",
            Children = golden.Children.Select(c => c is MapPrefabMarkerChild marker ? marker with { Value = marker.Value with { Name = name } } : c).ToArray(),
        };
        return PrefabClosure(golden, renamed);
    }

    internal static string InstanceWorldId(MapDocument document, MapPrefabChildKey child) =>
        document.Prefabs!.Instances.Single(i => i.Id == "a").Bindings.Entries.Single(b => b.Child == child).WorldId;

    static MarkerFixture WithWorldMarker(PrefabFixture fixture, bool withMarker, Func<MapDocument, MapLocalMarkerDoc> marker)
    {
        MapDocument document = fixture.Document;
        var intent = document.BuildingIntent ?? new(1, [], [], [], []);
        document.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups, intent.Lights,
            withMarker ? intent.Markers.Append(marker(document)).ToArray() : intent.Markers)
        { Roofs = intent.Roofs, Storeys = intent.Storeys };
        return new(document, fixture.Assets);
    }
}
