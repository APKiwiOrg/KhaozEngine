using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
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
}
