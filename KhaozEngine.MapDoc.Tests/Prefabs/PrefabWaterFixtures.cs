using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    internal static MapFramePoint PoolProbe(MapEffectiveResources resources) => WaterPoint(resources, "a/pool-water", 6, 0, -6);
    internal static MapFramePoint DryCavePoint(MapEffectiveResources resources) => WaterPoint(resources, "a/lake", 2, -55, -2);
    internal static MapFramePoint FloodedCavePoint(MapEffectiveResources resources) => WaterPoint(resources, "a/lake", 2, -37, -2);
    internal static MapExactPoint WaterExact(MapEffectiveResources resources, string body, float x, float y, float z) =>
        new MapResourceTransform(resources.Transforms["WaterBody:" + body]).Point(new(MapExactValue.FromSingle(x),
            MapExactValue.FromSingle(y), MapExactValue.FromSingle(z)));
    static MapFramePoint WaterPoint(MapEffectiveResources resources, string body, float x, float y, float z) =>
        MapEffectiveGeometry.Presentation(resources.CompleteView(), WaterExact(resources, body, x, y, z));

    internal static PrefabFixture ExteriorWaterDryRoom()
    {
        var f = Create();
        f.Document.Water = new();
        f.Document.Water.Media.Add(new("water", MapWaterMediumKind.Water, 1));
        f.Document.Water.Bodies.Add(new("ocean", 4, "water", "pool-material", Domain(0, 0, 16, 16),
            new[] { new MapRecordRef("exterior", new("exterior", 0, 0)) }));
        return f;
    }

    internal static PrefabFixture WorldAndLocalWater()
    {
        var f = Create("all-resources");
        var patch = Patch("far-exterior", 16, 16, -200);
        patch.CellMinX = patch.CellMinZ = 16;
        patch.Records.Add(new MapSpaceDoc("far-exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("far-domain", new("far-exterior", patch.Key), patch.Key,
            Enumerable.Range(16, 16).SelectMany(z => Enumerable.Range(16, 16).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "far-exterior", null), new(MapBoundKind.OpenTop, null, null)));
        f.Document.Surfaces.Patches.Add(patch.Key, patch);
        f.Document.Surfaces.Refs.Add(Surface("far-exterior", MapSurfaceRole.SupportFloor));
        SealWorldParent(f.Document);
        f.Document.Water = new();
        f.Document.Water.Media.Add(new("world-water", MapWaterMediumKind.Water, 1));
        f.Document.Water.Bodies.Add(new("world-ocean", 0, "world-water", "pool-material", Domain(16, 16, 32, 32),
            new[] { new MapRecordRef("far-exterior", patch.Key) }));
        return f;
    }

    internal static PrefabFixture WaterCaves()
    {
        var f = Create();
        var children = new List<MapPrefabChild>();
        Add("dry", -6000, -5000);
        Add("flooded", -4000, -3000);
        children.Add(new MapPrefabMediumChild(new(MapPrefabChildKind.WaterMedium, "water"), new("water", MapWaterMediumKind.Water, 1)));
        children.Add(new MapPrefabWaterChild(new(MapPrefabChildKind.WaterBody, "lake"), new("lake", -35, "water", "pool-material",
            Domain(0, 0, 4, 4), new[] { new MapRecordRef("flooded", new("flooded-floor", 0, 0)) }))
        { References = Slots(Slot("/value/medium", MapPrefabChildKind.WaterMedium, "water"), Ref("/value/covers/0", "flooded", "flooded-floor")) });
        f.Document.Water = new();
        f.Document.Water.Media.Add(new("ocean-water", MapWaterMediumKind.Water, 1));
        f.Document.Water.Bodies.Add(new("ocean", 0, "ocean-water", "pool-material", Domain(0, 0, 16, 16),
            new[] { new MapRecordRef("exterior", new("exterior", 0, 0)) }));
        return Reasset(f, new(1, "hut", children), children.ToDictionary(c => c.Key.Key, c => "a/" + c.Key.Key));

        void Add(string id, int floor, int ceiling)
        {
            var anchor = new MapPatchKey(id + "-floor", 0, 0);
            children.Add(SurfaceChild(id + "-floor", MapSurfaceRole.SupportFloor, Patch(id + "-floor", 4, 4, floor)));
            children.Add(SurfaceChild(id + "-ceiling", MapSurfaceRole.Ceiling, Patch(id + "-ceiling", 4, 4, ceiling)));
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, id), new MapSpaceDoc(id, MapSpaceKind.Cave,
                null, null, [], [], [], []), anchor)
            { References = Slots(Anchor(id + "-floor")) });
            children.Add(new MapPrefabRecordChild(new(MapPrefabChildKind.Record, id + "-domain"), new MapSpaceFootprint(id + "-domain",
                new(id, anchor), anchor, Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 4).Select(x => z * 64 + x)).ToArray(),
                new(MapBoundKind.SupportFloor, id + "-floor", null), new(MapBoundKind.Ceiling, id + "-ceiling", null)), anchor)
            {
                References = Slots(Anchor(id + "-floor"), Ref("/value/space", id, id + "-floor"), Surf("/value/lattice/surfaceId", id + "-floor"),
                Surf("/value/lower/surfaceId", id + "-floor"), Surf("/value/upper/surfaceId", id + "-ceiling"))
            });
        }
    }

    internal static PrefabFixture RoomWater(bool seam = false, bool connected = false)
    {
        var f = seam ? Seam() : Create();
        var asset = f.Assets.GetPrefab("hut");
        var children = asset.Children.ToList();
        var ids = new Dictionary<string, string> { ["water"] = "a/water", ["room-water"] = "a/room-water" };
        if (seam)
            foreach (var child in Create("all-resources").Assets.GetPrefab("hut").Children.Where(c =>
                c.Key.Key is "pool-floor" or "pool-ceiling" or "pool" or "pool-domain"))
            { children.Add(child); ids.Add(child.Key.Key, "a/" + child.Key.Key); }
        var room = children.OfType<MapPrefabRecordChild>().Single(c => c.Value is MapSpaceDoc { Id: "room" });
        var covers = new List<MapRecordRef> { new("room", room.Anchor) };
        var slots = new List<MapPrefabReferenceSlot> { Slot("/value/medium", MapPrefabChildKind.WaterMedium, "water"),
            Ref("/value/covers/0", "room", room.Anchor.SurfaceId)[0], Ref("/value/covers/0", "room", room.Anchor.SurfaceId)[1] };
        if (connected)
        {
            covers.Add(new("exterior", new("exterior", 0, 0)));
            slots.AddRange(Ref("/value/covers/1", "exterior", "exterior", MapPrefabReferenceScope.World));
        }
        children.Add(new MapPrefabMediumChild(new(MapPrefabChildKind.WaterMedium, "water"), new("water", MapWaterMediumKind.Water, 1)));
        children.Add(new MapPrefabWaterChild(new(MapPrefabChildKind.WaterBody, "room-water"), new("room-water", 0.5f, "water", "pool-material",
            Domain(0, 0, 4, 4), covers))
        { References = slots });
        return Reasset(f, asset with { Children = children }, ids);
    }

    internal static PrefabFixture WaterLevel(PrefabFixture f, float level)
    {
        var asset = f.Assets.GetPrefab("hut");
        return Reasset(f, asset with
        {
            Children = asset.Children.Select(c => c is MapPrefabWaterChild w ? w with
            { Value = w.Value with { SurfaceY = level } } : c).ToArray()
        });
    }
    internal static PrefabFixture MarkerOnlyWaterEdit(PrefabFixture f)
    {
        var asset = f.Assets.GetPrefab("hut");
        return Reasset(f, asset with
        {
            Children = asset.Children.Select(c => c is MapPrefabMarkerChild m ? m with
            { Value = m.Value with { Name = "Another entry" } } : c).ToArray()
        });
    }

    internal static PrefabFixture TangentWater()
    {
        var f = WaterCaves();
        var asset = f.Assets.GetPrefab("hut");
        var top = asset.Children.OfType<MapPrefabSurfaceChild>().Single(c => c.Key.Key == "flooded-ceiling");
        foreach (var patch in top.Patches)
            for (int z = 0; z <= patch.Depth; z++)
                for (int x = 0; x <= patch.Width; x++) patch.Heights[z * (patch.Width + 1) + x] = -3000 + 100 * Math.Abs(x - 2);
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with
        { Transform = new(new(10.25f, 5, -3.125f), 0.371f, 1.2f) } }, []);
        return Reasset(f, asset with
        {
            Children = asset.Children.Select(c => c is MapPrefabWaterChild w ? w with
            { Value = w.Value with { SurfaceY = -30 } } : c).ToArray()
        });
    }
    internal static PrefabFixture DisconnectedWater()
    {
        var f = RoomWater(connected: true);
        var asset = f.Assets.GetPrefab("hut");
        return Reasset(f, asset with { Children = asset.Children.Where(c => c.Key.Key != "room-portal").ToArray() });
    }

    internal static PrefabFixture ExternalApertureWater(float offset)
    {
        var f = RoomWater(connected: true);
        var asset = f.Assets.GetPrefab("hut");
        var anchor = new MapPatchKey("aperture-lattice", 0, 0);
        var patch = Patch("aperture-lattice", 4, 4, 0);
        foreach (var (id, height) in new[] { ("aperture-bottom", 0), ("aperture-top", 220) })
            patch.Records.Add(new MapBoundaryChain(id, MapChainKind.Authored, null, new[]
            {
                new MapChainVertex(new(anchor.SurfaceId, MapLatticeAddress.Create(7, 0, 5)), height),
                new MapChainVertex(new(anchor.SurfaceId, MapLatticeAddress.Create(13, 0, 5)), height),
            }));
        f.Document.Surfaces.Patches.Add(anchor, patch);
        f.Document.Surfaces.Refs.Add(Surface(anchor.SurfaceId, MapSurfaceRole.SupportFloor));
        SealWorldParent(f.Document);
        f.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, anchor.SurfaceId,
            new(0, offset, 0, 0, 1), new string('a', 64)) };
        return Reasset(f, asset with
        {
            Children = asset.Children.Select(c => c is MapPrefabRecordChild { Value: MapCavePortal p } portal
            ? portal with
            {
                Value = p with { BandBottom = new("aperture-bottom", anchor), BandTop = new("aperture-top", anchor) },
                References = portal.References.Where(s => !s.Path.StartsWith("/value/bandBottom", StringComparison.Ordinal) &&
                    !s.Path.StartsWith("/value/bandTop", StringComparison.Ordinal)).Concat(Ref("/value/bandBottom", "aperture-bottom", anchor.SurfaceId, MapPrefabReferenceScope.World))
                    .Concat(Ref("/value/bandTop", "aperture-top", anchor.SurfaceId, MapPrefabReferenceScope.World)).ToArray()
            } : c).ToArray()
        });
    }

    internal static readonly (string Fault, string What, MapPatchKey Key, MapPatchStatus Status, string Detail)[] WaterFaults =
    {
        ("local-cover", "a/room", new("a/remote-anchor", 1, 0), MapPatchStatus.Missing, "a/room"),
        ("prism", "a/room-domain", new("a/floor", 0, 0), MapPatchStatus.Missing, "a/room-domain"),
        ("parent-floor", "patch", new("exterior", 0, 0), MapPatchStatus.Unloaded, "exterior"),
        ("remote-anchor", "a/room", new("a/remote-anchor", 1, 0), MapPatchStatus.Unloaded, "a/remote-anchor")
    };
    internal static (IReadOnlyList<MapAssetRef> Roots, CaptureSource Source) WaterCaptureInputs()
    {
        var (roots, source) = Inputs(AssetBytes("all-resources"), true);
        return (roots, new(source));
    }
    internal static MapScopedSurfaces AcquireWaterFault(MapEffectiveResources resources, string fault) => MapScopedSurfaces.Acquire(
        new WaterFaultSource((IMapSurfaceAcquisitionSource)resources.SurfaceSource, fault), Scope(resources), new[] { resources.Digest }, 2, 2);

    sealed class WaterFaultSource(IMapSurfaceAcquisitionSource source, string fault) : IMapSurfaceAcquisitionSource, IMapEffectiveSurfaceContext
    {
        public MapEffectiveContext EffectiveContext => ((IMapEffectiveSurfaceContext)source).EffectiveContext;
        public string SnapshotId => source.SnapshotId;
        public string RootSha256 => source.RootSha256;
        public IReadOnlyList<MapSurfaceRef> Surfaces => source.Surfaces;
        MapPatchRead Fault(MapPatchRead read)
        {
            if (fault == "parent-floor" && read.Key.SurfaceId == "exterior" || fault == "remote-anchor" && read.Key.SurfaceId == "a/remote-anchor")
                return read with { Status = MapPatchStatus.Unloaded, Patch = null, SemanticSha256 = null };
            string? id = fault switch { "local-cover" => "a/room", "prism" => "a/room-domain", "aperture" => "a/door-bottom", _ => null };
            if (id is null || read.Patch is not { } original) return read;
            var patch = original.Clone();
            patch.Records.RemoveAll(r => r.Id == id);
            return read with { Patch = patch, SemanticSha256 = MapSurfaceSemantics.PatchDigest(patch) };
        }
        public MapPatchRead ReadPatch(MapPatchKey key) => Fault(source.ReadPatch(key));
        public MapPatchFindResult FindPatches(MapSurfaceScope scope) { using var s = OpenAcquisition(scope); return s.FindPatches(); }
        public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => new Session(this, source.OpenAcquisition(scope));
        sealed class Session(WaterFaultSource owner, IMapSurfaceAcquisitionSession source) : IMapSurfaceAcquisitionSession
        {
            public string SnapshotId => source.SnapshotId;
            public string RootSha256 => source.RootSha256;
            public int PagesRead => source.PagesRead;
            public IReadOnlyList<MapPatchKey> ReservedPatchKeys => source.ReservedPatchKeys;
            public bool TryGetSurface(string id, out MapSurfaceRef? surface) => source.TryGetSurface(id, out surface);
            public MapPatchRead ReadPatch(MapPatchKey key) => owner.Fault(source.ReadPatch(key));
            public bool TryGetIncidentRecords(MapPatchKey key, out IReadOnlyList<MapRecordRef>? records) => source.TryGetIncidentRecords(key, out records);
            public MapPatchFindResult FindPatches()
            {
                var result = source.FindPatches();
                var reads = result.Patches.Select(owner.Fault).ToArray();
                return result with
                {
                    Patches = reads.Where(r => r.Status == MapPatchStatus.Present).ToArray(),
                    Unavailable = result.Unavailable.Concat(reads.Where(r => r.Status != MapPatchStatus.Present)).ToArray()
                };
            }
            public void Dispose() => source.Dispose();
        }
    }
}
