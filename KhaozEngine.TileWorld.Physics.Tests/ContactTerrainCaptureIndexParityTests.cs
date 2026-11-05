using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainCaptureIndexParityTests
{
    [Fact]
    public void RoundedRawAnchorOutsideItsDoubleCellRetainsTheOriginalFaceBinding()
    {
        using var scene = new ContactTerrainScene(0);
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.Rebase(new(65536, 0, 0));
        var box = world.AddStatic(new BoxShape(new(1, 0.5f, 0.5f)), Pose.At(new(-0.9990234375f, -0.5f, -1.5f)));
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", world,
            new(new(-20, -120, -20), new(65538, 20, 20), -101, 10))
            .WithAnalyticSupport(scene.Source)
            .Add(box, 0, int.MaxValue, new("world", "rounded-anchor"), new(65534, -1, -2), new(65537, 0, -1));
        OrdinaryMovementRegistration registration;
        using (var lease = world.AcquireQueryReadLease()) registration = builder.Build(lease);
        using var context = new GroundMoveContext(scene.Colliders.MovementGroundHeight, scene.Colliders.MovementGroundNormal,
            world, null, null, null, null, registration, null);
        var options = new PhysicsNavBakeOptions(65536, -1.375f, 65536.0078125f, -1.373046875f,
            0.001953125f, 10, 14, ContactTerrainScene.Tuning.MaxSlopeRadians, 4, 64);
        byte[] expected;
        using (var capture = PhysicsNavBake.Capture(context, options, _ => 0))
        {
            var reference = ContactTerrainCaptureOracle.Capture(capture);
            Assert.Equal(2, reference.RawCount);
            expected = ContactTerrainCaptureBytes.Encode(reference);
        }
        using var indexed = PhysicsNavBake.CaptureDescent(context, options, _ => 0);
        Assert.Equal(expected, ContactTerrainCaptureBytes.Encode(indexed.DescentEvidence!));
    }

    [Theory]
    [InlineData(1.3f, false)]
    [InlineData(4f, false)]
    [InlineData(6f, false)]
    [InlineData(4f, true)]
    public void IndexedCaptureMatchesEveryAllScanFieldIncludingBoundaryFeatures(float height, bool boundaries)
    {
        using var scene = new ContactTerrainScene(height, extra: (world, builder) =>
        {
            if (!boundaries) return;
            var features = new[]{
                new TriangleMeshShape([new(2,0,-1),new(2,2,-1),new(2,2,-2)],[0,1,2]),
                new TriangleMeshShape([new(3,-1,-1),new(3.25f,-1,-1),new(3,-1,-0.75f)],[0,1,2]),
                new TriangleMeshShape([new(3.25f,0,-2),new(3.25f,1,-2),new(3.25f,2,-1.75f)],[0,1,2])};
            for (int i = 0; i < features.Length; i++)
            {
                var handle = world.AddStatic(features[i], Pose.Identity);
                builder.Add(handle, 0, 0, new("world", $"boundary/{i}"), new(2, -1, -2), new(4, 3, 0));
            }
            var sphere = world.AddStatic(new SphereShape(0.25f), Pose.At(new(4.125f, 1, -1.375f)));
            builder.Add(sphere, 0, 0, new("world", "sphere"), new(3.875f, 0.75f, -1.625f), new(4.375f, 1.25f, -1.125f));
        });
        var options = new PhysicsNavBakeOptions(0, -3, 5, 0, 0.25f, 10, 14, ContactTerrainScene.Tuning.MaxSlopeRadians, 256, 1024);
        using var indexed = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        byte[] expected = ContactTerrainCaptureBytes.Encode(ContactTerrainCaptureOracle.Capture(indexed));
        Assert.Equal(expected, ContactTerrainCaptureBytes.Encode(indexed.DescentEvidence!));
    }
}
