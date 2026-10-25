using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Physics;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;
using static KhaozEngine.Tests.MapDoc.Physics.NativeCellHandoffFixtures;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapSimulationCoverageAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaddingOnlyCollider_MatchesCompleteNavigationAndNeighbour(bool tiled)
    {
        using var fixture = Create();
        RemoveUpperLayer(fixture.Document);
        fixture.Document.Placements.Add(new()
        {
            Id = "padding-only",
            Kind = "crate",
            AssetId = "crate",
            X = 65.25f,
            Y = 5,
            Z = 10,
            Scale = 0.5f,
        });
        var source = NativeSceneFixtures.Open(fixture, tiled);
        using var lease = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([Domain(0), Domain(1)]);
        Assert.Equal(MapSceneStatus.CompleteForScope, lease.Status);
        var complete = MapWorldBuilder.Build(fixture.Document, fixture.Assets, Options);
        var fullTiles = MapNavTiling.Partition(complete, Grids, Nav);
        foreach (var world in lease.Worlds)
        {
            Assert.Contains(world.Witness.Required, k => k == new MapSceneResourceKey("Placement", "padding-only"));
            var descriptor = Assert.Single(world.Statics, s => s.OwnerId == "padding-only");
            Assert.Equal(Assert.Single(complete.Statics, s => s.OwnerId == "padding-only").Digest, descriptor.Digest);
            var tile = Assert.Single(MapNavTiling.Partition(world, Grids, Nav));
            var full = Assert.Single(fullTiles, t => t.Coord == tile.Coord);
            Assert.Equal(full.GeometryDigest, tile.GeometryDigest);
            Assert.Equal(full.CaptureIdentity, tile.CaptureIdentity);
            Assert.Equal(full.MediumDigest, tile.MediumDigest);
        }
        Assert.Equal((0d, 64d), (lease.Worlds[0].Bounds.MinX, lease.Worlds[0].Bounds.MaxX));
        Assert.Equal((64d, 128d), (lease.Worlds[1].Bounds.MinX, lease.Worlds[1].Bounds.MaxX));
        Assert.Same(lease.Worlds[0].Admission, lease.Worlds[1].Admission);
    }

    [Fact]
    public void PaddingOnlyDependencyMissing_RefusesDestinationPublication()
    {
        using var fixture = Create();
        fixture.Document.Placements.Add(new()
        {
            Id = "destination-padding-only",
            Kind = "crate",
            AssetId = "crate",
            X = 62.75f,
            Y = 5,
            Z = 10,
            Scale = 0.5f,
        });
        var ready = NativeSceneFixtures.Open(fixture, false);
        var key = new MapSceneResourceKey("Placement", "destination-padding-only");
        // Retain the attested discovery metadata, but make this required producer unavailable.
        var source = new MapSceneSource(ready.Data with
        {
            Producing = ready.Data.Producing.Where(p => p.Key != key).ToDictionary(p => p.Key, p => p.Value),
        });
        Assert.NotNull(source.Acquire(Domain(1)).Scene);
        using var host = Host();
        var entity = host.SpawnOwned(63, 10, 101, out var origin);
        origin.World.Set(entity, new Actor { Position = new(65, 5, 10) });
        var destination = host.CellFor(65, 10);
        int publications = 0;
        var gate = new Admission(new(source, fixture.Assets, Options), [Domain(1)]) { Published = () => publications++ };
        destination.ImportAdmission = gate;
        host.ProcessHandoffs();
        Assert.Equal(CellAdmissionOutcome.Unresolved, gate.LastOutcome);
        Assert.False(destination.TryGetOwned(101, out _));
        Assert.True(origin.World.IsAlive(entity));
        Assert.Equal(0, publications);
        using (var refused = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([Domain(1)]))
        {
            Assert.Empty(refused.Worlds);
            Assert.Contains(key, refused.Witness.Unavailable);
        }
        var recovered = new Admission(new(ready, fixture.Assets, Options), [Domain(1)]) { Published = () => publications++ };
        destination.ImportAdmission = recovered;
        host.ProcessHandoffs();
        Assert.Equal(CellAdmissionOutcome.Accepted, recovered.LastOutcome);
        Assert.Equal(1, publications);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ScopedNavigation_UsesCapturedDocumentGridValidation(bool links, bool range)
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        // Mutating the producer cannot change the storage size of the captured source.
        fixture.Document.TileSize = 32;
        using var lease = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([Domain(0)]);
        var world = Assert.Single(lease.Worlds);
        var grids = range ? Grids with { Origin = new(137438953472f, 0) } : Grids with { StorageTileSize = 32 };
        _ = grids.NavTileOf(default); // Intrinsically valid, including the extreme grid's index at the origin.
        long before = world.Admission.RetainedBytes;
        var error = Assert.Throws<MapDocumentException>(() =>
        {
            if (links) _ = MapNavTiling.Links(world, grids);
            else _ = MapNavTiling.Partition(world, grids, Nav);
        });
        Assert.Contains("grid alignment", error.Message, StringComparison.Ordinal);
        Assert.Contains(range ? "one block" : "document's tile size 64", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, world.Admission.RetainedBytes);
        Assert.Null(MapSceneAdmission.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedComplexAsset_ChargesEveryScaledShapeAndRaisedEnvelopeBeforeAllocation(bool compound)
    {
        using var fixture = Create();
        const int points = 8192;
        var assets = ComplexAssets(fixture.Assets, points, compound);
        var placements = Enumerable.Range(0, 6).Select(i => new MapResolvedPlacement("repeated-" + i, "crate", "crate", null,
            new(new(i, 5, 10), 0, 2), [])).ToArray();
        IReadOnlyList<MapPlacementGeometry>? admitted = null;
        (long Bytes, long Reads) Measure(int count)
        {
            var admission = new MapSceneAdmission(new());
            using (admission.Enter())
            {
                var result = MapPlacementShapes.Prepare(placements.Take(count).ToArray(), assets, null);
                Assert.All(result, p => Assert.True(p.Envelope.RaiseMetres > 0));
                admitted = result;
            }
            return (admission.RetainedBytes, admission.PayloadReads);
        }
        var one = Measure(1);
        var two = Measure(2);
        long instance = two.Bytes - one.Bytes;
        // A scaled copy plus the raised hull's doubled vertices, independently of the shared decoder.
        Assert.True(instance >= 3L * points * 12);
        Assert.Equal(1, one.Reads);
        Assert.Equal(one.Reads, two.Reads);
        // Five live outputs exceed the initial decoder peak, so the sixth shape is the binding stage.
        var five = Measure(5);
        int preparations = 0;
        var bounded = new MapSceneAdmission(new(MaxPreparedBytes: checked(five.Bytes + points * 12L - 1)),
            stage => { if (stage == "geometry") preparations++; });
        using (bounded.Enter())
            Assert.Throws<MapSceneCapacityException>(() => MapPlacementShapes.Prepare(placements, assets, null));
        Assert.Equal(5, preparations);
        Assert.Equal(0, bounded.RetainedBytes);
        Assert.Equal(0, bounded.WorkspaceBytes);
        Assert.Null(MapSceneAdmission.Current);
        // Released callers retain their existing unmetered path and output.
        var released = MapPlacementShapes.Prepare(placements, assets, null);
        Assert.Equal(6, released.Count);
        Assert.Equal(admitted!.Take(2).Select(p => p.Digest), released.Take(2).Select(p => p.Digest));
    }

    static void RemoveUpperLayer(MapDocument document)
    {
        document.Surfaces.Refs.RemoveAll(s => s.Id.EndsWith("35", StringComparison.Ordinal));
        foreach (var key in document.Surfaces.Patches.Keys.Where(k => k.SurfaceId.EndsWith("35", StringComparison.Ordinal)).ToArray())
            document.Surfaces.Patches.Remove(key);
        Seal(document);
    }

    static MapAssetClosure ComplexAssets(MapAssetClosure assets, int count, bool compound)
    {
        var points = Enumerable.Range(0, count).Select(i => new Vector3(
            0.2f * (float)Math.Cos(i * Math.Tau / count), i % 2 * 0.1f, 0.2f * (float)Math.Sin(i * Math.Tau / count))).ToArray();
        PhysicsShape shape = new ConvexHullShape(points);
        if (compound) shape = new CompoundShape([new(shape, Pose.Identity)]);
        byte[] collision = NativeWorldFixtures.ColliderBytes(shape);
        var root = assets.Roots.Single();
        var bytes = assets.CapturedResources.ToDictionary(r => r.Reference.Id, r => r.Bytes.ToArray(), StringComparer.Ordinal);
        bytes["crate.collider"] = collision;
        var manifest = JsonNode.Parse(assets.BorrowVerifiedResourceBytes(root.Id).Span)!.AsObject();
        var resource = manifest["resources"]!.AsArray().Single(r => r!["reference"]!["id"]!.GetValue<string>() == "crate.collider")!;
        resource["reference"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(collision));
        byte[] payload = Encoding.UTF8.GetBytes(manifest.ToJsonString(MapJsonTextOptions.CreateSerializer(false)));
        bytes[root.Id] = payload;
        return MapAssetClosure.Load([root with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) }], new AssetBytes(bytes));
    }

    sealed class AssetBytes(IReadOnlyDictionary<string, byte[]> bytes) : IMapAssetSource
    {
        public ReadOnlyMemory<byte> Read(MapAssetRef reference) => bytes[reference.Id];
    }
}
