using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

public sealed class OrdinaryContactSwimBankFloorTests(ITestOutputHelper output)
{
    const float Surface = -0.02f;
    const float Dt = 1f / 30;

    static MoveTuning Tuning => OrdinaryContactScene.DefaultTuning with
    {
        CapsuleRadius = 0.3f,
        CapsuleHalfHeight = 0.75f,
        MaxSlopeRadians = MathF.PI / 4,
        StepHeight = 0.4f,
        FootRadiusFraction = 0.5f,
        SwimEnterDepthFraction = 0.65f,
        SwimExitDepthFraction = 0.55f,
        SwimSurfaceSubmersionFraction = 0.6f,
        SwimBuoyancyStiffness = 8,
        SwimSpeed = 3,
        Water = new(WaterTraversalMode.SurfaceSwimmer),
    };

    [Theory]
    [InlineData(0.5f, -2e-7f, 6e-7f)]
    [InlineData(0.5f, 2e-7f, -6e-7f)]
    [InlineData(0.95f, -2e-7f, 6e-7f)]
    [InlineData(0.95f, 2e-7f, -6e-7f)]
    [InlineData(1f, -2e-7f, 6e-7f)]
    [InlineData(1f, 2e-7f, -6e-7f)]
    public void EverySwimmingSubstepClearsTheRisingBedBeforeLanding(float grade, float offset, float vertical)
    {
        MoveTuning tuning = Tuning;
        float Floor(float x) => Math.Clamp(grade * (x - 35), -2.85f, 0);
        Vector3 Normal(float x) => Floor(x) is > -2.85f and < 0
            ? Vector3.Normalize(new(-grade, 1, 0)) : Vector3.UnitY;
        using var scene = new OrdinaryContactScene(tuning, (x, _) => Floor(x), (x, _) => Normal(x),
            new(new(0, -4, -4), new(40, 4, 4), -2.85f, 0), MovementWaterField.Level("world", Surface, -4),
            boundary: MovementBoundary.Rectangle(0, -4, 40, 4));
        using var lease = scene.Acquire();
        float rest = (float)(Surface + (double)tuning.CapsuleHalfHeight *
            (1 - 2d * tuning.SwimSurfaceSubmersionFraction));
        float exitBed = Surface - 2 * tuning.CapsuleHalfHeight * tuning.SwimExitDepthFraction;
        var samples = new List<(int Phase, int Tick, MoveState Body, float Bed)>();
        float maximumPenetration = 0;
        int deepContacts = 0;
        // Sweep a whole 0.1 m movement phase, including the consumer reproduction's 30.06604 start.
        for (int phase = 0; phase < 4; phase++)
        {
            var state = new FramedMovementState(new MoveState
            {
                Position = new(30.06604f + phase * 0.025f, rest + offset, 0),
                VerticalVelocity = vertical,
                Swimming = true,
                WaterExcursion = WaterExcursionState.Surface,
            }, scene.Frame, null);
            int tick = 0;
            while (!state.State.Grounded && tick < 90)
            {
                // The 0.1 m swim move is below half the 0.3 m radius. Each command is one air substep,
                // including the clipped contact, so no intermediate unsupported endpoint is hidden.
                MovementStepResult result = scene.Binding.StepTowards(state, Vector2.UnitX, false, Dt, lease);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                state = result.State;
                MoveState body = state.State;
                float bed = Floor(body.Position.X), feet = body.Position.Y - tuning.CapsuleHalfHeight;
                samples.Add((phase, tick++, body, bed));
                if (!body.Grounded) maximumPenetration = MathF.Max(maximumPenetration, bed - feet);
                if (bed < exitBed && feet <= bed + 1e-6f) deepContacts++;
            }
            Assert.True(state.State.Grounded, $"phase {phase}, pose {state.State.Position}");
            Assert.False(state.State.Swimming);
            Assert.Equal(WaterExcursionState.None, state.State.WaterExcursion);
            Assert.NotNull(state.Selection!.Value.Support);
        }
        output.WriteLine($"grade {grade:R}, offset {offset:R}, maximum unsupported penetration {maximumPenetration:R} m, deep contacts {deepContacts}");
        Assert.True(deepContacts > 0, "The fixture must contact a bed deeper than the swim exit line.");
        Assert.All(samples, sample =>
        {
            MoveState body = sample.Body;
            string context = $"phase {sample.Phase}, tick {sample.Tick}, pose {body.Position}, bed {sample.Bed:R}";
            if (sample.Bed < exitBed)
            {
                Assert.True(body.Swimming, context);
                Assert.False(body.Grounded, context);
                Assert.False(body.SupportGranted, context);
                Assert.Equal(WaterExcursionState.Surface, body.WaterExcursion);
            }
            if (!body.Grounded)
            {
                Assert.True(body.Position.Y - tuning.CapsuleHalfHeight >= sample.Bed, context);
                Assert.Equal(0, body.ClimbRate);
                Assert.Equal(0, body.StepDeltaY);
            }
        });
    }

