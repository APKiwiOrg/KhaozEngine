using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedSweepDomainAllocationTests
{
    [Theory]
    [MemberData(nameof(CertifiedStoredBoundsResidualTests.Routes), MemberType = typeof(CertifiedStoredBoundsResidualTests))]
    public void WarmStoredEnclosureAndDomainRefusalAllocateNothing(bool restricted, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(1), Pose.At(Vector3.Zero));
        using var view = restricted ? world.CreateQueryViewExcludingStatics(
            [world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 100, 0)))]) : null;
        object source = (object?)view ?? world;
        using var lease = ((IPhysicsQueryLeaseSource)source).AcquireQueryReadLease();
        var ordinary = new CapsuleShape(0.25f, 0);
        var outside = new CapsuleShape(1001, 0);
        var ordinaryValue = new CapsuleQueryDimensions(0.25f, 0);
        var outsideValue = new CapsuleQueryDimensions(1001, 0);
        Pose start = Pose.At(new(3, 0, 0)), far = Pose.At(new(10000, 0, 0));
        Vector3 delta = new(0, 0, 0.125f);
        CapsuleSweepResult Read(bool domain) => dimensions
            ? ((IPhysicsCapsuleGeometryQueries)source).SweepCapsuleCertified(domain ? outsideValue : ordinaryValue, domain ? far : start, delta)
            : ((IPhysicsCapsuleSweep)source).SweepCapsuleCertified(domain ? outside : ordinary, domain ? far : start, delta);
        Clear(Read(false), 0.125f);
        Refused(Read(true));
        CapsuleSweepResult Hit() => dimensions
            ? ((IPhysicsCapsuleGeometryQueries)source).SweepCapsuleCertified(ordinaryValue, start, new(-4, 0, 0))
            : ((IPhysicsCapsuleSweep)source).SweepCapsuleCertified(ordinary, start, new(-4, 0, 0));
        HitContains(Hit(), 1.75, 1.75);
        CapsuleSweepResult clear = default, refusal = default, hit = default;
        long bytes = AllocationMeasurement.Measure(nameof(WarmStoredEnclosureAndDomainRefusalAllocateNothing), () =>
        {
            clear = Read(false);
            refusal = Read(true);
            hit = Hit();
        });
        Assert.Equal(0, bytes);
        Clear(clear, 0.125f);
        Refused(refusal);
        HitContains(hit, 1.75, 1.75);
        lease.AssertCurrent();
    }
}
