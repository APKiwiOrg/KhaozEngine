using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>Support-bound marker fixtures over the released stacked, cave and prefab room geometry. Nothing here reads
/// a committed file.</summary>
internal static partial class MarkerFixtures
{
    internal static readonly MapTransform TransferredFrame = new(new(-12.25f, -450, 7.125f), 0.371f, 1.2f);
    internal static readonly MapTransform LocalCaveFrame = new(new(-12.25f, -450, 7.125f), 0, 2);

    /// <summary>A closure holding only the given prefab payloads, through the same in-memory source as Create.</summary>
    internal static MapAssetClosure PrefabClosure(params MapPrefabAsset[] prefabs)
    {
        var (roots, source) = Closure(prefabs);
        return MapAssetClosure.Load(roots, source);
    }

    internal static MapEffectiveResources Effective(MarkerFixture fixture) => MapPrefabResolver.Expand(fixture.Document, fixture.Assets);

    internal static MapSupportBinding SurfaceBinding(string surfaceId) => new(MapSupportBindingKind.Surface, surfaceId, null, null, null, null);
    internal static MapSupportBinding SpaceBinding(string spaceId, float referenceY, float below, float above) =>
        new(MapSupportBindingKind.Space, null, spaceId, referenceY, below, above);

    /// <summary>A payload-2 marker at a world or frame-local XZ with one height choice.</summary>
    internal static MapLocalMarkerDoc Marker(string id, float x, float z, MapMarkerHeight height, IReadOnlyList<string>? tags = null, float yaw = 0) =>
        MapMarkerHeights.Apply(new(1, id, id + "-name", new Vector3(x, 0, z), yaw, "guard-role", true, tags ?? new[] { "first", "second" }), height);

    /// <summary>Released eight stacked caves with one Surface-bound and one Space-bound world marker at (0.5, 0.5), and a
    /// disabled Space-bound world marker at (1.5, 1.5).</summary>
    internal static MarkerFixture Stacked()
    {
        MapDocument document = SupportFixtures.EightStacked();
        document.ResolverIdentity ??= new(1, 2);
        if (document.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2) document.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
        document.BuildingIntent = new(1, [], [], [], new[]
        {
            Marker("stacked-surface", 0.5f, 0.5f, new(null, SurfaceBinding("floor-2"))),
            Marker("stacked-space", 0.5f, 0.5f, SpaceHeight()),
            Marker("stacked-disabled", 1.5f, 1.5f, SpaceHeight()) with { Enabled = false },
        });
        return new(document, SupportFixtures.Assets());
    }

    /// <summary>Stacked with floor-2 regraded to centimetre corner heights, a Surface-bound world marker on the slope and
    /// an Explicit control.</summary>
    internal static MarkerFixture Sloped()
    {
        MarkerFixture stacked = Stacked();
        var floor = new MapPatchKey("floor-2", 0, 0);
        stacked.Document.Surfaces = MapTerrainEdits.Prepare(stacked.Document.Surfaces,
            new MapSetCornerHeights(floor, 0, 0, 3, 3, new[] { 2000, 2007, 2013, 2003, 2010, 2017, 2006, 2011, 2019 })).Candidate;
        stacked.Document.BuildingIntent = new(1, [], [], [], stacked.Document.BuildingIntent!.Markers.Concat(new[]
        {
            Marker("slope", 0.3f, 0.7f, new(null, SurfaceBinding("floor-2"))),
            MapMarkerHeights.Apply(new(1, "flat", "flat-name", new Vector3(1.25f, 0, 1.75f), 0, "guard-role", true, Array.Empty<string>()), ExplicitHeight()),
        }).ToArray());
        return stacked;
    }

    /// <summary>The released stacked prefab-cave geometry with one Explicit world marker in the deep room.</summary>
    internal static MarkerFixture Cave()
    {
        PrefabFixture cave = PrefabFixtures.Cave("stacked");
        var storeys = cave.Document.BuildingIntent?.Storeys ?? [];
        cave.Document.BuildingIntent = new(1, [], [], [], new[]
        {
            MapMarkerHeights.Apply(new(1, "underground", "underground-name", new Vector3(2, 0, -2), 0.371f, "guard-role", true, new[] { "deep", "room" }), new(-57.75f, null)),
        })
        { Storeys = storeys };
        return new(cave.Document, cave.Assets);
    }

