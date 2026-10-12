using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    internal static MapEffectiveResources Effective(PrefabFixture fixture) => MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
    internal static MapFramePoint Point(MapEffectiveResources resources, float x, float y, float z) => new(resources.CompleteView().Scope.Frame, new(x, y, z));
    internal static PrefabFixture Transformed(float yaw, float scale, Vector3 position)
    {
        var f = Create("all-resources");
        var instance = f.Document.Prefabs!.Instances.Single() with { Transform = new(position, yaw, scale) };
        f.Document.Prefabs = new(1, 1, new[] { instance }, []);
        AuthorWorldParent(f, instance.Transform);
        return f;
    }
    internal static MapExactPoint SharedFloorCorner(MapEffectiveResources resources) => Corner(resources, "a/floor", 4, 4);
    internal static MapExactPoint SharedRoomCorner(MapEffectiveResources resources)
    {
        var view = resources.CompleteView();
        var prism = view.RecordsIn(new("a/floor", 0, 0)).OfType<MapPrismDomain>().Single();
        var corner = MapPrismGeometry.Compile(view, prism).Pieces.SelectMany(p => p.Ring)
            .Single(p => p == new MapExactXz(SharedFloorCorner(resources).X, SharedFloorCorner(resources).Z));
        return new(corner.X, StoreyUpper(resources), corner.Z);
    }
    internal static MapExactPoint SharedWaterCorner(MapEffectiveResources resources) => resources.WaterCorners["a/pool-water"].First();
    internal static MapExactValue StoreyUpper(MapEffectiveResources resources) => Corner(resources, "a/ceiling", 4, 4).Y;
    static MapExactPoint Corner(MapEffectiveResources resources, string id, long x, long z)
    {
        var patch = MapEffectiveGeometry.Patch(resources.CompleteView(), new(id, 0, 0));
        int index = patch.VertexIds.ToList().FindIndex(v => v.Address == MapLatticeAddress.Corner(x, z));
        return patch.ExactVertices[index];
    }
    internal static PrefabFixture Decomposed(bool far)
    {
        var f = Transformed(0.371f, 1.2f, far ? new(30000.25f, -450, -29999.75f) : new(10.25f, 5, -3.125f));
        var original = f.Assets.GetPrefab("hut");
        var deck = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "deck"), new MapPlacement
        { Id = "deck", Kind = "deck", AssetId = "deck", X = 4, Y = 0, Z = -4, Scale = 1 });
        var asset = original with { Children = original.Children.Append(deck).ToArray() };
        var support = DeckSupport();
        foreach (var patch in support.Surfaces.Patches.Values)
        {
            Array.Fill(patch.Heights, 0);
            for (int i = 0; i < patch.Records.Count; i++)
                if (patch.Records[i] is MapBoundaryChain chain) patch.Records[i] = chain with { Vertices = chain.Vertices.Select(v => v with { HeightUnits = 0 }).ToArray() };
        }
        return Reasset(f, asset, new Dictionary<string, string> { ["deck"] = "a/deck" }, AssetsWithSupport(asset, support));
    }
    internal static (MapExactPoint Direct, MapExactPoint OffsetSupport) DecomposedCorners(MapEffectiveResources resources)
    {
        var direct = SharedFloorCorner(resources);
        var id = resources.Surfaces.Refs.Single(s => s.Id.Contains("deck-support", StringComparison.Ordinal)).Id;
        var offset = Corner(resources, id, 0, 0);
        return (direct, offset);
    }
    internal static PrefabFixture Sloped(bool far, float scale)
    {
        var f = Transformed(0.371f, scale, far ? new(30000.25f, -450, -29999.75f) : new(10.25f, 5, -3.125f));
        var asset = f.Assets.GetPrefab("hut");
        var floor = (MapPrefabSurfaceChild)asset.Children.Single(c => c.Key == new MapPrefabChildKey(MapPrefabChildKind.Surface, "floor"));
        foreach (var patch in floor.Patches)
            for (int z = 0; z <= patch.Depth; z++)
                for (int x = 0; x <= patch.Width; x++) patch.Heights[z * (patch.Width + 1) + x] = x * 25;
        // The parent uses its own world-aligned lattice. Its ramp stays below the transformed room floor.
        var instance = f.Document.Prefabs!.Instances.Single().Transform;
        var (sin, cos) = Math.SinCos(instance.YawRadians);
        foreach (var parent in f.Document.Surfaces.Patches.Values.Where(p => p.Key.SurfaceId == "exterior"))
            for (int z = 0; z <= parent.Depth; z++)
                for (int x = 0; x <= parent.Width; x++)
                {
                    double worldX = parent.Key.SlotX * 64 + parent.CellMinX + x;
                    double worldZ = -(parent.Key.SlotZ * 64 + parent.CellMinZ + z);
                    double along = cos * (worldX - instance.Position.X) - sin * (worldZ - instance.Position.Z);
                    parent.Heights[z * (parent.Width + 1) + x] = (int)Math.Round(100 * (instance.Position.Y - 2 * instance.Scale + along * 0.25));
                }
        SealWorldParent(f.Document);
        return Reasset(f, asset);
    }
    static PrefabFixture Reasset(PrefabFixture f, MapPrefabAsset asset, IReadOnlyDictionary<string, string>? ids = null, MapAssetClosure? captured = null)
    {
        var assets = captured ?? LoadAssets(MapPrefabFile.Write(asset), true);
        var document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(f.Document));
        document.NativeAssets = assets.Roots.ToList();
        var instance = document.Prefabs!.Instances.Single() with { ResourceSha256 = assets.GetResource("hut").Reference.Sha256 };
        document.Prefabs = new(1, 1, new[] { instance }, []);
        var plan = MapPrefabBindingPlanner.Plan(document, asset, assets, "a", instance.Bindings);
        if (ids is not null) plan = plan with { Bindings = new(plan.Bindings.Entries.Select(b => ids.TryGetValue(b.Child.Key, out string? id) ? b with { WorldId = id } : b).ToArray()) };
        document.Prefabs = new(1, 1, new[] { instance with { Bindings = plan.Bindings } }, []);
        document.NumericIdHighWaterMark = plan.HighWaterMark;
        return new(document, assets);
    }
    internal static PrefabFixture MaterializedOverrideFixture()
    {
        var f = Create("materialized");
        var source = f.Assets.GetPrefab("hut");
        var surface = SurfaceChild("captured-floor", MapSurfaceRole.SupportFloor, Patch("captured-floor", 2, 2, 50));
        var light = new MapPrefabLightChild(new(MapPrefabChildKind.Light, "captured-light"), GlowLight() with
        { Id = "captured-light", Owner = new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "deck-owner") });
        var owner = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "deck-owner"), new MapPlacement
        { Id = "deck-owner", Kind = "deck", AssetId = "deck", X = 4, Y = 0, Z = -4, Scale = 1 });
        MapPrefabLocalMaterializedChild Captured(MapPrefabChild child) => new(new(MapPrefabReferenceScope.Local, child.Key.Kind, child.Key.Key),
            new(0, 0, 0, 0, 1), MapPrefabIdentity.ChildDigest(child));
        var asset = source with
        {
            Children = new MapPrefabChild[] { owner, surface, light },
            MaterializedAttachments = new[]
            {
                new MapPrefabLocalMaterialization(new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "deck-owner"), "deck-support",
                    ResourceSha("deck-support"), MapPrefabAttachmentKind.Support, new(4, 0, -4, 0, 1), new[] { Captured(surface) }),
                new MapPrefabLocalMaterialization(new(MapPrefabReferenceScope.Local, MapPrefabChildKind.Placement, "deck-owner"), "glow-light",
                    ResourceSha("glow-light"), MapPrefabAttachmentKind.Light, new(4, 0, -4, 0, 1), new[] { Captured(light) }),
            },
        };
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Bindings = new([]) } }, []);
        return Reasset(f, asset, new Dictionary<string, string> { ["deck-owner"] = "deck-owner", ["captured-floor"] = "captured-floor", ["captured-light"] = "captured-light" });
    }
    internal static MapPrefabOverride MaterializedTransformOverride(string kind) => kind switch
    {
        "owner-placement" => new MapPrefabPlacementOverride(new(MapPrefabChildKind.Placement, "deck-owner"), new(new(7.25f, 0, -2.5f), 0, 1), null, null),
        "owner-transform" => new MapPrefabTransformOverride(new(MapPrefabChildKind.Placement, "deck-owner"), new(new(7.25f, 0, -2.5f), 0, 1)),
        "floor" => new MapPrefabTransformOverride(new(MapPrefabChildKind.Surface, "captured-floor"), new(new(-0.5f, 0.25f, 0.75f), 0, 1)),
        "light" => new MapPrefabTransformOverride(new(MapPrefabChildKind.Light, "captured-light"), new(new(0.75f, 0.5f, -1.25f), 0, 1)),
        _ => throw new ArgumentException(kind),
    };
    internal static PrefabFixture WithMaterializedOverride(PrefabFixture fixture, MapPrefabOverride value)
    {
        var document = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        document.Prefabs = new(1, 1, new[] { document.Prefabs!.Instances.Single() with { Overrides = new[] { value } } }, []);
        return new(document, fixture.Assets);
    }
    internal static (MapExactPoint Floor, MapExactPoint Light) CapturedCoordinates(MapEffectiveResources resources) =>
        (Corner(resources, "captured-floor", 0, 0), resources.LightPoints["captured-light"]);
    internal static IReadOnlyList<string> CapturedSupportOwners(MapEffectiveResources resources) => resources.Surfaces.Refs
        .Where(s => s.Role == MapSurfaceRole.SupportFloor && s.Id != "ground").Select(s => s.Id).ToArray();
    internal static PrefabFixture SupportBoundChild(float parentY)
    {
        var f = Create("attached");
        var deck = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "deck"), new MapPlacement
        { Id = "deck", Kind = "deck", AssetId = "deck", Y = 0, Scale = 1 });
        var item = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "low-item"), new MapPlacement
        {
            Id = "low-item",
            Kind = "item",
            AssetId = "item",
            Y = null,
            Scale = 1,
            SupportBinding = new(MapSupportBindingKind.Surface, "deck/deck-support/floor", null, null, null, null)
        })
        { References = Slots(Surf("/value/supportBinding/surfaceId", "deck/deck-support/floor")) };
        var asset = new MapPrefabAsset(1, "hut", new MapPrefabChild[] { deck, item });
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with
        { Transform = new(new(10.25f, parentY, -3.125f), 0.371f, 1.2f), Bindings = new([]) } }, []);
        var surfaces = new MapSurfaceSet();
        surfaces.Refs.Add(Surface("floor", MapSurfaceRole.SupportFloor));
        var patch = Patch("floor", 2, 2, 0);
        surfaces.Patches.Add(patch.Key, patch);
        var support = new MapSupportResource(1, surfaces, null, Slots(Surf("/patches/0/key/surfaceId", "floor")));
        return Reasset(f, asset, new Dictionary<string, string> { ["low-item"] = "low-item" }, AssetsWithSupport(asset, support));
    }
    static MapAssetClosure AssetsWithSupport(MapPrefabAsset asset, MapSupportResource support)
    {
        var (roots, source) = Inputs(MapPrefabFile.Write(asset), true);
        byte[] supportBytes = MapSupportResourceFile.Write(support);
        var manifest = JsonNode.Parse(source.Read(roots[0]).Span)!.AsObject();
        manifest["resources"]!.AsArray().Single(r => r!["reference"]!["id"]!.GetValue<string>() == "deck-support")!["reference"]!["sha256"] =
            Convert.ToHexStringLower(SHA256.HashData(supportBytes));
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        var root = roots[0] with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) };
        var closure = MapAssetClosure.Load(new[] { root }, new ChangedSource(source, manifestBytes, supportBytes));
        return closure;
    }

    internal static MapPrefabAsset ImplicitReferenceAsset(string target, MapPrefabChildKind kind = MapPrefabChildKind.Surface)
    {
        var deck = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "deck"), new MapPlacement
        { Id = "deck", Kind = "deck", AssetId = "deck", Y = 0, Scale = 1 });
        var item = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "low-item"), new MapPlacement
        {
            Id = "low-item",
            Kind = "item",
            AssetId = "item",
            Y = null,
            Scale = 1,
            SupportBinding = new(MapSupportBindingKind.Surface, target, null, null, null, null)
        })
        { References = Slots(Slot("/value/supportBinding/surfaceId", kind, target)) };
        return new(1, "hut", new MapPrefabChild[] { deck, item });
    }
    internal static PrefabFixture ExtremePose(float instanceScale, float childScale)
    {
        var f = Create("roomless");
        var asset = f.Assets.GetPrefab("hut");
        ((MapPrefabPlacementChild)asset.Children.Single()).Value.Scale = childScale;
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Transform = new(Vector3.Zero, 0, instanceScale) } }, []);
        return Reasset(f, asset);
    }
    internal static PrefabFixture AttachedRecord()
    {
        var f = Create("attached");
        var asset = ImplicitReferenceAsset("deck/deck-support/top");
        var deck = (MapPrefabPlacementChild)asset.Children.Single(c => c.Key.Key == "deck");
        deck.Value.X = 4; deck.Value.Z = -4;
        const string surface = "deck/deck-support/top";
        var rail = new MapPrefabRecordChild(new(MapPrefabChildKind.Record, "rail"), new MapBoundaryChain("rail", MapChainKind.Authored, null,
            new[] { new MapChainVertex(new(surface, MapLatticeAddress.Corner(0, 0)), 50), new MapChainVertex(new(surface, MapLatticeAddress.Corner(2, 0)), 50) }), new(surface, 0, 0))
        { LocalTransform = new(new(4, 0, -4), 0, 1), References = Slots(Anchor(surface), Surf("/value/vertices/0/vertex/surfaceId", surface), Surf("/value/vertices/1/vertex/surfaceId", surface)) };
        f.Document.Prefabs = new(1, 1, new[] { f.Document.Prefabs!.Instances.Single() with { Bindings = new([]) } }, []);
        return Reasset(f, asset with { Children = new MapPrefabChild[] { deck, rail } }, new Dictionary<string, string> { ["rail"] = "a/rail" });
    }
    internal static PrefabFixture Seam()
    {
        var f = Create();
        var asset = f.Assets.GetPrefab("hut");
        var anchor = new MapPatchKey("remote-anchor", 1, 0);
        var remote = Patch("remote-anchor", 1, 1, 0); remote.Key = anchor;
        var children = asset.Children.Select(child => child switch
        {
            MapPrefabRecordChild { Value: MapSpaceDoc { Id: "room" } } room => room with
            { Anchor = anchor, References = room.References.Select(r => r.Path == "/anchor/surfaceId" ? r with { Target = r.Target with { Id = "remote-anchor" } } : r).ToArray() },
            MapPrefabRecordChild { Value: MapPrismDomain domain } record when domain.Space.Id == "room" => record with
            { Value = domain with { Space = new("room", anchor) }, References = record.References.Select(r => r.Path == "/value/space/anchor/surfaceId" ? r with { Target = r.Target with { Id = "remote-anchor" } } : r).ToArray() },
            MapPrefabRecordChild { Value: MapCavePortal portal } record when portal.FromSpace.Id == "room" => record with
            { Value = portal with { FromSpace = new("room", anchor) }, References = record.References.Select(r => r.Path == "/value/fromSpace/anchor/surfaceId" ? r with { Target = r.Target with { Id = "remote-anchor" } } : r).ToArray() },
            _ => child,
        }).Append(SurfaceChild("remote-anchor", MapSurfaceRole.SupportFloor, remote)).ToArray();
        return Reasset(f, asset with { Children = children }, new Dictionary<string, string> { ["remote-anchor"] = "a/remote-anchor" });
    }
    internal static MapScopedSurfaces AcquireWithout(MapEffectiveResources resources, string dependencyId) =>
        MapScopedSurfaces.Acquire(new FaultSurfaceSource((IMapSurfaceAcquisitionSource)resources.SurfaceSource, dependencyId),
            Scope(resources), new[] { resources.Digest }, 2, 2);
    internal static MapSurfaceScope Scope(MapEffectiveResources resources)
    {
        var scope = resources.CompleteView().Scope;
        return scope;
    }
    sealed class FaultSurfaceSource(IMapSurfaceAcquisitionSource source, string fault) : IMapSurfaceAcquisitionSource, IMapEffectiveSurfaceContext
    {
        public MapEffectiveContext EffectiveContext => ((IMapEffectiveSurfaceContext)source).EffectiveContext;
        public string SnapshotId => source.SnapshotId;
        public string RootSha256 => source.RootSha256;
        public IReadOnlyList<MapSurfaceRef> Surfaces => source.Surfaces;
        MapPatchRead Fault(MapPatchRead read)
        {
            if (fault == "parent-floor" && read.Key.SurfaceId == "exterior" || fault == "remote-anchor" && read.Key.SurfaceId == "a/remote-anchor")
                return read with { Status = MapPatchStatus.Unloaded, Patch = null, SemanticSha256 = null };
            if (fault is "room-domain" or "prism" && read.Patch is { } original && original.Records.Any(r => r.Id == "a/room-domain"))
            {
                var patch = original.Clone(); patch.Records.RemoveAll(r => r.Id == "a/room-domain");
                return read with { Patch = patch, SemanticSha256 = MapSurfaceSemantics.PatchDigest(patch) };
            }
            return read;
        }
        public MapPatchRead ReadPatch(MapPatchKey key) => Fault(source.ReadPatch(key));
        public MapPatchFindResult FindPatches(MapSurfaceScope scope)
        { using var session = OpenAcquisition(scope); return session.FindPatches(); }
        public IMapSurfaceAcquisitionSession OpenAcquisition(MapSurfaceScope scope) => new Session(this, source.OpenAcquisition(scope));
        sealed class Session(FaultSurfaceSource owner, IMapSurfaceAcquisitionSession session) : IMapSurfaceAcquisitionSession
        {
            public string SnapshotId => session.SnapshotId;
            public string RootSha256 => session.RootSha256;
            public int PagesRead => session.PagesRead;
            public IReadOnlyList<MapPatchKey> ReservedPatchKeys => session.ReservedPatchKeys;
            public bool TryGetSurface(string id, out MapSurfaceRef? surface) => session.TryGetSurface(id, out surface);
            public MapPatchRead ReadPatch(MapPatchKey key) => owner.Fault(session.ReadPatch(key));
            public bool TryGetIncidentRecords(MapPatchKey key, out IReadOnlyList<MapRecordRef>? records) => session.TryGetIncidentRecords(key, out records);
            public MapPatchFindResult FindPatches()
            {
                var found = session.FindPatches(); var reads = found.Patches.Select(owner.Fault).ToArray();
                return found with
                {
                    Patches = reads.Where(r => r.Status == MapPatchStatus.Present).ToArray(),
                    Unavailable = found.Unavailable.Concat(reads.Where(r => r.Status != MapPatchStatus.Present)).ToArray()
                };
            }
            public void Dispose() => session.Dispose();
        }
    }
    internal static (IReadOnlyList<MapAssetRef> Roots, CaptureSource Source) CaptureInputs()
    {
        var (roots, source) = Inputs(AssetBytes("valid"), true);
        return (roots, new(source));
    }
    internal sealed class CaptureSource(IMapAssetSource source) : IMapAssetSource
    {
        internal bool MissingPrefab { get; set; }
        internal int PrefabReads { get; private set; }
        public ReadOnlyMemory<byte> Read(MapAssetRef reference)
        {
            if (reference.Id == "hut")
            { PrefabReads++; if (MissingPrefab) throw new System.IO.IOException("prefab unavailable"); }
            return source.Read(reference);
        }
    }
    internal static PrefabFixture PreflightRich()
    {
        var f = Create("all-resources");
        var asset = f.Assets.GetPrefab("hut");
        f = Reasset(f, asset with { Children = asset.Children.Append(SurfaceChild("large", MapSurfaceRole.SupportFloor, Patch("large", 64, 64, 0))).ToArray() });
        var basis = f.Document.Prefabs!.Instances.Single();
        long numeric = f.Document.NumericIdHighWaterMark;
        var instances = Enumerable.Range(0, 4).Select(i => basis with
        {
            Id = "instance-" + i,
            Removed = i == 3,
            Bindings = new(basis.Bindings.Entries.Select(b => b with
            {
                WorldId = "instance-" + i + "/" + b.Child.Key,
                NumericId = b.NumericId is null ? null : ++numeric,
                Removed = b.Child == new MapPrefabChildKey(MapPrefabChildKind.Marker, "entry")
            })
                .Append(new(new(MapPrefabChildKind.Marker, "retired-marker"), "instance-" + i + "/retired-marker", null, true)).ToArray()),
            Overrides = new MapPrefabOverride[] { new MapPrefabRemoveOverride(new(MapPrefabChildKind.Marker, "entry"),
                new MapPrefabTransformOverride(new(MapPrefabChildKind.Marker, "entry"), new(new(0.5f, 0, 0), 0, 1))) },
        }).ToArray();
        f.Document.NumericIdHighWaterMark = numeric;
        f.Document.Prefabs = new(1, 1, instances, new[] { new MapPrefabBindingTransfer("old", new(new(MapPrefabChildKind.Marker, "retired"), "old/retired", null, true)) { Retired = true },
            new MapPrefabBindingTransfer("old", new(new(MapPrefabChildKind.Surface, "deck/deck-support/top"), "old/top", null, true)) { Retired = true } })
        {
            Attachments = new[] { new MapPrefabAttachmentTransfer("old/deck", "deck-support", ResourceSha("deck-support"), MapPrefabAttachmentKind.Support,
                new[] { new MapPrefabAttachedChild(new(MapPrefabChildKind.Surface, "deck/deck-support/top"), "old/top", null, new(0, 0, 0, 0, 1), new string('b', 64)) }, true) },
        };
        return f;
    }
    sealed class ChangedSource(IMapAssetSource original, byte[] manifest, byte[] support) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => reference.Id switch
        { "manifest" => manifest, "deck-support" => support, _ => original.Read(reference) };
    }
    internal static MapScopedSurfaces AcquireAll(MapEffectiveResources resources) => resources.Acquire(Scope(resources));
    internal static IReadOnlyList<string> UnavailableIds(MapScopedSurfaces view) => view.Witness.Unavailable.Select(u => u.What == "a/room-domain" ? "room-domain" :
        u.Key.SurfaceId == "exterior" ? "parent-floor" : u.Key.SurfaceId == "a/remote-anchor" ? "remote-anchor" : u.What).Distinct().ToArray();
    internal static bool UnrelatedFactsRemainReadable(MapScopedSurfaces view) => view.Witness.Present.Any(p => view.Patch(p.Key).Patch is not null);
}
