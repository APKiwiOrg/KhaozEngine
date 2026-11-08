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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BorrowedViewEvidenceUsesItsActualImmutableStaticSelection(bool exclude)
    {
        // #1729. Registration exclusions cannot stand in for a caller-supplied query view's selection.
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var first = world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(-0.5f, 0, 0)));
        var second = world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0.5f, 0, 0)));
        StaticHandle[] excluded = exclude ? [first] : [];
        using var view = world.CreateQueryViewExcludingStatics(excluded);
        if (exclude) excluded[0] = second;
        using var lease = world.AcquireQueryReadLease();
        var capture = Assert.IsAssignableFrom<IPhysicsStaticEvidence>(view);
        var selected = new HashSet<StaticHandle>();
        Assert.Equal(PhysicsEvidenceStatus.Complete, capture.CaptureStaticEvidence(lease, new(-2), new(2), 32, Visit));
        Assert.Contains(second, selected);
        Assert.Equal(!exclude, selected.Contains(first));
        using var other = new BepuPhysicsWorld(Vector3.Zero);
        using var foreign = other.AcquireQueryReadLease();
        Assert.Throws<ArgumentException>(() => capture.CaptureStaticEvidence(foreign, new(-2), new(2), 32, Visit));
        lease.Dispose();
        view.Dispose();
        using var afterDisposal = world.AcquireQueryReadLease();
        Assert.Throws<ObjectDisposedException>(() => capture.CaptureStaticEvidence(afterDisposal, new(-2), new(2), 32, Visit));
        bool Visit(in PhysicsEvidenceFace face, ReadOnlySpan<PhysicsEvidencePoint> vertices)
        {
            selected.Add(face.Static);
            return true;
        }
    }

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
