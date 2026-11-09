using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Ecs;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

internal static class NativeCellHandoffFixtures
{
    internal static readonly MapWorldGrids Grids = new(64, 1, 1, Vector2.Zero);
    internal static readonly MapNavTileOptions Nav = new(2, "cell-profile", "cell-controller");
    internal static MapWorldBuildOptions Options => NativeWorldFixtures.Options();

    internal static NativeSceneFixture Create()
    {
        var (assets, roots) = NativeWorldFixtures.Assets();
        var document = new MapDocument
        {
            Id = "invented-cell-handoff",
            TileSize = 64,
            NativeAssets = roots.ToList(),
            ResolverIdentity = new(1, 2),
            SupportRecipe = MapSupportRecipe.AuthoredBindingsV2,
            Bounds = new() { MinX = -192, MinZ = -64, MaxX = 192, MaxZ = 128 },
            PlayableBounds = new() { MinX = -192, MinZ = -64, MaxX = 192, MaxZ = 128 },
            NumericIdHighWaterMark = 2,
        };
        foreach (int height in new[] { 5, 35 })
        {
            string id = "floor-" + height;
            document.Surfaces.Refs.Add(new(id, new(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0),
                MapSurfaceRole.SupportFloor, MapPresencePolicy.Native, null, null, ""));
            for (int slot = 0; slot < 2; slot++)
            {
                var patch = new MapSurfacePatch
                {
                    Key = new(id, slot, 0),
                    CellMinX = slot == 0 ? 56 : 0,
                    CellMinZ = 8,
                    Width = 8,
                    Depth = 4,
                    Heights = Enumerable.Repeat(height * 100, 45).ToArray(),
                    Cells = Enumerable.Repeat(new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0,
                        MapCellFlags.None, MapCellTopology.Auto), 32).ToArray(),
                    Presence = new ulong[1],
                };
                for (int z = 0; z < 4; z++)
                    for (int x = 0; x < 8; x++) patch.SetPresent(x, z, true);
                document.Surfaces.Patches.Add(patch.Key, patch);
                string ceilingId = "ceiling-" + height;
                if (slot == 0)
                    document.Surfaces.Refs.Add(new(ceilingId, document.Surfaces.Refs.First(r => r.Id == id).Frame,
                        MapSurfaceRole.Ceiling, MapPresencePolicy.Native, null, null, ""));
                var ceiling = patch.Clone();
                ceiling.Key = new(ceilingId, slot, 0);
                Array.Fill(ceiling.Heights, (height + 3) * 100);
                document.Surfaces.Patches.Add(ceiling.Key, ceiling);
                var anchor = new MapPatchKey(id, 0, 0);
                if (slot == 0) patch.Records.Add(new MapSpaceDoc("room-" + height, MapSpaceKind.Cave, null, null, [], [], [], []));
                patch.Records.Add(new MapSpaceFootprint("room-cells-" + height + "-" + slot, new("room-" + height, anchor), patch.Key,
                    Enumerable.Range(0, 32).Select(i => (8 + i / 8) * 64 + patch.CellMinX + i % 8).ToArray(),
                    new(MapBoundKind.SupportFloor, id, null), new(MapBoundKind.Ceiling, ceilingId, null)));
            }
        }
        document.Placements.Add(new()
        {
            Id = "cell-crate",
            AssetId = "crate",
            Kind = "crate",
            NumericId = 1,
            X = 64,
            Y = 5,
            Z = 9
        });
        document.BuildingIntent = new(1, [], [],
            [new(1, "cell-light", null, new(65, 7, 10), Vector3.One, 1, 4, true)],
            [new(1, "cell-marker", "Cell marker", new(69, 5, 10), 0, "landmark", true, [])]);
        Seal(document);
        return new(document, assets);
    }

    internal static MapAssetClosure CosmeticAssets(NativeSceneFixture fixture)
    {
        var assets = fixture.Assets;
        var root = assets.Roots.Single();
        var bytes = assets.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        var manifest = JsonNode.Parse(assets.BorrowVerifiedResourceBytes(root.Id).Span)!.AsObject();
        var crate = manifest["assets"]!.AsArray().Single(a => a!["id"]!.GetValue<string>() == "crate")!;
        JsonObject Bounds(int size) => new()
        {
            ["min"] = new JsonObject { ["x"] = -size, ["y"] = -size, ["z"] = -size },
            ["max"] = new JsonObject { ["x"] = size, ["y"] = size, ["z"] = size },
        };
        crate["renderBounds"] = Bounds(900);
        crate["lodBounds"] = Bounds(1000);
        crate["lightBounds"] = Bounds(1100);
        byte[] lod = Encoding.UTF8.GetBytes("invented-lod-with-large-cosmetic-bounds");
        bytes.Add("cosmetic-lod", lod);
        manifest["resources"]!.AsArray().Add(new JsonObject
        {
            ["reference"] = new JsonObject
            {
                ["id"] = "cosmetic-lod",
                ["path"] = "assets/cosmetic-lod",
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(lod)),
                ["payloadVersion"] = 1
            },
            ["kind"] = "Lod",
            ["dependencies"] = new JsonArray(),
        });
        crate["lodResourceIds"] = new JsonArray("cosmetic-lod");
        byte[] payload = Encoding.UTF8.GetBytes(manifest.ToJsonString(MapJsonTextOptions.CreateSerializer(false)));
        bytes[root.Id] = payload;
        var changed = root with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) };
        var closure = MapAssetClosure.Load([changed], new AssetBytes(bytes));
        fixture.Document.NativeAssets = closure.Roots.ToList();
        return closure;
    }

    sealed class AssetBytes(IReadOnlyDictionary<string, byte[]> bytes) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => bytes[reference.Id];
    }

    internal static void Seal(MapDocument document)
    {
        var digests = document.Surfaces.Patches.Select(p => new KeyValuePair<MapPatchKey, string>(p.Key,
            MapSurfaceSemantics.PatchDigest(p.Value))).ToArray();
        for (int i = 0; i < document.Surfaces.Refs.Count; i++)
        {
            var surface = document.Surfaces.Refs[i];
            document.Surfaces.Refs[i] = surface with { SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface, digests.Where(p => p.Key.SurfaceId == surface.Id)) };
        }
    }

    internal static MapSceneScope Domain(int cell, float minY = 4, float maxY = 8, float margin = 2) => new(
        new(default, new(cell * 64 - margin, -margin), new(cell * 64 + 64 + margin, 64 + margin),
            minY, maxY, Enum.GetValues<MapSurfaceRole>(), null, new()),
        new(cell * 64, minY, 0, cell * 64 + 64, maxY, 64), MapSceneChannels.Terrain | MapSceneChannels.Placements);

    internal static MapSceneScope Visual(float x) => new(
        new(default, new(x - 4, 8), new(x + 4, 12), -500, 500, Enum.GetValues<MapSurfaceRole>(), null, new()),
        new(x - 4, -500, 8, x + 4, 500, 12), MapSceneChannels.All, 64);

    internal struct Actor : IComponent { public Vector3 Position; }
    internal static ShardHost Host()
    {
        var registry = new ReplicationRegistry();
        registry.Register<Actor>(1, (v, w) => { w.Write(v.Position.X); w.Write(v.Position.Y); w.Write(v.Position.Z); },
            r => new() { Position = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) });
        return new(64, 0.1f, registry, 64, 4, Locate, physicsFactory: _ => new BepuPhysicsWorld());
    }

    static bool Locate(World world, Entity entity, out float x, out float z)
    {
        var position = world.Get<Actor>(entity).Position;
        x = position.X; z = position.Z;
        return true;
    }

    internal static void Install(CellSim cell, MapSimulationLease lease)
    {
        Assert.Equal(MapSceneStatus.CompleteForScope, lease.Status);
        foreach (var descriptor in lease.Worlds.SelectMany(w => w.Statics).DistinctBy(s => s.OwnerId))
            cell.Physics!.AddStatic(descriptor.Shape, new(descriptor.Position, descriptor.Orientation));
    }

    internal static float Support(CellSim cell, float x) => cell.Physics!.Raycast(new(x, 7, 10), -Vector3.UnitY,
        4, out var hit) ? hit.Point.Y : throw new InvalidOperationException("Missing cell support.");

    // Consumer policy owns its cell domains and retains a separate minimum lease for the cell's lifetime.
    internal sealed class Admission(MapSimulationCoverage coverage, IReadOnlyList<MapSceneScope> domains) : ICellImportAdmission
    {
        internal CellAdmissionOutcome LastOutcome;
        internal int Releases;
        internal Action? Published;
        public CellAdmissionRead Acquire(World staged, IReadOnlyDictionary<long, Entity> entities, CellImportPurpose purpose)
        {
            var lease = coverage.Acquire(domains);
            bool complete = lease.Witness.CompleteForScope;
            if (complete)
            {
                try
                {
                    foreach (var world in lease.Worlds)
                    {
                        var tiles = MapNavTiling.Partition(world, Grids, Nav);
                        _ = MapNavTiling.Seams(tiles, world, Nav);
                        _ = MapNavTiling.Links(world, Grids);
                    }
                    foreach (var entity in entities.Values)
                    {
                        var position = staged.Get<Actor>(entity).Position;
                        int supported = 0;
                        foreach (var world in lease.Worlds)
                        {
                            var bounds = world.Bounds;
                            if (position.X < bounds.MinX || position.X >= bounds.MaxX || position.Z < bounds.MinZ || position.Z >= bounds.MaxZ) continue;
                            using var admitted = world.Admission.Enter();
                            var support = new MapSupportQuery(world.Surfaces).Select(new(new(default, position), null, null, null, 0.1f, 0.1f, null));
                            if (support.Status == MapSupportStatus.Supported) supported++;
                            else complete = false;
                        }
                        if (supported != 1) complete = false;
                    }
                }
                catch (MapDocumentException) { complete = false; }
            }
            LastOutcome = complete ? CellAdmissionOutcome.Accepted : CellAdmissionOutcome.Unresolved;
            return new(LastOutcome, new Release(lease, () => { Releases++; if (complete) { Assert.NotEmpty(lease.Worlds); Published?.Invoke(); } }));
        }
    }

    sealed class Release(IDisposable lease, Action released) : IDisposable
    {
        public void Dispose() { released(); lease.Dispose(); }
    }
}