    /// <summary>The released prefab room with an Explicit marker child at local (2, 1, -2), placed at the given frame
    /// inside an authored world parent.</summary>
    internal static MarkerFixture LocalCave() => LocalCave(LocalCaveFrame);

    internal static MarkerFixture LocalCave(MapTransform frame) => Placed(frame, new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "room-marker"),
        MapMarkerHeights.Apply(new(1, "room-marker", "room-marker-name", new Vector3(2, 0, -2), 0.25f, "guard-role", true, new[] { "inside" }), new(1f, null))));

    /// <summary>The released prefab room with a marker child Space-bound to its local room, placed at the transferred frame.</summary>
    internal static MarkerFixture TransferredSpace() => Placed(TransferredFrame, SpaceChild());

    /// <summary>The released prefab room with a marker child Surface-bound to its local floor, placed at the transferred frame.</summary>
    internal static MarkerFixture TransferredSurface() => Placed(TransferredFrame, SurfaceChild());

    /// <summary>TransferredSurface with a separate local pad floor inside the parent exterior and a marker bound only to it.</summary>
    internal static MarkerFixture PaddedSurface()
    {
        MapSurfacePatch pad = PrefabFixtures.Patch("pad", 2, 2, 0);
        pad.CellMinX = 8;
        pad.CellMinZ = 8;
        return Placed(TransferredFrame, SurfaceChild(), PrefabFixtures.SurfaceChild("pad", MapSurfaceRole.SupportFloor, pad),
            new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "pad-marker"), Marker("pad-marker", 9, -9, new(null, SurfaceBinding("pad"))))
            { References = [new("/value/supportBinding/surfaceId", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Surface, "pad"))] });
    }

    internal static MapPrefabMarkerChild SpaceChild() => new(new(MapPrefabChildKind.Marker, "space-marker"),
        Marker("space-marker", 2, -2, new(null, SpaceBinding("room", 0.5f, 1, 1)), new[] { "bound", "space" }, 0.5f))
    { References = [new("/value/supportBinding/spaceId", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Record, "room"))] };

    internal static MapPrefabMarkerChild SurfaceChild() => new(new(MapPrefabChildKind.Marker, "surface-marker"),
        Marker("surface-marker", 3, -1, new(null, SurfaceBinding("floor")), new[] { "bound", "surface" }, -0.5f))
    { References = [new("/value/supportBinding/surfaceId", new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Surface, "floor"))] };

    /// <summary>Places the released room prefab with extra children at <paramref name="frame"/>, inside an independently
    /// authored world parent, through the released binding planner.</summary>
    internal static MarkerFixture Placed(MapTransform frame, params MapPrefabChild[] extra)
    {
        PrefabFixture room = PrefabFixtures.Create();
        MapPrefabAsset hut = room.Assets.GetPrefab("hut");
        MapPrefabAsset asset = hut with { Children = hut.Children.Concat(extra).ToArray() };
        var assets = PrefabFixtures.LoadAssets(MapPrefabFile.Write(asset), true);
        MapDocument document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(room.Document));
        document.NativeAssets = assets.Roots.ToList();
        var instance = document.Prefabs!.Instances.Single() with { ResourceSha256 = assets.GetResource("hut").Reference.Sha256, Transform = frame };
        document.Prefabs = new(1, 1, new[] { instance }, []);
        var plan = MapPrefabBindingPlanner.Plan(document, asset, assets, "a", instance.Bindings);
        // Added children take the released fixture's instance-prefixed world IDs, as the released room children do.
        var added = extra.Select(c => c.Key).ToHashSet();
        var bindings = new MapPrefabBindings(plan.Bindings.Entries.Select(b => added.Contains(b.Child) ? b with { WorldId = "a/" + b.Child.Key } : b).ToArray());
        document.Prefabs = new(1, 1, new[] { instance with { Bindings = bindings } }, []);
        document.NumericIdHighWaterMark = plan.HighWaterMark;
        var placed = new PrefabFixture(document, assets);
        PrefabFixtures.AuthorWorldParent(placed, frame);
        return new(placed.Document, assets);
    }
}
