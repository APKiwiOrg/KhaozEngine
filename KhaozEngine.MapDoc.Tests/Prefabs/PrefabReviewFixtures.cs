using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    internal static IMapSurfaceAcquisitionSource PublicCopyingSource(IMapSurfaceAcquisitionSource source, bool opaque = false) => new PublicCopyingSurfaceSource(source, opaque);
    sealed class PublicCopyingSurfaceSource(IMapSurfaceAcquisitionSource source, bool opaque) : IMapSurfaceAcquisitionSource
    {
        public string SnapshotId => source.SnapshotId;
        public string RootSha256 => source.RootSha256;
        public IReadOnlyList<MapSurfaceRef> Surfaces => source.Surfaces;
        public MapPatchRead ReadPatch(MapPatchKey key) => source.ReadPatch(key);
        public MapPatchFindResult FindPatches(MapSurfaceScope scope) => source.FindPatches(Copy(scope));
        public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope)
        {
            var session = source.OpenAcquisition(Copy(scope));
            return opaque ? new OpaqueSession(session) : session;
        }
        static MapSurfaceScope Copy(MapSurfaceScope scope) => new(scope.Frame, scope.LocalMin, scope.LocalMax,
            scope.MinY, scope.MaxY, scope.Roles, scope.SpaceIds, scope.Limits);
        sealed class OpaqueSession(IMapSurfaceAcquisitionSession session) : IMapSurfaceAcquisitionSession
        {
            public string SnapshotId => session.SnapshotId;
            public string RootSha256 => session.RootSha256;
            public int PagesRead => session.PagesRead;
            public IReadOnlyList<MapPatchKey> ReservedPatchKeys => session.ReservedPatchKeys;
            public bool TryGetSurface(string id, out MapSurfaceRef? surface) => session.TryGetSurface(id, out surface);
            public MapPatchFindResult FindPatches() => session.FindPatches();
            public MapPatchRead ReadPatch(MapPatchKey key) => session.ReadPatch(key);
            public bool TryGetIncidentRecords(MapPatchKey key, out IReadOnlyList<MapRecordRef>? records) => session.TryGetIncidentRecords(key, out records);
            public void Dispose() => session.Dispose();
        }
    }
    internal static IMapSurfaceAcquisitionSource ObservedNative(MapDocument doc, MapSurfaceAcquisitionWork work) =>
        new ObservedNativeSource(MapDocumentSurfaceSource.Capture(doc), work);
    sealed class ObservedNativeSource(MapDocumentSurfaceSource source, MapSurfaceAcquisitionWork work) : IMapSurfaceAcquisitionSource
    {
        public string SnapshotId => source.SnapshotId;
        public string RootSha256 => source.RootSha256;
        public IReadOnlyList<MapSurfaceRef> Surfaces => source.Surfaces;
        public MapPatchRead ReadPatch(MapPatchKey key) => source.ReadPatch(key);
        public MapPatchFindResult FindPatches(MapSurfaceScope scope) => source.FindPatches(scope);
        public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => source.OpenAcquisition(scope, work);
    }
    internal static PrefabFixture WorldGeometryReference()
    {
        var f = Create();
        var chain = new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "world-edge"),
            new MapBoundaryChain("world-edge", MapChainKind.Authored, null, new[]
            {
                new MapChainVertex(new("exterior", MapLatticeAddress.Corner(0, 0)), -200),
                new MapChainVertex(new("exterior", MapLatticeAddress.Corner(1, 0)), -200),
            }), new("floor", 0, 0))
        {
            References = new[] { Surf("/anchor/surfaceId", "floor"),
            new MapPrefabReferenceSlot("/value/vertices/0/vertex/surfaceId", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, "exterior")),
            new MapPrefabReferenceSlot("/value/vertices/1/vertex/surfaceId", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, "exterior")) }
        };
        var asset = f.Assets.GetPrefab("hut");
        return Reasset(f, asset with { Children = asset.Children.Append(chain).ToArray() }, new Dictionary<string, string> { ["world-edge"] = "a/world-edge" });
    }
    internal static (MapDocument Document, MapEffectiveContext Context) ComposedBudgetGeometry(int nativeCount, bool generated)
    {
        var f = Create("roomless");
        f.Document.Prefabs = null;
        var transforms = new Dictionary<string, MapEffectiveTransform>();
        for (int i = 0; i < nativeCount; i++)
        {
            string id = "native-" + i;
            f.Document.Surfaces.Refs.Add(Surface(id, MapSurfaceRole.SupportFloor));
            var patch = Patch(id, 1, 1, 0);
            f.Document.Surfaces.Patches.Add(patch.Key, patch);
            if (i != 0) transforms.Add("Surface:" + id, new(1000 + i * 100, 0, 0, 0, 1));
        }
        var surfaces = f.Document.Surfaces.Clone();
        if (generated)
        {
            surfaces.Refs.Add(Surface("generated", MapSurfaceRole.SupportFloor));
            var patch = Patch("generated", 1, 1, 0);
            surfaces.Patches.Add(patch.Key, patch);
            transforms.Add("Surface:generated", new(20, 0, 0, 0, 1));
        }
        return (f.Document, new(surfaces, transforms, new string('c', 64)));
    }
    internal static PrefabFixture SourceConsumer(bool prefab)
    {
        var f = Create();
        if (!prefab) f.Document.Prefabs = null;
        f.Document.Placements.Add(new()
        {
            Id = "consumer",
            Kind = "item",
            AssetId = "item",
            X = 1,
            Z = -1,
            Y = null,
            Scale = 1,
            SupportBinding = new(MapSupportBindingKind.Surface, "exterior", null, null, null, null)
        });
        return f;
    }
    internal static PrefabFixture PrefixedOwners(bool overlap)
    {
        var f = Create("attached");
        MapPrefabPlacementChild Owner(string id, string asset, float x) => new(new(MapPrefabChildKind.Placement, id),
            new() { Id = id, Kind = asset, AssetId = asset, X = x, Y = 0, Scale = 1 });
        MapPrefabChild[] children = overlap ? new MapPrefabChild[] { Owner("p", "item", 1), Owner("p/q", "deck", 10) }
            : new MapPrefabChild[] { Owner("deck", "item", 1), SurfaceChild("deck/floor", MapSurfaceRole.SupportFloor, Patch("deck/floor", 2, 2, 0))
                with { LocalTransform = new(new(10, 0, 0), 0, 1) } };
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Bindings = new([]) } }, []);
        return Reasset(f, new(1, "hut", children), overlap ? null : new Dictionary<string, string> { ["deck/floor"] = "a/deck/floor" });
    }
    internal static PrefabFixture MissingYProvider(bool materialized, bool space)
    {
        var f = materialized ? MaterializedOverrideFixture() : SupportBoundChild(0);
        string ownerId = materialized ? "deck-owner" : "deck";
        string surfaceId = materialized ? "captured-floor" : "deck/deck-support/floor";
        var original = f.Assets.GetPrefab("hut");
        var owner = (MapPrefabPlacementChild)original.Children.Single(c => c.Key.Key == ownerId);
        var value = MapPrefabReferenceWalker.Copy(owner.Value, ownerId, new(MapSupportBindingKind.Surface, "exterior", null, null, null, null));
        value.Y = null;
        owner = owner with { Value = value, References = new[] { new MapPrefabReferenceSlot("/value/supportBinding/surfaceId", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, "exterior")) } };
        var child = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "consumer"), new()
        {
            Id = "consumer",
            Kind = "item",
            AssetId = "item",
            X = owner.Value.X + 0.5f,
            Z = owner.Value.Z - 0.5f,
            Y = null,
            Scale = 1,
            SupportBinding = space ? new(MapSupportBindingKind.Space, null, "provider-space", 0, 10, 10) : new(MapSupportBindingKind.Surface, surfaceId, null, null, null, null)
        })
        { References = Slots(space ? Slot("/value/supportBinding/spaceId", MapPrefabChildKind.Record, "provider-space") : Surf("/value/supportBinding/surfaceId", surfaceId)) };
        var children = original.Children.Where(c => c is not MapPrefabPlacementChild).Prepend(owner).Append(child).ToList();
        if (space)
        {
            var anchor = new MapPatchKey(surfaceId, 0, 0);
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "provider-space"),
                new MapSpaceDoc("provider-space", MapSpaceKind.Exterior, null, null, [], [], [], []), anchor)
            { LocalTransform = new(new(materialized ? 0 : owner.Value.X, 0, materialized ? 0 : owner.Value.Z), 0, 1), References = Slots(Surf("/anchor/surfaceId", surfaceId)) });
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "provider-domain"),
                new MapSpaceFootprint("provider-domain", new("provider-space", anchor), anchor, new[] { 0, 1, 64, 65 }, new(MapBoundKind.SupportFloor, surfaceId, null), new(MapBoundKind.OpenTop, null, null)), anchor)
            { LocalTransform = new(new(materialized ? 0 : owner.Value.X, 0, materialized ? 0 : owner.Value.Z), 0, 1), References = Slots(Surf("/anchor/surfaceId", surfaceId), Slot("/value/space/id", MapPrefabChildKind.Record, "provider-space"), Surf("/value/space/anchor/surfaceId", surfaceId), Surf("/value/lattice/surfaceId", surfaceId), Surf("/value/lower/surfaceId", surfaceId)) });
        }
        var asset = original with { Children = children.ToArray() };
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Transform = new(Vector3.Zero, 0, 1), Bindings = new([]) } }, []);
        var floor = Patch("exterior", 16, 16, 400);
        f.Document.Surfaces.Refs.Add(Surface("exterior", MapSurfaceRole.SupportFloor));
        floor.Records.Add(new MapSpaceDoc("exterior-world", MapSpaceKind.Exterior, null, null, [], [], [], []));
        f.Document.Surfaces.Patches.Add(floor.Key, floor);
        if (space) f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { ParentSpace = new("exterior-world", floor.Key) } }, []);
        return Reasset(f, asset, new Dictionary<string, string> { [ownerId] = "provider", ["consumer"] = "consumer" }, materialized ? null : AssetsWithSupport(asset, f.Assets.BorrowSupport("deck-support")));
    }
}
