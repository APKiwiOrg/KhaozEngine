using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Bounded water in the built world: never a static, part of the build hash, its own residency entries, a
/// navigation tile medium digest only where water meets a tile, and invalidation of exactly the tiles a water edit
/// reaches.</summary>
public class MapWaterWorldTests
{
    static readonly MapWorldGrids Grids = new(64f, 2, 4, Vector2.Zero);
    static readonly MapNavTileOptions Nav = new(0.5f, "test-profile", "test-controller");
    static readonly MapNavTileCoord[] Wet = { new(0, -1), new(0, 0) };

    [Fact]
    public void Water_IsNeverAPhysicsStatic()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildBridgeOverRiver();
        Assert.DoesNotContain(world.Statics, s => s.OwnerId == "river");
        using var physics = new BepuPhysicsWorld();
        using var registration = MapPhysicsRegistration.Register(world, physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Equal(world.Statics.Count, registration.Handles.Count);
        Assert.Equal(MapStaticKind.TerrainChunk, NativeWorldFixtures.DownwardHit(registration, lease, new Vector3(-3f, 10f, 6f)).Kind);
    }

    [Fact]
    public void BuildHash_FollowsTheWater()
    {
        WaterWorldFixture fixture = NativeWorldFixtures.BridgeOverRiver();
        MapBuiltWorld a = NativeWorldFixtures.Build(fixture), b = NativeWorldFixtures.Build(fixture);
        Assert.Equal((a.BuildHash, a.WaterDigest), (b.BuildHash, b.WaterDigest));
        fixture.Document.Water!.Bodies[0] = fixture.Document.Water.Bodies[0] with { SurfaceY = -0.5f };
        MapBuiltWorld c = NativeWorldFixtures.Build(NativeWorldFixtures.Reresolve(fixture));
        Assert.NotEqual(a.BuildHash, c.BuildHash);
        Assert.NotEqual(a.WaterDigest, c.WaterDigest);
        MapBuiltWorld dry = NativeWorldFixtures.BuildBridge();
        Assert.Equal(("", 0), (dry.WaterDigest, dry.Water.Count));
    }

    [Fact]
    public void WaterResidency_FollowsTheWetRegions()
    {
        MapResidencyEntry river = Assert.Single(MapResidencyOwnership.BuildWater(NativeWorldFixtures.BuildLRiver(), Grids));
        Assert.Equal(new[] { new MapTileCoord(0, -1), new MapTileCoord(0, 0), new MapTileCoord(1, 0) },
            river.Membership.OrderBy(t => t.X).ThenBy(t => t.Z));
    }

    [Fact]
    public void NavTiles_CarryAMediumDigestOnlyWhereWaterMeetsThem()
    {
        var wet = MapNavTiling.Partition(NativeWorldFixtures.BuildLRiver(), Grids, Nav).ToDictionary(t => t.Coord);
        var dry = MapNavTiling.Partition(NativeWorldFixtures.BuildDryTwinOfLRiver(), Grids, Nav).ToDictionary(t => t.Coord);
        Assert.Equal(dry.Keys.OrderBy(c => c.X).ThenBy(c => c.Z), wet.Keys.OrderBy(c => c.X).ThenBy(c => c.Z));
        Assert.Equal(Wet, wet.Values.Where(t => t.MediumDigest.Length > 0).Select(t => t.Coord).OrderBy(c => c.X).ThenBy(c => c.Z));
        foreach (var (coord, tile) in wet)
        {
            Assert.Equal(dry[coord].GeometryDigest, tile.GeometryDigest);
            Assert.Equal(!Wet.Contains(coord), dry[coord].CaptureIdentity == tile.CaptureIdentity);
        }
    }

    [Fact]
    public void MediumEdit_ChangesWetTileIdentityOnly()
    {
        var before = MapNavTiling.Partition(NativeWorldFixtures.BuildLRiver(), Grids, Nav).ToDictionary(t => t.Coord);
        var after = MapNavTiling.Partition(NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(wadeSpeedScale: 0.5f)), Grids, Nav).ToDictionary(t => t.Coord);
        foreach (var (coord, tile) in before)
            Assert.Equal(!Wet.Contains(coord), after[coord].CaptureIdentity == tile.CaptureIdentity);
    }

    [Fact]
    public void RemoteFloorEdit_ChangesNoUnrelatedIdentity_AndReportsEveryChangedTile()
    {
        MapBuiltWorld before = NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(withPond: true));
        MapBuiltWorld after = NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(withPond: true, pondFloorLowered: true));
        Assert.Equal(before.Water.Single(b => b.BodyId == "river").Digest, after.Water.Single(b => b.BodyId == "river").Digest);
        Assert.NotEqual(before.Water.Single(b => b.BodyId == "pond").Digest, after.Water.Single(b => b.BodyId == "pond").Digest);
        var a = MapNavTiling.Partition(before, Grids, Nav).ToDictionary(t => t.Coord);
        var b = MapNavTiling.Partition(after, Grids, Nav).ToDictionary(t => t.Coord);
        // The edited tile's identity must change, so the coverage check below cannot pass vacuously.
        Assert.NotEqual(a[new MapNavTileCoord(1, -1)].CaptureIdentity, b[new MapNavTileCoord(1, -1)].CaptureIdentity);
        var affected = MapNavTiling.AffectedTiles(before, Grids, Nav, NativeWorldFixtures.PondFloorEditEffects()).ToHashSet();
        foreach (var (coord, tile) in a)
            if (tile.CaptureIdentity != b[coord].CaptureIdentity) Assert.Contains(coord, affected);
        foreach (MapNavTileCoord coord in Wet) Assert.Equal(a[coord].MediumDigest, b[coord].MediumDigest);
    }

    [Fact]
    public void MetadataOnlyBedEdit_ChangesTheAffectedBodyAndTilesOnly()
    {
        MapBuiltWorld before = NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(withPond: true));
        MapBuiltWorld after = NativeWorldFixtures.Build(NativeWorldFixtures.LRiver(withPond: true, pondFloorHalfHeightUnit: true));
        Assert.Equal(before.Water.Single(b => b.BodyId == "river").Digest, after.Water.Single(b => b.BodyId == "river").Digest);
        Assert.NotEqual(before.Water.Single(b => b.BodyId == "pond").Digest, after.Water.Single(b => b.BodyId == "pond").Digest);
        var a = MapNavTiling.Partition(before, Grids, Nav).ToDictionary(t => t.Coord);
        var b = MapNavTiling.Partition(after, Grids, Nav).ToDictionary(t => t.Coord);
        Assert.NotEqual(a[new MapNavTileCoord(1, -1)].MediumDigest, b[new MapNavTileCoord(1, -1)].MediumDigest);
        foreach (MapNavTileCoord coord in Wet) Assert.Equal(a[coord].MediumDigest, b[coord].MediumDigest);
    }

    [Fact]
    public void WaterEdits_InvalidateExactlyTheirTiles()
    {
        MapBuiltWorld world = NativeWorldFixtures.BuildLRiver();
        MapNativeEditEffects effects = NativeWorldFixtures.RiverLevelEditEffects();
        Assert.Contains("water:river", MapResidencyOwnership.Affected(world, Grids, effects).Owners);
        Assert.Equal(Wet, MapNavTiling.AffectedTiles(world, Grids, Nav, effects).OrderBy(c => c.X).ThenBy(c => c.Z));
    }
}
