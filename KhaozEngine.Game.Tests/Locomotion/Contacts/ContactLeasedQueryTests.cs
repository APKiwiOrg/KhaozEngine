using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default with
    { CapsuleRadius = 0.25f, CapsuleHalfHeight = 0.75f, StepHeight = 0.3f };
    static readonly GroundCoreSettings Settings = new();
    static readonly MovementSpaceKey Room = new("world", "room");
    const float Skin = 0.001f;

    [Theory]
    [InlineData(0f)]
    [InlineData(30f)]
    public void B4RestingBodyUsesOneVerticalSkinAndGeometricSupport(float degrees)
    {
        float angle = degrees * MathF.PI / 180;
        Vector3 normal = new(-MathF.Sin(angle), MathF.Cos(angle), 0);
        using var scene = new QueryScene(x => 0.25f + MathF.Tan(angle) * x, normal);
        var queries = scene.Queries();
        Vector3 feet = new(0, 0.25f, 0);
        SupportSample support = queries.FindSupport(Query(feet), out var binding);
        GroundStepResult result = GroundCore.Step(feet, Vector2.Zero, 1f / 30, Tuning, Settings, queries);
        GroundStepResult dry = GroundCore.Step(feet, Vector2.Zero, 1f / 30, Tuning, Settings,
            (x, z) => 0.25f + MathF.Tan(angle) * x, (x, z) => normal, null, null);

        Assert.Equal(0.25f, support.Height);
        Assert.Equal(new MovementSupportKey("world", "floor"), binding!.Value.Owner);
        Assert.Equal(0.25f, result.Feet.Y);
        Assert.InRange(queries.BodyCentre(result.Feet).Y - 0.75f, 0.2509f, 0.2511f);
        Assert.Equal(0f, result.StepRise);
        Assert.Equal(0f, result.MoveRise);
        AssertBits(dry, result);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void B4QuarterMetreRiserPaysOnlyItsGeometricRise(int hz)
    {
        using var geometry = FootSupportScenes.Bank(SceneVariant.Box);
        float dt = 1f / hz;
        var drySteps = new GroundStepResult[hz];
        Vector3 dryFeet = Vector3.Zero;
        using (IPhysicsQueryLease lease = geometry.World.AcquireQueryReadLease())
            for (int tick = 0; tick < hz; tick++)
            {
                drySteps[tick] = GroundCore.Step(dryFeet, new Vector2(2 * dt, 0), dt, Tuning, Settings,
                    null, null, geometry.World, lease, stepInProgress: dryFeet.Y > 0 && dryFeet.Y < 0.25f);
                dryFeet = drySteps[tick].Feet;
            }
        using var scene = new QueryScene(geometry);
        var queries = scene.Queries();
        Vector3 feet = Vector3.Zero;
        double paid = 0;
        for (int tick = 0; tick < hz; tick++)
        {
            GroundStepResult step = GroundCore.Step(feet, new Vector2(2 * dt, 0), dt, Tuning,
                Settings, queries, stepInProgress: feet.Y > 0 && feet.Y < 0.25f);
            paid += step.StepRise;
            feet = step.Feet;
            AssertBits(drySteps[tick], step);
        }
        Assert.True(feet.X > 1.1f);
        Assert.InRange(feet.Y, 0.2495f, 0.2505f);
        Assert.InRange(paid, 0.2495, 0.2505);
        Assert.InRange(queries.BodyCentre(feet).Y, 1.0009f, 1.0011f);
        GroundStepResult rest = GroundCore.Step(feet, Vector2.Zero, dt, Tuning, Settings, queries);
        Assert.Equal(0f, rest.StepRise);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void B5MappedHeadCannotPublishThroughSubMillimetreRoofClearance(int hz)
    {
        using var geometry = new FootSupportScene(SceneVariant.Box)
            .Flat("floor", -4, 4, -4, 4, 0);
        geometry.World.AddStatic(new BoxShape(new(4, 0.1f, 4)), Pose.At(new(0, 1.6005f, 0)));
        using var scene = new QueryScene(geometry);
        var queries = scene.Queries();
        var support = new SupportSample(SupportStatus.Walkable, 0, 0, Vector3.UnitY,
            geometry["floor"], 0, Vector3.Zero);
        var seat = new GroundSeatResult(SeatOutcome.Seated, 0, support, 0, Vector3.Zero);

        GroundPlacement placed = GroundPlacement.Try(queries, support, 0, Vector2.Zero, Vector2.Zero,
            seat, 0.125f, Tuning, MathF.Cos(Tuning.MaxSlopeRadians), 1);
        GroundStepResult step = GroundCore.Step(Vector3.Zero, new Vector2(2f / hz, 0), 1f / hz,
            Tuning, Settings, queries);

        // The shared seat can clear downward into permitted leg lag. Any accepted placement must clear
        // the physical roof, and a steep seat that forbids a push must expose no placement prefix.
        Assert.True(!placed.Valid || queries.BodyCentre(placed.Feet).Y + 0.75f <= 1.5005f);
        Assert.True(queries.BodyCentre(step.Feet).Y + 0.75f <= 1.5005f);
        GroundPlacement refused = GroundPlacement.Try(queries, support, 0, Vector2.Zero, Vector2.Zero,
            seat with { Outcome = SeatOutcome.SteepSeated }, 0.125f, Tuning,
            MathF.Cos(Tuning.MaxSlopeRadians), 1, clearSteep: true);
        Assert.False(refused.Valid);
        Assert.Equal(Vector2.Zero, refused.Total);
        Assert.Equal(0f, step.StepRise);
        Assert.Equal(0f, step.MoveRise);
    }

    [Theory]
    [InlineData("rise")]
    [InlineData("drop")]
    [InlineData("probe-margin")]
    [InlineData("custom-footprint")]
    [InlineData("pushed-route")]
    [InlineData("mapped-head")]
    public void Q2OutOfScopeQueryRefusesBeforePhysicsOrProducer(string fault)
    {
        using var scene = new QueryScene(scopeMax: new Vector3(1, 2, 1), maxRise: 0.3f, maxDrop: 0.3f);
        var queries = scene.Queries();
        var feet = Vector3.Zero;
        FootSupportQuery query = Query(feet);
        Action request = fault switch
        {
            "rise" => () => queries.FindSupport(query with { ReachUp = 0.31f }),
            "drop" => () => queries.FindSupport(query with { ReachDown = 0.31f }),
            "probe-margin" => () => queries.FindSupport(query with { Axis = new(0.75f, 0), FootRadius = 0.2495f }),
            "custom-footprint" => () => queries.FindSupport(query with { FootRadius = 1.1f }),
            "pushed-route" => () => queries.AcceptSegment(feet, new Vector3(1, 0, 0)),
            _ => () => queries.PenetrationShell(ShellGeometry.Shape(Tuning), new Vector3(0, 1.725f, 0), out _)
        };
        ContactQueryFailure failure = Assert.Throws<ContactQueryFailure>(request);
        Assert.Equal(MovementAvailability.Unresolved, failure.Availability);
        Assert.Equal(0, scene.View.Calls);
        Assert.Equal(0, scene.Environment.AnalyticGroundCalls);
        Assert.Equal(0, scene.Environment.ContactSupportCalls);
        Assert.Equal(0, scene.Environment.SampleCalls);
        Assert.Equal(0, scene.Environment.CoverageCalls);
    }

    [Fact]
    public void Q2OutOfScopeSignalSampleIsRefusedBeforeAccessAndSkipped()
    {
        using var scene = new QueryScene(scopeMax: new Vector3(1, 2, 1), maxRise: 0.3f, maxDrop: 0.3f);
        var queries = scene.Queries();
        // The first look-ahead is 0.25 m ahead with reach 0.3 + 2 * 0.25 = 0.8 m.
        ContactQueryFailure failure = Assert.Throws<ContactQueryFailure>(() => queries.FindSupport(
            Query(Vector3.Zero) with { Axis = new(0.25f, 0), ReachUp = 0.8f, ReachDown = 0.8f }));
        Assert.Equal(MovementAvailability.Unresolved, failure.Availability);
        var signals = GroundSignals.Resolve(new MoveState { ClimbRate = 1 }, 0, 0, true,
            false, 0, Vector2.UnitX, Vector3.Zero, new(SupportStatus.Walkable, 0, 0, Vector3.UnitY,
                null, -1, Vector3.Zero), true, 1f / 30, Tuning, 0.125f,
            MathF.Cos(Tuning.MaxSlopeRadians), queries);

        Assert.Equal((0f, 0f, 0f), signals);
        Assert.Equal(0, scene.View.Calls);
        Assert.Equal(0, scene.Environment.AnalyticGroundCalls);
        Assert.Equal(0, scene.Environment.ContactSupportCalls);
        Assert.Equal(0, scene.Environment.SampleCalls);
        Assert.Equal(0, scene.Environment.CoverageCalls);
    }

    [Fact]
    public void KnownAnalyticAbsenceLeavesAStaticFootprintAndNoTerrainBackstop()
    {
        using var geometry = FootSupportScenes.Bank(SceneVariant.Box);
        using var scene = new QueryScene(geometry);
        var queries = scene.Queries();
        Assert.True(float.IsNaN(queries.HeightAt(Vector3.Zero)));
        SupportSample support = queries.FindSupport(Query(new Vector3(0.9f, 0, 0)), out var binding);
        Assert.Equal(SupportStatus.Walkable, support.Status);
        Assert.InRange(support.Height, 0.2495f, 0.2505f);
        Assert.Equal(geometry["bank"], support.Static);
        Assert.Equal(new MovementSupportKey("world", "bank"), binding!.Value.Owner);
    }

    [Fact]
    public void MediumClassificationUsesTheOriginalBodyAtTheMappedCentre()
    {
        using var scene = new QueryScene();
        scene.Environment.OnBodySample = (in MovementBodyQuery body) =>
        {
            Assert.Equal(0.25f, body.Radius);
            Assert.Equal(0.75f, body.HalfHeight);
            Assert.InRange(body.Centre.Y, 1.0009f, 1.0011f);
            return new(MovementAvailability.Known, Room, null, false, 0, null);
        };
        Assert.Equal(MovementAvailability.Known, scene.Queries().SampleMedium(new(0, 0.25f, 0))!.Value.Availability);
        Assert.Equal(1, scene.Environment.SampleCalls);
    }

    static FootSupportQuery Query(Vector3 feet) => new(new(feet.X, feet.Z), feet.Y, 0.125f, 0.3f, 0.3f,
        MathF.Cos(Tuning.MaxSlopeRadians));

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(0.1f)]
    public void ClosedSupportBandUsesKernelFeetDespitePublishedCentreRounding(float reference)
    {
        using var scene = new QueryScene(static x => 0);
        SupportSample sample = scene.Queries().FindSupport(Query(new(0, reference, 0)) with
        { ReachUp = 0.1f, ReachDown = 0.1f }, out var binding);
        Assert.Equal(SupportStatus.Walkable, sample.Status);
        Assert.Equal(0f, sample.Height);
        Assert.Equal(new MovementSupportKey("world", "floor"), binding!.Value.Owner);
    }

    [Fact]
    public void Q2RoundedNeighborhoodPoseCannotEscapeTheAdmittedProbeEnvelope()
    {
        using var scene = new QueryScene(scopeMin: new(-4, 1000000, -4), scopeMax: new(4, 1000002, 4),
            maxRise: 0.3f, maxDrop: 0.3f);
        var feet = new Vector3(0, 1000000, 0);
        var queries = new LeasedContactQueryContext(scene.Lease, Tuning, new(Room, null, scene.Lease.Identity),
            new(Room, feet));
        ContactQueryFailure failure = Assert.Throws<ContactQueryFailure>(() => queries.FindSupport(Query(feet)));
        Assert.Equal(MovementAvailability.Unresolved, failure.Availability);
        Assert.Equal(0, scene.View.Calls);
        Assert.Equal(0, scene.Environment.AnalyticGroundCalls);
        Assert.Equal(0, scene.Environment.ContactSupportCalls);
    }
}
