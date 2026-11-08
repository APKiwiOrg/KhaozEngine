using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileDryTracePointProofTests
{
    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, 1000000f)]
    [InlineData(true, 1000000f)]
    public void DryTracePointClosureRequiresColumnDisjointness(bool overlapsColumn, float originX)
    {
        // #1729 F1. Dry nonuniform-bed traces do not authorize the rounded point bed lookup.
        var field = MovementWaterFieldTests.Field(MovementWaterFieldTests.WaterWorld());
        var proof = Assert.IsAssignableFrom<IMovementDryTracePointProof>(field);
        float x = overlapsColumn ? 0.5f : 4;
        var origin = new Vector3(originX, 0, 0);
        var body = new MovementBodyQuery(new(x - originX, -8, 0.5f), 0.1f, 0.25f,
            MovementWaterFieldTests.Space, null);
        var query = new MovementMediumSweepQuery(body, new(0.125f, 0, 0));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var coverage = field.TraceWater(query, origin, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, coverage.Availability);
        Assert.Equal(0, coverage.ContactsWritten);
        Assert.True(coverage.SpansWritten > 0);
        Assert.All(spans[..coverage.SpansWritten], span => Assert.True(span.HasDryCoverage));
        Assert.Equal(!overlapsColumn, proof.CanClassifyDryTracePoints(query, origin));
        if (overlapsColumn) return;
        for (int sample = 0; sample <= 8; sample++)
        {
            var pointBody = new MovementBodyQuery(body.Centre + query.Delta * (sample / 8f),
                body.Radius, body.HalfHeight, body.CurrentSpace, null);
            var point = field.SampleCentreWater(pointBody, origin);
            Assert.Equal(MovementAvailability.Known, point.Availability);
            Assert.False(point.InWater);
            Assert.Null(point.Domain);
            Assert.Null(point.Interval);
        }
    }

    [Fact]
    public void CapturedTileWaterKeepsTheFlatArrivalPreviewBound()
    {
        var document = ContactTerrainScene.Document(0, profile: _ => 0.25f);
        document.SetUnderlay(4, 2, 0, TileWorldPhysicsTestData.Water);
        var colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), "world");
        Assert.True(colliders.Medium.HasColumns);
        Assert.IsAssignableFrom<IMovementDryTracePointProof>(colliders.Medium);
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var installed = colliders.AddTo(world);
        var builder = installed.CreateMovementRegistrationBuilder("world",
            new(new(-20, -4, -20), new(20, 6, 20), -2, 2));
        OrdinaryMovementRegistration registration;
        using (var lease = world.AcquireQueryReadLease()) registration = builder.Build(lease);
        GroundMoveContext Context(IMovementWaterField? water) => new(colliders.MovementGroundHeight,
            colliders.MovementGroundNormal, world, null, null, null, water, registration,
            MovementBoundary.Rectangle(0, -3, 5, 0));
        using var dry = Context(null);
        using var actual = Context(colliders.Medium);
        MoveTuning tuning = ContactTerrainScene.Tuning with { WalkSpeed = 4 };
        var body = new MoveState { Position = new(1, 1, -1), Grounded = true };
        var target = ReachTarget.Point(body.Position + new Vector3(0.4f, 0, 0));
        Vector2 Stop(GroundMoveContext context) => RangeApproachCore.StopAtRange(body, tuning, target,
            0.05f, false, 0.1f, context, Vector2.UnitX,
            context.Step(body, Vector2.UnitX, false, 0.1f, tuning), Admit);
        Vector2 expected;
        int expectedTrials;
        using (var work = new ArrivalPreviewDiagnostics())
        {
            expected = Stop(dry);
            Assert.Equal(0, work.FullSteps);
            Assert.InRange(work.CertifiedTrials, 1, 32);
            expectedTrials = work.CertifiedTrials;
        }
        using var actualWork = new ArrivalPreviewDiagnostics();
        Vector2 command = Stop(actual);
        LocomotionStateBits.AssertEqual(expected, command, LocomotionStateBits.Policy.IncludeContinuityHandle);
        Assert.Equal(0, actualWork.FullSteps);
        Assert.Equal(expectedTrials, actualWork.CertifiedTrials);
        LocomotionStateBits.AssertEqual(dry.Step(body, expected, false, 0.1f, tuning),
            actual.Step(body, command, false, 0.1f, tuning), LocomotionStateBits.Policy.IncludeContinuityHandle);
    }

    static bool Admit(in MoveState from, in MoveState to, in MoveTuning tuning) => to.Grounded && !to.Swimming;
}
