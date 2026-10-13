using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class ContactCompositionTests
{
    static readonly WaterTraversalPolicy Dry = new(WaterTraversalMode.DryOnly);
    static readonly WaterTraversalPolicy Wade = new(WaterTraversalMode.WadeOnly);
    static MoveTuning Tuning(float fraction = 0.5f) => MoveTuning.Default with
    {
        Controller = MovementControllerKind.Contact,
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        StepHeight = 0.3f,
        FootRadiusFraction = fraction,
        WalkSpeed = 4,
        SwimEnterDepthFraction = 0.65f
    };

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    public void OrdinaryAndPublicExplicitComposeDifferentRestingFeetOnEveryDoor(float fraction)
    {
        var tuning = Tuning(fraction);
        var ordinary = ContactComposition.Ordinary(tuning);
        var explicitComposition = ContactComposition.PublicExplicit(tuning);
        Assert.Equal(0f, ordinary.PlacementOffset);
        Assert.Equal(0.001f, explicitComposition.PlacementOffset);
        Assert.Equal(fraction, ordinary.Foot.FootRadiusFraction);
        Assert.Equal(fraction, explicitComposition.Foot.FootRadiusFraction);
        Assert.Equal(0f, ordinary.RestingFeet(0f));
        Assert.Equal(0.001f, explicitComposition.RestingFeet(0f));
        using var scene = new QueryScene();
        var initial = scene.State(new(0, 0.76f, 0));
        var ordinarySettled = ExplicitCharacterMovement.SettlePlacement(initial, tuning, Dry, scene.Lease, ordinary);
        var explicitSettled = ExplicitCharacterMovement.SettlePlacement(initial, tuning, Dry, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, ordinarySettled.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, explicitSettled.Outcome);
        Assert.Equal(0.75f, ordinarySettled.State.State.Position.Y);
        Assert.Equal(0.751f, explicitSettled.State.State.Position.Y);
        Assert.True(ordinarySettled.State.State.Grounded);
        Assert.True(explicitSettled.State.State.Grounded);
        var ordinaryValidated = ExplicitCharacterMovement.ValidatePlacement(ordinarySettled.State, tuning, Dry, scene.Lease, ordinary);
        var explicitValidated = ExplicitCharacterMovement.ValidatePlacement(explicitSettled.State, tuning, Dry, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, ordinaryValidated.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, explicitValidated.Outcome);
        Assert.Equal(ordinarySettled.State.State, ordinaryValidated.State.State);
        Assert.Equal(explicitSettled.State.State, explicitValidated.State.State);
        var carried = new FramedMovementState(ordinarySettled.State.State with { JumpBufferRemaining = 0.125f },
            ordinarySettled.State.Frame, ordinarySettled.State.Selection);
        float before = carried.State.JumpBufferRemaining;
        var ordinaryContinuation = ExplicitCharacterMovement.ReclassifyContinuation(carried, tuning, Dry, scene.Lease, ordinary);
        var explicitContinuation = ExplicitCharacterMovement.ReclassifyContinuation(explicitSettled.State, tuning, Dry, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, ordinaryContinuation.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, explicitContinuation.Outcome);
        Assert.Equal(ordinarySettled.State.State.Position, ordinaryContinuation.State.State.Position);
        Assert.Equal(explicitSettled.State.State.Position, explicitContinuation.State.State.Position);
        Assert.Equal(before, ordinaryContinuation.State.State.JumpBufferRemaining);
        using var dryGeometry = FootSupportScenes.Floor(SceneVariant.Box);
        MoveState dryTick = ContactController.Step(ordinarySettled.State.State, Vector2.UnitX, 1, false, false,
            0.125f, static (_, _) => -1000, tuning, null, dryGeometry.World, null, null);
        var ordinaryTick = ExplicitCharacterMovement.StepTowards(ordinarySettled.State, Vector2.UnitX, false,
            0.125f, tuning, Dry, scene.Lease, ordinary);
        var ordinaryCamera = ExplicitCharacterMovement.Step(ordinarySettled.State, new(new(1, 0), false, 0),
            0.125f, tuning, Dry, scene.Lease, ordinary);
        var explicitTick = ExplicitCharacterMovement.StepTowards(explicitSettled.State, Vector2.UnitX, false,
            0.125f, tuning, Dry, scene.Lease);
        var explicitCamera = ExplicitCharacterMovement.Step(explicitSettled.State, new(new(1, 0), false, 0),
            0.125f, tuning, Dry, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, ordinaryTick.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, ordinaryCamera.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, explicitTick.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, explicitCamera.Outcome);
        Assert.Equal(dryTick, ordinaryTick.State.State);
        Assert.Equal(ordinaryTick.State.State, ordinaryCamera.State.State);
        Assert.Equal(explicitTick.State.State, explicitCamera.State.State);
        Assert.Equal(0.75f, ordinaryTick.State.State.Position.Y);
        Assert.Equal(0.751f, explicitTick.State.State.Position.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomFootReachesPlacementTickAndContinuation(bool explicitOffset)
    {
        using var scene = new QueryScene(ledge: true);
        var narrow = Tuning();
        var wide = Tuning(0.75f);
        var composition = explicitOffset ? ContactComposition.PublicExplicit(wide) : ContactComposition.Ordinary(wide);
        var narrowComposition = explicitOffset ? ContactComposition.PublicExplicit(narrow) : ContactComposition.Ordinary(narrow);
        var input = scene.State(new(0, explicitOffset ? 0.751f : 0.75f, 0));
        var narrowSettled = ExplicitCharacterMovement.SettlePlacement(input, narrow, Dry, scene.Lease, narrowComposition);
        var wideSettled = ExplicitCharacterMovement.SettlePlacement(input, wide, Dry, scene.Lease, composition);
        var wideValidated = ExplicitCharacterMovement.ValidatePlacement(input, wide, Dry, scene.Lease, composition);
        var wideTick = ExplicitCharacterMovement.StepTowards(input, -Vector2.UnitY, false, 0.125f, wide, Dry, scene.Lease, composition);
        var wideCamera = ExplicitCharacterMovement.Step(input, new(new(0, 1), false, 0), 0.125f, wide, Dry, scene.Lease, composition);
        var wideContinuation = ExplicitCharacterMovement.ReclassifyContinuation(input, wide, Dry, scene.Lease, composition);
        var wideOwner = new MovementSupportKey("world", "ledge");
        Assert.Equal(MovementStepOutcome.Advanced, narrowSettled.Outcome);
        Assert.False(narrowSettled.State.State.Grounded);
        Assert.Null(narrowSettled.State.Selection!.Value.Support);
        Assert.Equal(MovementStepOutcome.Advanced, wideSettled.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, wideValidated.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, wideTick.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, wideCamera.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced, wideContinuation.Outcome);
        Assert.Equal(wideOwner, wideSettled.State.Selection!.Value.Support);
        Assert.Equal(wideOwner, wideValidated.State.Selection!.Value.Support);
        Assert.Equal(wideOwner, wideTick.State.Selection!.Value.Support);
        Assert.Equal(wideTick.State.State, wideCamera.State.State);
        Assert.Equal(wideOwner, wideContinuation.State.Selection!.Value.Support);
        Assert.Equal(input.State.Position.Y, wideSettled.State.State.Position.Y);
        Assert.Equal(input.State.Position.Y, wideTick.State.State.Position.Y);
        if (explicitOffset)
        {
            Assert.Equal(wideSettled, ExplicitCharacterMovement.SettlePlacement(input, wide, Dry, scene.Lease));
            Assert.Equal(wideValidated, ExplicitCharacterMovement.ValidatePlacement(input, wide, Dry, scene.Lease));
            Assert.Equal(wideTick, ExplicitCharacterMovement.StepTowards(input, -Vector2.UnitY, false, 0.125f, wide, Dry, scene.Lease));
            Assert.Equal(wideCamera, ExplicitCharacterMovement.Step(input, new(new(0, 1), false, 0), 0.125f, wide, Dry, scene.Lease));
            Assert.Equal(wideContinuation, ExplicitCharacterMovement.ReclassifyContinuation(input, wide, Dry, scene.Lease));
        }
    }

    [Fact]
    public void OffsetZeroDoesNotPayExplicitSkinUnderLowRoof()
    {
        using var scene = new QueryScene(roof: true);
        var tuning = Tuning();
        var ordinary = ContactComposition.Ordinary(tuning);
        var ordinaryInput = scene.State(new(0, 0.75f, 0));
        var explicitInput = scene.State(new(0, 0.751f, 0));
        var lowRoofOrdinary = ExplicitCharacterMovement.SettlePlacement(ordinaryInput, tuning, Dry, scene.Lease, ordinary);
        var lowRoofExplicit = ExplicitCharacterMovement.SettlePlacement(explicitInput, tuning, Dry, scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, lowRoofOrdinary.Outcome);
        Assert.Equal(ordinaryInput.State.Position, lowRoofOrdinary.State.State.Position);
        Assert.Equal(MovementStepOutcome.PlacementRefused, lowRoofExplicit.Outcome);
        Assert.Equal(MovementStepOutcome.Advanced,
            ExplicitCharacterMovement.StepTowards(ordinaryInput, Vector2.UnitX, false, 0.125f, tuning, Dry, scene.Lease, ordinary).Outcome);
        var explicitTick = ExplicitCharacterMovement.StepTowards(explicitInput, Vector2.UnitX, false, 0.125f, tuning, Dry, scene.Lease);
        Assert.True(explicitTick.State.State.Position.Y + tuning.CapsuleHalfHeight <= 1.5005f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.01f)]
    public void OffsetZeroDoesNotPayExplicitSkinAtDeepLine(float below)
    {
        using var scene = new QueryScene(water: [new Water("lake", "room", new(new(-8, -4, -8), new(8, 0, 8)), 0)]);
        var tuning = Tuning();
        var ordinary = ContactComposition.Ordinary(tuning);
        float line = 0f + 0.75f * (1f - 2f * 0.65f);
        var wadeInput = scene.State(new(0, line + below, 0));
        var ordinaryAtWadeThreshold = ExplicitCharacterMovement.StepTowards(wadeInput, Vector2.UnitX, false,
            0.125f, tuning, Wade, scene.Lease, ordinary);
        Assert.Equal(MovementStepOutcome.PlacementRefused, ordinaryAtWadeThreshold.Outcome);
        Assert.Equal(wadeInput.State.Position, ordinaryAtWadeThreshold.State.State.Position);
        Assert.Equal(wadeInput.State, ordinaryAtWadeThreshold.State.State);
        Assert.Equal(MovementStepOutcome.PlacementRefused,
            ExplicitCharacterMovement.ValidatePlacement(wadeInput, tuning, Wade, scene.Lease, ordinary).Outcome);
        Assert.Equal(MovementStepOutcome.PlacementRefused,
            ExplicitCharacterMovement.SettlePlacement(wadeInput, tuning, Wade, scene.Lease, ordinary).Outcome);
        Assert.Equal(MovementStepOutcome.PlacementRefused,
            ExplicitCharacterMovement.ReclassifyContinuation(wadeInput, tuning, Wade, scene.Lease, ordinary).Outcome);
    }

    [Fact]
    public void OrdinarySpatialShoreTangentAdmitsParallelTravel()
    {
        using var scene = new QueryScene(water: [new Water("lake", "room", new(new(1, -4, -8), new(8, 1, 8)), 1)]);
        scene.Environment.CertifyAxisSeparation = true;
        var tuning = Tuning();
        var ordinary = ContactComposition.Ordinary(tuning);
        var input = scene.State(new(0.75f, 0.75f, 0));
        var result = ExplicitCharacterMovement.StepTowards(input, Vector2.UnitY, false, 0.125f, tuning, Wade, scene.Lease, ordinary);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(new Vector3(0.75f, 0.75f, 0.5f), result.State.State.Position);
        Assert.True(result.State.State.Grounded);
    }

    [Theory]
    [InlineData(-0.001f)]
    [InlineData(0.002f)]
    [InlineData(float.NaN)]
    public void CompositionRefusesUnsupportedOffsets(float offset) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContactComposition(offset, new GroundCoreSettings()));

    [Fact]
    public void CompatibilityStepLeasedSettingsOverrideTheTuningFoot()
    {
        using var scene = new QueryScene(ledge: true);
        var tuning = Tuning();
        var input = scene.State(new(0, 0.751f, 0));
        var result = ContactController.StepLeased(input, Vector2.UnitY, 1, false, false, 0.125f,
            tuning, Dry, scene.Lease, settings: new GroundCoreSettings(0.75f));
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.Equal(new MovementSupportKey("world", "ledge"), result.State.Selection!.Value.Support);
        Assert.Equal(0.751f, result.State.State.Position.Y);
    }

    sealed class QueryScene : IDisposable
    {
        internal AnalyticMovementEnvironment Environment { get; }
        internal MovementQueryLease Lease { get; }
        internal QueryScene(bool ledge = false, bool roof = false, Water[]? water = null)
        {
            Environment = new([new Room("room", new(new(-16), new(16)))], water ?? []);
            if (ledge)
            {
                StaticHandle handle = Environment.Physics.AddStatic(new BoxShape(new(0.02f, 0.1f, 8)), Pose.At(new(0.18f, -0.1f, 0)));
                Environment.RegisterBoxSupport(handle, "ledge", "room");
            }
            else Environment.Slab(0);
            if (roof) Environment.Physics.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, 1.6255f, 0)));
            Lease = Environment.Acquire(null, 20, 20);
        }
        internal FramedMovementState State(Vector3 centre) => new(new MoveState { Position = centre, Grounded = true }, Lease.Frame, null);
        public void Dispose() { Lease.Dispose(); Environment.Dispose(); }
    }
}