    [Theory]
    [InlineData(-0.5200002f, 6.6658833e-7f)]
    [InlineData(-0.5199998f, -6.6658833e-7f)]
    public void DrawnBedBankExitNeverPublishesUnsupportedPenetration(float centreY, float vertical)
    {
        // #1708: exact drawn-bed membership must not hand an uncleared buoyant bank proposal to air.
        const float surface = -0.37f;
        MoveTuning tuning = Tuning;
        var water = new DrawnBankWater(surface);
        using var scene = new OrdinaryContactScene(tuning, (_, z) => DrawnBankWater.Floor(z),
            (_, z) => DrawnBankWater.Floor(z) is > -1.3f and < 0
                ? Vector3.Normalize(new(0, 1, -0.95f)) : Vector3.UnitY,
            new(new(100, -4, -245), new(120, 4, -225), -1.3f, 0), water,
            boundary: MovementBoundary.Rectangle(100, -245, 120, -225));
        using var lease = scene.Acquire();
        var state = scene.State(new(110.5f, centreY, -237.03256f), grounded: false);
        state = new(state.State with
        {
            VerticalVelocity = vertical,
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface,
        }, scene.Frame, null);
        var proposed = new MovementBodyQuery(new(110.5f, centreY, -236.93256f),
            tuning.CapsuleRadius, tuning.CapsuleHalfHeight, new("world", "ordinary"), null);
        Assert.False(water.SampleCentreWater(proposed, Vector3.Zero).InWater);
        int submergedContacts = 0, published = 0;
        bool reachedDryFooting = false;
        for (int tick = 0; tick < 90 && !reachedDryFooting; tick++)
        {
            MovementStepResult result = scene.Binding.StepTowards(state, Vector2.UnitY, false, Dt, lease);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            state = result.State;
            MoveState body = state.State;
            float bed = DrawnBankWater.Floor(body.Position.Z);
            float feet = body.Position.Y - tuning.CapsuleHalfHeight;
            string context = $"tick {tick}, pose {body.Position}, bed {bed:R}";
            if (!body.Grounded) Assert.True(feet >= bed, context);
            if (surface - bed > 2 * tuning.CapsuleHalfHeight * tuning.SwimExitDepthFraction)
            {
                Assert.True(body.Swimming, context);
                Assert.False(body.SupportGranted, context);
                Assert.Equal(WaterExcursionState.Surface, body.WaterExcursion);
                if (feet >= bed) submergedContacts++;
            }
            published++;
            reachedDryFooting = body.Grounded && feet >= surface;
        }
        Assert.True(published > 0);
        Assert.True(submergedContacts > 0);
        Assert.True(reachedDryFooting, $"pose {state.State.Position}");
        Assert.False(state.State.Swimming);
        Assert.Equal(WaterExcursionState.None, state.State.WaterExcursion);
    }

    sealed class DrawnBankWater(float surface) : IMovementWaterField
    {
        readonly IMovementWaterField level = MovementWaterField.Level("world", surface, -1.3f);
        public string SemanticIdentity => "drawn-bank";
        public long Generation => 0;
        public MovementWaterExtent Extent => level.Extent;
        internal static float Floor(float z) => Math.Clamp(-1.2359283f + 0.95f * (z + 236.93256f), -1.3f, 0);

        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin)
        {
            if ((double)body.Centre.Y + physicsOrigin.Y - body.HalfHeight < Floor(body.Centre.Z + physicsOrigin.Z))
                return new(MovementAvailability.Known, body.CurrentSpace, null, false, 1, null);
            return level.SampleCentreWater(body, physicsOrigin);
        }

        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            level.TraceWater(query, physicsOrigin, identity, spans, contacts);
    }
}
