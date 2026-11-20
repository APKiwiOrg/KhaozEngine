using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The ordinary pin and the lease entry of the water boundary reach certificate. The lease reproduces the
/// trace's pre-checks and catches and declines on every failure, so the trace that follows keeps its refusal.</summary>
public class WaterBoundaryReachLeaseTests
{
    static readonly Vector3 Minimum = new(100, -8, -250), Maximum = new(120, 8, -225);

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(100.5f, 0.3f, -230.25f)]
    [InlineData(110.0625f, -1.7f, -236.3f)]
    public void TheLeaseSurfaceIsTheLevelItsTraceGivesEveryContact(float x, float y, float z)
    {
        // Nonzero Y origins round the converted surface outward. Both sides must take the same conversion.
        var origin = new Vector3(x, y, z);
        using var parity = Lease(new ReachRecordingWaterField(Sampler()), origin);
        MovementMediumSweepQuery query = Local(GrazingWaterBank.Lift(), origin);
        MovementWaterReach reach = parity.Lease.CertifyWaterBoundary(query);
        var contacts = new MovementDomainContact[MovementQueryLease.MaxDomainContacts];
        var trace = parity.Lease.TraceWater(query, new MovementCoverageSpan[MovementQueryLease.MaxCoverageSpans], contacts);
        Assert.Equal(MovementWaterReachKind.OneFreeSurface, reach.Kind);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.True(trace.ContactsWritten > 0);
        Assert.All(contacts[..trace.ContactsWritten], contact => Assert.Equal(
            BitConverter.SingleToInt32Bits(reach.SurfaceY), BitConverter.SingleToInt32Bits(contact.Interval.NominalSurfaceY)));
    }

    [Fact]
    public void AnEmptyReachIsEmptyThroughTheLease()
    {
        using var parity = Lease(new ReachRecordingWaterField(Sampler()), Vector3.Zero);
        var query = new MovementMediumSweepQuery(new(new(104, 0, -244), 0.3f, 0.75f, GrazingWaterBank.Space, null), Vector3.UnitX);
        var trace = parity.Lease.TraceWater(query, new MovementCoverageSpan[MovementQueryLease.MaxCoverageSpans],
            new MovementDomainContact[MovementQueryLease.MaxDomainContacts]);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.Equal(0, trace.ContactsWritten);
        Assert.Equal(MovementWaterReach.Empty, parity.Lease.CertifyWaterBoundary(query));
    }

    [Theory]
    [InlineData("control", MovementAvailability.Known)]
    [InlineData("selection", MovementAvailability.Unresolved)]
    [InlineData("world", MovementAvailability.Invalid)]
    [InlineData("space", MovementAvailability.Invalid)]
    [InlineData("envelope", MovementAvailability.Unresolved)]
    [InlineData("hidden", MovementAvailability.Known)]
    [InlineData("argument", MovementAvailability.Known)]
    [InlineData("operation", MovementAvailability.Known)]
    [InlineData("stale", MovementAvailability.Stale)]
    public void EveryPreCheckFailureDeclinesAndTheTraceKeepsItsOutcome(string row, MovementAvailability traced)
    {
        // hidden: a wrapper that forwards only the trace. argument and operation: the producer throws from its
        // certificate, which the lease's catches turn into a decline as they turn a throwing trace into a refusal.
        TileMediumSampler sampler = Sampler();
        var recording = new ReachRecordingWaterField(sampler)
        {
            Fault = row switch
            {
                "argument" => () => new ArgumentException("fault"),
                "operation" => () => new InvalidOperationException("fault"),
                _ => null,
            }
        };
        IMovementWaterField field = row == "hidden" ? new RecordingWaterField(sampler) : recording;
        using var parity = Lease(field, Vector3.Zero, rebuildSelection: row != "selection");
        MovementMediumSweepQuery lift = GrazingWaterBank.Lift();
        MovementMediumSweepQuery query = row switch
        {
            "world" => new(new(lift.Body.Centre, 0.3f, 0.75f, new("other-world", "ordinary"), null), lift.Delta),
            "space" => new(new(lift.Body.Centre, 0.3f, 0.75f, new(GrazingWaterBank.Space.WorldId, "other"), null), lift.Delta),
            "envelope" => new(new(lift.Body.Centre + new Vector3(1000, 0, 0), 0.3f, 0.75f, GrazingWaterBank.Space, null), lift.Delta),
            _ => lift,
        };
        if (row == "stale") recording.Revision++;
        MovementWaterReach reach = parity.Lease.CertifyWaterBoundary(query);
        var trace = parity.Lease.TraceWater(query, new MovementCoverageSpan[MovementQueryLease.MaxCoverageSpans],
            new MovementDomainContact[MovementQueryLease.MaxDomainContacts]);
        Span<Vector3> normals = stackalloc Vector3[MovementQueryLease.MaxDomainContacts];
        MovementAvailability boundary = MovementWaterBoundary.FindForParityTests(query.Body, query.Delta, 0.75f, 0.65f,
            WaterTraversalMode.SurfaceSwimmer, parity.Lease, normals, out _, out _, 0, certify: true, out bool certified);
        Assert.Equal(traced, trace.Availability);
        if (row == "control")
        {
            Assert.Equal(MovementWaterReachKind.OneFreeSurface, reach.Kind);
            return;
        }
        Assert.Equal(MovementWaterReach.Declined, reach);
        Assert.False(certified);
        if (traced != MovementAvailability.Known) Assert.Equal(traced, boundary);
    }

    [Fact]
    public void AStalePinThrowsFromItsCertificateAsFromItsTrace()
    {
        var field = new ReachRecordingWaterField(Sampler());
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease())
            registration = OrdinaryMovementRegistration.CreateBuilder(GrazingWaterBank.Space.WorldId, physics,
                new(Minimum, Maximum, Minimum.Y, Minimum.Y)).Build(read);
        using var view = physics.CreateQueryViewExcludingStatics([]);
        var provider = new OrdinaryMovementEnvironment(view, registration, GrazingWaterBank.Tuning, (_, _) => Minimum.Y, null,
            MovementBoundary.Rectangle(Minimum.X, Minimum.Z, Maximum.X, Maximum.Z), field);
        var scope = provider.CreateScope(new(default, Vector3.Zero, 1));
        using var physicsLease = physics.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Known, provider.TryPinPrepared(scope, physicsLease, out var pin));
        using (pin)
        {
            var reachPin = Assert.IsAssignableFrom<IMovementBoundaryReachPin>(pin);
            Assert.Equal(MovementWaterReachKind.OneFreeSurface, reachPin.CertifyBoundaryReach(GrazingWaterBank.Lift()).Kind);
            field.Revision++;
            var traced = Assert.Throws<MovementReadException>(() => pin!.TraceWater(GrazingWaterBank.Lift(),
                new MovementCoverageSpan[MovementQueryLease.MaxCoverageSpans], new MovementDomainContact[MovementQueryLease.MaxDomainContacts]));
            var certified = Assert.Throws<MovementReadException>(() => reachPin.CertifyBoundaryReach(GrazingWaterBank.Lift()));
            Assert.Equal(MovementReadRefusal.EnvironmentPinStale, traced.Reason);
            Assert.Equal(traced.Reason, certified.Reason);
        }
    }

    [Fact]
    public void ADisposedLeaseThrowsFromItsCertificateAsFromItsTrace()
    {
        using var parity = Lease(new ReachRecordingWaterField(Sampler()), Vector3.Zero);
        MovementQueryLease lease = parity.Lease;
        Assert.Equal(MovementWaterReachKind.OneFreeSurface, lease.CertifyWaterBoundary(GrazingWaterBank.Lift()).Kind);
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => lease.TraceWater(GrazingWaterBank.Lift(),
            new MovementCoverageSpan[MovementQueryLease.MaxCoverageSpans], new MovementDomainContact[MovementQueryLease.MaxDomainContacts]));
        Assert.Throws<ObjectDisposedException>(() => lease.CertifyWaterBoundary(GrazingWaterBank.Lift()));
    }

    static TileMediumSampler Sampler() => MovementWaterFieldTests.Field(GrazingWaterBank.Document(109, 3));

    static WaterBoundaryParityLease Lease(IMovementWaterField field, Vector3 origin, bool rebuildSelection = true) =>
        new(field, GrazingWaterBank.Space.WorldId, origin, Minimum, Maximum, GrazingWaterBank.Tuning,
            new(110.375f, 0, -236.8f), rebuildSelection);

    static MovementMediumSweepQuery Local(MovementMediumSweepQuery absolute, Vector3 origin)
    {
        MovementBodyQuery body = absolute.Body;
        Vector3 centre = new((float)((double)body.Centre.X - origin.X), (float)((double)body.Centre.Y - origin.Y),
            (float)((double)body.Centre.Z - origin.Z));
        return new(new(centre, body.Radius, body.HalfHeight, body.CurrentSpace, body.CurrentSupport), absolute.Delta);
    }
}
