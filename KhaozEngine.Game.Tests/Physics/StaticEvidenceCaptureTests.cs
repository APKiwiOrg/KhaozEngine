using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class StaticEvidenceCaptureTests
{
    [Fact]
    public void CompleteInstalledCaptureRetainsTheRoofAndNonstandableTriangle()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var roof = world.AddStatic(new BoxShape(new(2, 0.1f, 2)), Pose.At(new(0, 2.9f, 0)));
        world.AddStatic(new TriangleMeshShape([new(-2, 4, -2), new(2, -4, -2), new(2, -4, 2)], [0, 1, 2]), Pose.Identity);
        bool roofTop = false, steep = false;
        using var lease = world.AcquireQueryReadLease();
        var status = world.CaptureStaticEvidence(lease, new(-1, -5, -1), new(1, 5, 1), 32, Visit);
        Assert.Equal(PhysicsEvidenceStatus.Complete, status);
        Assert.True(roofTop);
        Assert.True(steep);
        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> vertices)
        {
            lease.AssertCurrent();
            if (face.Static == roof)
                roofTop |= vertices.ToArray().All(p => p.Y > 2.9);
            if (face.Kind == PhysicsEvidenceKind.Triangle)
                steep |= vertices.ToArray().Max(p => p.Y) - vertices.ToArray().Min(p => p.Y) > 4;
            return true;
        }
    }

    [Fact]
    public void CapacityAndForeignIntervalsNeverCertifyCompleteEvidence()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(Vector3.One), Pose.Identity);
        using var lease = world.AcquireQueryReadLease();
        Assert.Equal(PhysicsEvidenceStatus.CapacityExceeded,
            world.CaptureStaticEvidence(lease, new(-2), new(2), 1, Visit));
        using var other = new BepuPhysicsWorld(Vector3.Zero);
        using var foreign = other.AcquireQueryReadLease();
        Assert.Throws<ArgumentException>(() => world.CaptureStaticEvidence(foreign, new(-2), new(2), 32, Visit));
        static bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> points) => true;
    }
}
