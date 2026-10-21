using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;
using static KhaozEngine.Tests.MapDoc.Physics.NativeCellHandoffFixtures;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapSimulationCoverageTests
{
    [Fact]
    public void MultiCellGhostHandoff_RenderAndNavStayContinuous()
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, true);
        var coverage = new MapSimulationCoverage(source, fixture.Assets, Options);
        using var minimum = coverage.Acquire([Domain(0), Domain(1)]);
        Assert.Equal(MapSceneStatus.CompleteForScope, minimum.Status);
        Assert.All(minimum.Worlds, w => Assert.False(w.Witness.WholeWorldComplete));
        Assert.Contains(minimum.Worlds.SelectMany(w => w.Markers), m => m.Id == "cell-marker");
        using var host = Host();
        var entity = host.SpawnOwned(63, 10, 101, out var origin);
        origin.World.Set(entity, new Actor { Position = new(63, 5, 10) });
        var destination = host.CellFor(65, 10);
        Install(origin, minimum);
        Install(destination, minimum);
        var gate = new Admission(coverage, [Domain(1)]);
        destination.ImportAdmission = gate;
        host.SyncGhosts();
        bool destinationHasExpectedGhost = destination.TryGetGhost(101, out var ghost) &&
            destination.World.Get<Actor>(ghost).Position == new Vector3(63, 5, 10);
        Assert.True(destinationHasExpectedGhost);
        Assert.Equal(5, Support(origin, 63));
        var beforeVisualUnloadStatics = Statics(minimum);
        var beforeVisualUnloadNavDigests = NavDigests(minimum);
        using (var visual = MapRequiredDomainCoverage.Acquire(source, [Visual(-128)]))
            Assert.True(visual.Witness.CompleteForScope);
        using (var visual = MapRequiredDomainCoverage.Acquire(source, [Visual(128)]))
        {
            Assert.True(visual.Witness.CompleteForScope);
            Assert.Contains(Assert.Single(visual.Scenes).Placements, p => p.PlacementId == "cell-crate");
        }
        int publications = 0;
        gate.Published = () =>
        {
            Assert.True(destination.TryGetOwned(101, out _));
            Assert.Equal(1, host.OwnerCount(101));
            Assert.NotEmpty(minimum.Worlds);
            publications++;
        };
        origin.World.Set(entity, new Actor { Position = new(65, 5, 10) });
        host.ProcessHandoffs();
        host.SyncGhosts();
        Assert.Equal(1, host.OwnerCount(101));
        Assert.True(destination.TryGetOwned(101, out var adopted));
        Assert.Equal(new Vector3(65, 5, 10), destination.World.Get<Actor>(adopted).Position);
        Assert.True(origin.TryGetGhost(101, out _));
        Assert.Equal(5, Support(destination, 65));
        Assert.Equal(1, gate.Releases);
        Assert.Equal(1, publications);
        host.ProcessHandoffs();
        Assert.Equal(1, publications);
        var afterVisualUnloadStatics = Statics(minimum);
        var afterVisualUnloadNavDigests = NavDigests(minimum);
        Assert.Equal(beforeVisualUnloadStatics, afterVisualUnloadStatics);
        Assert.Equal(beforeVisualUnloadNavDigests, afterVisualUnloadNavDigests);
        Assert.Contains(minimum.Worlds.SelectMany(w => w.Markers), m => m.Id == "cell-marker");
        Assert.Contains(minimum.Worlds.SelectMany(w => w.Lights), l => l.Id == "cell-light");
        var west = MapNavTiling.Partition(minimum.Worlds[0], Grids, Nav).Single();
        var east = MapNavTiling.Partition(minimum.Worlds[1], Grids, Nav).Single();
        Assert.Equal(66, west.CaptureBounds.MaxX);
        Assert.Equal(62, east.CaptureBounds.MinX);
        Assert.Equal(west.GeometryDigest, east.GeometryDigest);
    }

    [Fact]
    public void SimulationCoverage_RemainsWithoutObservers()
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        var coverage = new MapSimulationCoverage(source, fixture.Assets, Options);
        using var first = coverage.Acquire([Domain(0), Domain(1, 34, 38)]);
        using var second = coverage.Acquire([Domain(0)]);
        Assert.All(first.Worlds[0].Terrain.Chunks.Where(c => c.ChunkId.StartsWith("floor-", StringComparison.Ordinal)), c => Assert.Equal(5, c.Bounds.MinY));
        Assert.All(first.Worlds[1].Terrain.Chunks.Where(c => c.ChunkId.StartsWith("floor-", StringComparison.Ordinal)), c => Assert.Equal(35, c.Bounds.MinY));
        var before = Statics(second);
        first.Dispose();
        first.Dispose();
        Assert.Empty(first.Worlds);
        Assert.Equal(before, Statics(second));
        Assert.NotEmpty(second.Worlds.Single().Statics);
        second.Dispose();
        Assert.Empty(second.Worlds);
    }

    [Fact]
    public void MissingDestination_RefusesAdmissionBeforePublication()
    {
        using var fixture = Create();
        using var storage = NativeSceneFixtures.OpenTiled(fixture);
        var source = MapSceneSource.Capture(storage, fixture.Assets).Source!;
        var coverage = new MapSimulationCoverage(source, fixture.Assets, Options);
        string page = Directory.GetFiles(Path.Combine(fixture.StorageDirectories.Single(), "tiles", "surfaces", "p"), "*.json", SearchOption.AllDirectories)
            .First(p => p.Contains("surfaces", StringComparison.Ordinal) && File.ReadAllText(p).Contains("floor-5", StringComparison.Ordinal));
        byte[] saved = File.ReadAllBytes(page);
        File.Delete(page);
        using var host = Host();
        var entity = host.SpawnOwned(63, 10, 101, out var origin);
        origin.World.Set(entity, new Actor { Position = new(65, 5, 10) });
        var destination = host.CellFor(65, 10);
        int publicationsBeforeDestinationReady = 0;
        var gate = new Admission(coverage, [Domain(1)]) { Published = () => publicationsBeforeDestinationReady++ };
        destination.ImportAdmission = gate;
        host.ProcessHandoffs();
        var missingDestinationAdmission = gate.LastOutcome;
        Assert.Equal(CellAdmissionOutcome.Unresolved, missingDestinationAdmission);
        Assert.Equal(0, publicationsBeforeDestinationReady);
        Assert.False(destination.TryGetOwned(101, out _));
        Assert.True(origin.World.IsAlive(entity));
        Assert.Equal(1, host.PendingMigrationAdmissions);
        File.WriteAllBytes(page, saved);
        using var minimum = coverage.Acquire([Domain(1)]);
        Install(destination, minimum);
        host.ProcessHandoffs();
        Assert.Equal(CellAdmissionOutcome.Accepted, gate.LastOutcome);
        Assert.Equal(1, host.OwnerCount(101));
        Assert.Equal(1, publicationsBeforeDestinationReady);
        Assert.Equal(5, Support(destination, 65));
    }

    [Fact]
    public void CosmeticBounds_NeverSetNavigationTileHeights()
    {
        using var fixture = Create();
        fixture.Document.Placements.Add(new()
        {
            Id = "cosmetically-discovered",
            Kind = "crate",
            AssetId = "crate",
            X = 160,
            Y = 50,
            Z = 10,
        });
        var beforeSource = NativeSceneFixtures.Open(fixture, false);
        var domain = Domain(0, maxY: 60);
        Assert.DoesNotContain(beforeSource.Acquire(domain).Scene!.Placements, p => p.PlacementId == "cosmetically-discovered");
        using var before = new MapSimulationCoverage(beforeSource, fixture.Assets, Options).Acquire([domain]);
        var cosmeticAssets = CosmeticAssets(fixture);
        var afterSource = NativeSceneFixtures.Open(fixture with { Assets = cosmeticAssets }, false);
        Assert.Contains(afterSource.Acquire(domain).Scene!.Placements, p => p.PlacementId == "cosmetically-discovered");
        using var after = new MapSimulationCoverage(afterSource, cosmeticAssets, Options).Acquire([domain]);
        Assert.Contains(after.Witness.Required, k => k == new MapSceneResourceKey("Placement", "cosmetically-discovered"));
        var beforeCosmeticEditTileHeights = Heights(before);
        var afterCosmeticEditTileHeights = Heights(after);
        Assert.Equal(beforeCosmeticEditTileHeights, afterCosmeticEditTileHeights);
        Assert.InRange(beforeCosmeticEditTileHeights.Single().Item1, 4, 6);
        Assert.Equal(38, beforeCosmeticEditTileHeights.Single().Item2);
        Assert.Equal(NavDigests(before), NavDigests(after));
        Assert.DoesNotContain(after.Worlds.Single().Statics, s => s.OwnerId == "cosmetically-discovered");
    }

    [Fact]
    public void MissingSeamMargin_DoesNotAdmitDestination()
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        var coverage = new MapSimulationCoverage(source, fixture.Assets, Options);
        var gate = new Admission(coverage, [Domain(1, margin: 0)]);
        using var read = gate.Acquire(new(), new System.Collections.Generic.Dictionary<long, KhaozEngine.Ecs.Entity>(), CellImportPurpose.Transfer);
        Assert.Equal(CellAdmissionOutcome.Unresolved, read.Outcome);
    }

    [Fact]
    public void Coverage_RefusesAtomicallyAndSharesAdmissionAcrossDomains()
    {
        using var fixture = Create();
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var baseline = MapSceneSource.Capture(document, fixture.Assets).Source!.Acquire(Domain(0));
        int scopes = baseline.Scene!.SurfaceView.Scope.SceneAdmission!.PatchScopes;
        Assert.True(scopes > 0);
        var source = MapSceneSource.Capture(document, fixture.Assets, new(MaxPatchScopes: scopes)).Source!;
        Assert.NotNull(source.Acquire(Domain(0)).Scene);
        using var refused = MapRequiredDomainCoverage.Acquire(source, [Domain(0), Domain(1)]);
        Assert.Equal(MapSceneStatus.CapacityExceeded, refused.Status);
        Assert.False(refused.Witness.CompleteForScope);
        Assert.Empty(refused.Scenes);
        using var empty = MapRequiredDomainCoverage.Acquire(source, []);
        Assert.Equal(MapSceneStatus.Invalid, empty.Status);
        Assert.Empty(empty.Scenes);
    }

    [Fact]
    public void ScopedStatics_ChargeCompilationBeforePublishing()
    {
        using var fixture = Create();
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var measured = MapSceneSource.Capture(document, fixture.Assets).Source!.Acquire(Domain(0));
        Assert.NotNull(measured.Scene);
        var bounded = MapSceneSource.Capture(document, fixture.Assets,
            new(MaxPreparedFaces: checked((int)measured.Work.PreparedFaces))).Source!;
        var scene = bounded.Acquire(Domain(0));
        Assert.NotNull(scene.Scene);
        var result = MapScopedWorldBuilder.Build(scene.Scene, fixture.Assets, Options);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Null(result.World);
        Assert.False(result.Witness.CompleteForScope);
    }

    [Fact]
    public void VerticalDomain_DoesNotInstallUnrelatedLowerStatics()
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        using var lease = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([Domain(0, 34, 38)]);
        var world = Assert.Single(lease.Worlds);
        Assert.DoesNotContain(world.Statics, s => s.OwnerId == "cell-crate");
        Assert.All(MapNavTiling.Partition(world, Grids, Nav), t => Assert.Equal((35d, 38d), (t.Bounds.MinY, t.Bounds.MaxY)));
    }

    [Fact]
    public void EmptyOrAmbiguousSupport_RemainsUnresolved()
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        var staged = new KhaozEngine.Ecs.World();
        var actor = staged.Spawn();
        staged.Set(actor, new Actor { Position = new(65, 15, 10) });
        var entities = new System.Collections.Generic.Dictionary<long, KhaozEngine.Ecs.Entity> { [101] = actor };
        var coverage = new MapSimulationCoverage(source, fixture.Assets, Options);
        using (var empty = new Admission(coverage, [Domain(1, 14, 18)]).Acquire(staged, entities, CellImportPurpose.Transfer))
            Assert.Equal(CellAdmissionOutcome.Unresolved, empty.Outcome);
        var duplicate = fixture.Document.Surfaces.Patches.Values.First(p => p.Key.SurfaceId == "floor-5" && p.Key.SlotX == 1).Clone();
        duplicate.Key = duplicate.Key with { SurfaceId = "independent-floor" };
        duplicate.Records.Clear();
        duplicate.Records.Add(new KhaozEngine.MapDoc.Spaces.MapSpaceFootprint("independent-cells", new("room-5", new("floor-5", 0, 0)), duplicate.Key,
            Enumerable.Range(0, 32).Select(i => (8 + i / 8) * 64 + i % 8).ToArray(),
            new(KhaozEngine.MapDoc.Spaces.MapBoundKind.SupportFloor, "independent-floor", null),
            new(KhaozEngine.MapDoc.Spaces.MapBoundKind.Ceiling, "ceiling-5", null)));
        fixture.Document.Surfaces.Patches.Add(duplicate.Key, duplicate);
        fixture.Document.Surfaces.Refs.Add(fixture.Document.Surfaces.Refs.First(s => s.Id == "floor-5") with { Id = "independent-floor" });
        Seal(fixture.Document);
        var ambiguous = new MapSimulationCoverage(NativeSceneFixtures.Open(fixture, false), fixture.Assets, Options);
        staged.Set(actor, new Actor { Position = new(65, 5, 10) });
        using var read = new Admission(ambiguous, [Domain(1)]).Acquire(staged, entities, CellImportPurpose.Transfer);
        Assert.Equal(CellAdmissionOutcome.Unresolved, read.Outcome);
    }

    [Fact]
    public void ScopedNavigation_UsesReleasedSeamAndLinkIdentities()
    {
        var fixture = NativeWorldFixtures.StackedCave();
        fixture.Document.TileSize = 16;
        for (int i = 0; i < fixture.Document.Surfaces.Refs.Count; i++)
        {
            var surface = fixture.Document.Surfaces.Refs[i];
            fixture.Document.Surfaces.Refs[i] = surface with { Frame = surface.Frame with { CellUnitMetres = new(4, 1) } };
        }
        Seal(fixture.Document);
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, fixture.Assets).Source!;
        var scope = new MapSceneScope(new(default, new(-2), new(34), -1, 8,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(0, -1, 0, 32, 8, 32), MapSceneChannels.All);
        using var lease = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([scope]);
        var world = Assert.Single(lease.Worlds);
        var grids = new MapWorldGrids(16, 1, 4, Vector2.Zero);
        var complete = MapWorldBuilder.Build(fixture.Document, fixture.Assets, Options);
        var scopedTiles = MapNavTiling.Partition(world, grids, Nav);
        var fullTiles = MapNavTiling.Partition(complete, grids, Nav);
        Assert.Equal(fullTiles.Where(t => Inside(t.Coord)), scopedTiles);
        var links = MapNavTiling.Links(world, grids);
        Assert.NotEmpty(links);
        Assert.Equal(MapNavTiling.Links(complete, grids), links);
        var seams = MapNavTiling.Seams(scopedTiles, world, Nav);
        Assert.NotEmpty(seams);
        Assert.Equal(MapNavTiling.Seams(fullTiles, complete, Nav).Where(s => Inside(s.A) && Inside(s.B)), seams);

        static bool Inside(MapNavTileCoord c) => c.X is >= 0 and < 2 && c.Z is >= 0 and < 2;
    }

    [Fact]
    public void ScopedNavigation_RefusesCapacityBeforeDescriptorPreparation()
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        using var lease = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([Domain(0)]);
        var world = Assert.Single(lease.Worlds);
        world.Admission.ReserveBytes(source.Data.Limits.MaxPreparedBytes - world.Admission.RetainedBytes);
        Assert.Throws<MapDocumentException>(() => MapNavTiling.Partition(world, Grids, Nav));
        Assert.Null(MapSceneAdmission.Current);
    }

    [Fact]
    public void MissingVerticalCaptureMargin_RefusesNavigation()
    {
        using var fixture = Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        using var lease = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([Domain(1, minY: 5)]);
        var world = Assert.Single(lease.Worlds);
        Assert.True(world.Bounds.MinY < 5);
        Assert.Throws<MapDocumentException>(() => MapNavTiling.Partition(world, Grids, Nav));
    }

    [Theory]
    [InlineData("triangles")]
    [InlineData("cells")]
    [InlineData("domain")]
    [InlineData("traversal")]
    public void WaterPreparation_SharesSceneAdmissionBeforeWork(string operation)
    {
        var fixture = NativeWorldFixtures.BridgeOverRiver();
        var meter = new MapWaterWorkMeter(new());
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0, MaxPreparedFaces: 0));
        using (admission.Enter())
        {
            Assert.Throws<MapSceneCapacityException>(() =>
            {
                if (operation == "triangles") meter.ChargeTriangles(1);
                else if (operation == "cells") meter.ChargeFootprintCells(1);
                else if (operation == "domain") meter.ChargeDomain(fixture.Document.Water!.Bodies[0].Domain);
                else meter.ChargeBoundTraversal(BigInteger.One, BigInteger.One);
            });
        }
        Assert.Equal(0, admission.PreparedFaces);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void ScopedWater_UsesReleasedPhysicalDescriptors()
    {
        var fixture = NativeWorldFixtures.BridgeOverRiver();
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, fixture.Assets).Source!;
        var domain = new MapSceneScope(new(default, new(-66), new(66), -4, 6,
            Enum.GetValues<MapSurfaceRole>(), null, new()), new(-64, -4, -64, 64, 6, 64), MapSceneChannels.All);
        using var lease = new MapSimulationCoverage(source, fixture.Assets, Options).Acquire([domain]);
        var world = Assert.Single(lease.Worlds);
        var body = Assert.Single(world.Water);
        var complete = MapWorldBuilder.Build(fixture.Document, fixture.Assets, Options);
        Assert.Equal(MapWaterCompileStatus.Complete, body.Status);
        Assert.Equal(Assert.Single(complete.Water).Digest, body.Digest);
        Assert.Equal(MapNavTiling.Partition(complete, Grids, Nav), MapNavTiling.Partition(world, Grids, Nav));
    }

    static string[] Statics(MapSimulationLease lease) => lease.Worlds.SelectMany(w => w.Statics)
        .Select(s => s.OwnerId + "|" + s.Digest).ToArray();
    static string[] NavDigests(MapSimulationLease lease) => lease.Worlds.SelectMany(w => MapNavTiling.Partition(w, Grids, Nav))
        .Select(t => t.GeometryDigest + "|" + t.CaptureIdentity).ToArray();
    static (double, double)[] Heights(MapSimulationLease lease) => lease.Worlds.SelectMany(w => MapNavTiling.Partition(w, Grids, Nav))
        .Select(t => (t.Bounds.MinY, t.Bounds.MaxY)).ToArray();
}
