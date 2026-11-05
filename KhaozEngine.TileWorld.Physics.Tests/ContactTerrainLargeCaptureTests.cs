using System;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class ContactTerrainLargeCaptureTests
{
    [Fact]
    public void MovementCaptureAcceptsTheSameCompleteOverlappingStaticInventory()
    {
        const int walls = 4097, required = walls + 2;
        var doc = TileWorldMovementNavigationTests.Drawn(0, 0, 1, 1);
        using var scene = new ContactTerrainScene(0, document: doc, extra: (world, builder) =>
        {
            var shape = new TriangleMeshShape([new(0.25f, 1, -0.75f), new(0.25f, 2, -0.75f), new(0.25f, 2, -0.25f)], [0, 1, 2]);
            for (int i = 0; i < walls; i++)
            {
                var handle = world.AddStatic(shape, Pose.Identity);
                builder.Add(handle, 0, 0, new("world", $"wall/{i}"), new(0.25f, 1, -0.75f), new(0.25f, 2, -0.25f));
            }
        });
        var options = new PhysicsNavBakeOptions(0, -1, 1, 0, 1, 10, 14,
            ContactTerrainScene.Tuning.MaxSlopeRadians, 1, 8192);
        using var capture = PhysicsNavBake.CaptureDescent(scene.Context, options, _ => 0);
        var evidence = Assert.IsType<PhysicsNavDescentEvidence>(capture.DescentEvidence);
        Assert.Equal(required, evidence.FaceCount);
        Assert.Equal(DescentCoverage.Complete, evidence.Coverage(0, 0));
        Assert.Equal(1, evidence.RawCount);
        int observed = 0;
        using (var lease = scene.World.AcquireQueryReadLease())
        {
            Assert.Equal(PhysicsEvidenceStatus.Complete, scene.World.CaptureStaticEvidence(lease,
                new(0, -4, -1), new(1, 10, 0), required, Visit));
            Assert.Equal(required, observed);
        }
        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points) { observed++; return true; }
    }
}
