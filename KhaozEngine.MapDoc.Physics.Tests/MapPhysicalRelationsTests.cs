using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Physical line of sight and walk clearance over registered statics, evaluated under a held read
/// lease.</summary>
public class MapPhysicalRelationsTests
{
    [Fact]
    public void StackedCave_LineOfSightAndClearanceKeepLevelsDistinct()
    {
        var f = NativeWorldFixtures.StackedCave();
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Equal(MapPhysicalCertainty.Blocked, r.LineOfSight(lease, f.LowerRoom, f.UpperRoom).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.LineOfSight(lease, f.LowerUnderShaft, f.UpperOverShaft).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.Clearance(lease, new[] { f.LowerRoomFeet }, MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, r.Clearance(lease, new[] { f.UnderLowCeilingFeet }, MoveTuning.Default).Certainty);
    }

    [Fact]
    public void Doorway_JambBlocksWithItsOwner_OpeningIsClear_OutsideIsUnknown()
    {
        var f = NativeWorldFixtures.Doorway(0.371f, 1.137f);
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var blocked = r.LineOfSight(lease, f.BeforeJamb, f.AfterJamb);
        Assert.Equal(MapPhysicalCertainty.Blocked, blocked.Certainty);
        Assert.Equal("doorway", blocked.BlockingOwner);
        Assert.Equal(MapPhysicalCertainty.Clear, r.LineOfSight(lease, f.BeforeOpening, f.AfterOpening).Certainty);
        Assert.Equal(MapPhysicalCertainty.Unknown, r.LineOfSight(lease, f.BeforeJamb, f.FarOutsideBounds).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, r.Clearance(lease, new[] { f.FeetBeforeJamb, f.FeetAfterJamb }, MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.Clearance(lease, new[] { f.FeetBeforeOpening, f.FeetAfterOpening }, MoveTuning.Default).Certainty);
    }

    [Fact]
    public void CaveFloorCeilingSupportAndPick_AgreeOnBothHeads()
    {
        var f = NativeWorldFixtures.StackedCave();
        using var clientPhysics = new BepuPhysicsWorld();
        using var serverPhysics = new BepuPhysicsWorld();
        using var client = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), clientPhysics);
        using var server = MapPhysicsRegistration.Register(NativeWorldFixtures.BuildStackedCave(), serverPhysics);
        using var cl = ((IPhysicsQueryLeaseSource)clientPhysics).AcquireQueryReadLease();
        using var sl = ((IPhysicsQueryLeaseSource)serverPhysics).AcquireQueryReadLease();
        Assert.Equal("cave-crate", new MapWorldQueries(client.World).Pick(f.PickRayFrom(f.OverCrate))?.PlacementId);
        foreach (var point in f.ProbePoints)
        {
            var a = new MapPhysicalRelations(client).LineOfSight(cl, point, f.UpperRoom);
            var b = new MapPhysicalRelations(server).LineOfSight(sl, point, f.UpperRoom);
            Assert.Equal((a.Certainty, a.BlockingOwner, a.BuildHash, a.TerrainWitnessDigest), (b.Certainty, b.BlockingOwner, b.BuildHash, b.TerrainWitnessDigest));
            Assert.Equal(new MapWorldQueries(client.World).Pick(f.PickRayFrom(point))?.PlacementId, new MapWorldQueries(server.World).Pick(f.PickRayFrom(point))?.PlacementId);
            var sa = new MapSupportQuery(client.World.Surfaces).Select(f.SupportRequestAt(point));
            var sb = new MapSupportQuery(server.World.Surfaces).Select(f.SupportRequestAt(point));
            Assert.Equal((sa.Status, sa.Face, sa.WorldY), (sb.Status, sb.Face, sb.WorldY));
        }
    }

    [Fact]
    public void FarRegion_RelationsHoldAfterAWholeMetreRebase()
    {
        var f = NativeWorldFixtures.StackedCaveAt(30_000f, -30_000f);
        using var physics = new BepuPhysicsWorld();
        physics.Rebase(new Vector3(30_000f, 0f, -30_000f));
        using var reg = MapPhysicsRegistration.Register(f.Build(), physics);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var r = new MapPhysicalRelations(reg);
        Assert.Equal(MapPhysicalCertainty.Blocked, r.LineOfSight(lease, f.LowerRoom, f.UpperRoom).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.LineOfSight(lease, f.LowerUnderShaft, f.UpperOverShaft).Certainty);
    }

    // The slope wall at the origin is a 2 m by 2 m by 0.2 m box from y 0 to y 2 whose +z face lies at z 0.1. The
    // default shell's radius is 0.4, so feet at z 0.5 touch that face and feet at z 0.4995 sink 0.0005 m into it.
    static MapBuiltWorld Wall()
    {
        var f = NativeWorldFixtures.Placed("slope-wall");
        return MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options());
    }

    static MapFramePoint At(float x, float y, float z) => new(WorldFrame.Origin, new Vector3(x, y, z));

    static readonly MapFramePoint Touching = At(0f, 0f, 0.4995f);

    [Fact]
    public void DisposedRegistration_RefusesBothRelations()
    {
        using var physics = new BepuPhysicsWorld();
        var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        reg.Dispose();
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        string name = typeof(MapPhysicsRegistration).FullName!;
        Assert.Equal(name, Assert.Throws<ObjectDisposedException>(() => r.LineOfSight(lease, At(0f, 1f, 2f), At(0f, 1f, -2f))).ObjectName);
        Assert.Equal(name, Assert.Throws<ObjectDisposedException>(() => r.Clearance(lease, new[] { At(0f, 0f, 2f) }, MoveTuning.Default)).ObjectName);
    }

    [Fact]
    public void PathStartingInToleratedContact_MayLeaveTheWall()
    {
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Equal(MapPhysicalCertainty.Clear, r.Clearance(lease, new[] { Touching }, MoveTuning.Default).Certainty);
        Assert.Equal(MapPhysicalCertainty.Clear, r.Clearance(lease, new[] { Touching, At(0f, 0f, 1.5f) }, MoveTuning.Default).Certainty);
    }

    [Fact]
    public void PathStartingInToleratedContact_MayNotEnterTheWall()
    {
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var into = r.Clearance(lease, new[] { Touching, At(0f, 0f, -0.5f) }, MoveTuning.Default);
        Assert.Equal((MapPhysicalCertainty.Blocked, "slope-wall"), (into.Certainty, into.BlockingOwner));
    }

    [Fact]
    public void PenetrationDeeperThanTolerance_BlocksAtTheStart()
    {
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var deep = r.Clearance(lease, new[] { At(0f, 0f, 0.49f), At(0f, 0f, 1.5f) }, MoveTuning.Default);
        Assert.Equal((MapPhysicalCertainty.Blocked, (string?)null, (float?)0f), (deep.Certainty, deep.BlockingOwner, deep.BlockDistance));
    }

    [Fact]
    public void ClearancePathOutsideOneTo64Points_Throws()
    {
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Clearance(lease, Array.Empty<MapFramePoint>(), MoveTuning.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Clearance(lease, Enumerable.Repeat(At(0f, 0f, 2f), 65).ToArray(), MoveTuning.Default));
    }

    [Fact]
    public void ForeignAndStaleLeases_Throw()
    {
        using var physics = new BepuPhysicsWorld();
        using var other = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        using (var foreign = ((IPhysicsQueryLeaseSource)other).AcquireQueryReadLease())
            Assert.Throws<ArgumentException>(() => r.LineOfSight(foreign, At(0f, 1f, 2f), At(0f, 1f, -2f)));
        var stale = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        stale.Dispose();
        Assert.Equal(nameof(IPhysicsQueryLease),
            Assert.Throws<ObjectDisposedException>(() => r.LineOfSight(stale, At(0f, 1f, 2f), At(0f, 1f, -2f))).ObjectName);
    }

    [Fact]
    public void OutOfBoundsIntermediateClearancePoint_IsUnknown()
    {
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        var path = new[] { At(0f, 0f, 2f), At(40f, 0f, 2f), At(0f, 0f, 3f) };
        Assert.Equal(MapPhysicalCertainty.Unknown, r.Clearance(lease, path, MoveTuning.Default).Certainty);
    }

    [Fact]
    public void LineOfSightHitWithinToleranceOfTheEnd_IsUnknown()
    {
        using var physics = new BepuPhysicsWorld();
        using var reg = MapPhysicsRegistration.Register(Wall(), physics);
        var r = new MapPhysicalRelations(reg);
        using var lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
        // The ray meets the face at z 0.1, 0.0005 m before an end at z 0.0995 and 0.003 m before an end at z 0.097.
        Assert.Equal(MapPhysicalCertainty.Unknown, r.LineOfSight(lease, At(0f, 1f, 2f), At(0f, 1f, 0.0995f)).Certainty);
        Assert.Equal(MapPhysicalCertainty.Blocked, r.LineOfSight(lease, At(0f, 1f, 2f), At(0f, 1f, 0.097f)).Certainty);
    }
}
