using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class MovementSelectorTests
{
    const float Dt = 1f / 30f;
    static MoveTuning Contact => MoveTuning.Default with { Controller = MovementControllerKind.Contact };
    static MoveTuning Ordinary => Contact with { Water = new(WaterTraversalMode.WadeOnly) };
    static MoveState Standing => new() { Position = new(0, MoveTuning.Default.CapsuleHalfHeight, 0), Grounded = true };
    static float Flat(float x, float z) => 0;
    static Vector2 Clamp(float x, float z) => new(Math.Clamp(x, -0.1f, 0.1f), Math.Clamp(z, -0.1f, 0.1f));
    static MovementMedium Dry(float x, float z, float feet) => MovementMedium.Dry;

    [Fact]
    public void DefaultsWithEqualityAndHashIncludeTheThreeInitProperties()
    {
        MoveTuning baseline = MoveTuning.Default;
        MoveTuning contact = baseline with { Controller = MovementControllerKind.Contact };
        MoveTuning widerFoot = baseline with { FootRadiusFraction = 0.75f };
        MoveTuning wader = baseline with { Water = new(WaterTraversalMode.WadeOnly) };
        Assert.Equal(MovementControllerKind.Legacy, baseline.Controller);
        Assert.Equal(0.5f, baseline.FootRadiusFraction);
        Assert.Equal(default(WaterTraversalPolicy), baseline.Water);
        Assert.Equal(baseline, baseline with { });
        Assert.NotEqual(baseline, contact);
        Assert.NotEqual(baseline, widerFoot);
        Assert.NotEqual(baseline, wader);
        Assert.Equal(4, new HashSet<MoveTuning> { baseline, contact, widerFoot, wader }.Count);
    }

    [Fact]
    public void ContactFootFloorIsInclusiveAndNeverRoundedUp()
    {
        MoveTuning valid = Contact with
        {
            CapsuleRadius = 0.02f,
            CapsuleHalfHeight = 0.1f,
            StepHeight = 0.04f,
            MaxSlopeRadians = 0f,
            TractionHysteresisRadians = 0f,
        };
        Assert.Equal(0.01f, valid.FootRadiusFraction * valid.CapsuleRadius);
        MovementConfigurationValidation.ValidateContactGeometry(valid);
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = 0f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = 1.001f }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = float.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(valid with { FootRadiusFraction = MathF.BitDecrement(0.5f) }));
        static void Check(MoveTuning tuning) => MovementConfigurationValidation.ValidateContactGeometry(tuning);
    }

    [Fact]
    public void LegacyRefusesContactOnlySettingsIncludingLegacyModeWithHop()
    {
        MoveTuning baseline = MoveTuning.Default;
        MovementConfigurationValidation.ValidateLegacy(baseline);
        // Released legacy tuning remains permissive, including zero vertical-physics constants.
        MovementConfigurationValidation.ValidateLegacy(baseline with { Gravity = 0, MaxFallSpeed = 0, SwimEnterDepthFraction = 0 });
        Assert.Throws<ArgumentException>(() => Legacy(baseline with { FootRadiusFraction = 0.75f }));
        Assert.Throws<ArgumentException>(() => Legacy(baseline with { Water = new(WaterTraversalMode.WadeOnly) }));
        Assert.Throws<ArgumentException>(() => Legacy(baseline with { Water = new(WaterTraversalMode.Legacy, 1f) }));
        static void Legacy(MoveTuning tuning) => MovementConfigurationValidation.ValidateLegacy(tuning);
    }

    [Fact]
    public void LegacyEntryPointsRefuseContactOnlySettings()
    {
        MoveTuning tuning = MoveTuning.Default with { FootRadiusFraction = 0.75f };
        Assert.Throws<ArgumentException>(() => CharacterMovement.Step(Vector3.Zero, MoveCommand.Idle, Dt, Flat, tuning));
        Assert.Throws<ArgumentException>(() => CharacterMovement.Step(Standing, MoveCommand.Idle, Dt, Flat, tuning));
        Assert.Throws<ArgumentException>(() => CharacterMovement.StepTowards(Standing, Vector2.UnitX, false, Dt, Flat, tuning));
    }

    [Fact]
    public void PositionOnlyContactNamesTheStatefulOverload()
    {
        var failure = Assert.Throws<NotSupportedException>(() =>
            CharacterMovement.Step(Vector3.Zero, MoveCommand.Idle, Dt, Flat, Contact));
        Assert.Contains("stateful", failure.Message);
        Assert.Contains("MoveState", failure.Message);
    }

    [Fact]
    public void DirectContactAcceptsDryMediumAndClampAndPreservesDirectionChoice()
    {
        using var world = new BepuPhysicsWorld();
        MoveTuning tuning = Contact with { FootRadiusFraction = 0.75f };
        var cmd = new MoveCommand(Vector2.UnitX, true, 0.37f, jump: true, faceCamera: true);
        Vector2 direction = new(MathF.Cos(cmd.CameraYaw), -MathF.Sin(cmd.CameraYaw));
        MoveState dryReference = ContactController.Step(Standing, direction, 1, true, true, Dt, Flat, tuning,
            null, world, Clamp, Dry, cmd.CameraYaw, new GroundCoreSettings(tuning.FootRadiusFraction));
        MoveState directResult = CharacterMovement.Step(Standing, cmd, Dt, Flat, tuning, world: world,
            clampXz: Clamp, medium: Dry);
        Assert.Equal(dryReference, directResult);
        Assert.Equal(cmd.CameraYaw, directResult.FacingYaw);
        Assert.True(directResult.VerticalVelocity > 0);

        MoveState preciseDryReference = ContactController.Step(Standing, Vector2.UnitX, 0.0001f, true, false, Dt,
            Flat, tuning, null, world, Clamp, Dry, settings: new GroundCoreSettings(tuning.FootRadiusFraction));
        MoveState preciseTowardsResult = CharacterMovement.StepTowards(Standing, new(0.0001f, 0), true, Dt, Flat,
            tuning, preserveSmallMagnitude: true, world: world, clampXz: Clamp, medium: Dry);
        Assert.Equal(preciseDryReference, preciseTowardsResult);
        Assert.True(preciseTowardsResult.Position.X > 0);
        Assert.Equal(Standing.Position, CharacterMovement.StepTowards(Standing, new(0.0001f, 0), true, Dt, Flat,
            tuning, world: world, clampXz: Clamp, medium: Dry).Position);
        Assert.Contains("IMovementWaterField", Assert.Throws<NotSupportedException>(() =>
            CharacterMovement.Step(Standing, cmd, Dt, Flat, tuning, world: world,
                medium: (_, _, _) => new(2, true))).Message);
    }

    [Fact]
    public void DirectContactAllowsAnalyticOnlyWithoutOrdinaryKernelRequirements()
    {
        MoveTuning tuning = Contact with { Gravity = 0, MaxFallSpeed = 0, SwimEnterDepthFraction = 0 };
        MovementConfigurationValidation.ValidateDryDirect(tuning, null);
        MoveState result = CharacterMovement.StepTowards(Standing, Vector2.UnitX, false, Dt, Flat, tuning);
        Assert.Equal(new Vector3(6f * Dt, tuning.CapsuleHalfHeight, 0), result.Position);
    }

    [Theory]
    [InlineData("span")]
    [InlineData("slope")]
    [InlineData("nonfinite")]
    public void InvalidContactShellIsRefusedByEveryContactValidation(string fault)
    {
        MoveTuning tuning = fault switch
        {
            "span" => Ordinary with { CapsuleHalfHeight = 0.5f },
            "slope" => Ordinary with { StepHeight = 0 },
            _ => Ordinary with { CapsuleRadius = float.PositiveInfinity },
        };
        using var world = new BepuPhysicsWorld();
        using var view = world.CreateQueryViewExcludingStatics([]);
        Assert.ThrowsAny<ArgumentException>(() => MovementConfigurationValidation.ValidateContactGeometry(tuning));
        Assert.ThrowsAny<ArgumentException>(() => MovementConfigurationValidation.ValidateDryDirect(tuning, world));
        Assert.ThrowsAny<ArgumentException>(() => OrdinaryCheck(tuning, view));
        Assert.ThrowsAny<ArgumentException>(() => MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water));
    }

    [Theory]
    [InlineData("gravity")]
    [InlineData("fall")]
    [InlineData("swim-enter")]
    [InlineData("walk-nan")]
    [InlineData("wade-order")]
    public void OrdinaryAndExplicitRequireValidKernelTuning(string fault)
    {
        MoveTuning tuning = fault switch
        {
            "gravity" => Ordinary with { Gravity = 0 },
            "fall" => Ordinary with { MaxFallSpeed = 0 },
            "swim-enter" => Ordinary with { SwimEnterDepthFraction = 0, SwimExitDepthFraction = 0 },
            "walk-nan" => Ordinary with { WalkSpeed = float.NaN },
            _ => Ordinary with { WadeEndDepthFraction = Ordinary.WadeStartDepthFraction },
        };
        using var world = new BepuPhysicsWorld();
        using var view = world.CreateQueryViewExcludingStatics([]);
        Assert.Throws<ArgumentException>(() => OrdinaryCheck(tuning, view));
        Assert.Throws<ArgumentException>(() => MovementConfigurationValidation.ValidateExplicit(tuning, tuning.Water));
    }

    [Theory]
    [InlineData("registration")]
    [InlineData("boundary")]
    [InlineData("legacy-water")]
    [InlineData("legacy-hop")]
    [InlineData("medium-without-field")]
    public void OrdinaryRefusesMissingConfiguration(string fault)
    {
        using var world = new BepuPhysicsWorld();
        using var view = world.CreateQueryViewExcludingStatics([]);
        MoveTuning tuning = fault switch
        {
            "legacy-water" => Contact,
            "legacy-hop" => Contact with { Water = new(WaterTraversalMode.Legacy, 1) },
            _ => Ordinary,
        };
        Assert.Throws<ArgumentException>(() => MovementConfigurationValidation.ValidateOrdinaryContact(tuning, view,
            hasRegistration: fault != "registration", hasBoundary: fault != "boundary",
            hasMediumDelegate: fault == "medium-without-field", hasWaterField: fault != "medium-without-field"));
    }

    [Fact]
    public void OrdinaryAcceptsCompleteConfigurationAndOptionalDryMedium()
    {
        using var world = new BepuPhysicsWorld();
        using var view = world.CreateQueryViewExcludingStatics([]);
        OrdinaryCheck(Ordinary, view);
        MovementConfigurationValidation.ValidateOrdinaryContact(Ordinary, view, true, true, false, false);
    }

    [Theory]
    [InlineData("lease", "IPhysicsQueryLeaseSource")]
    [InlineData("footprint", "IPhysicsSupportFootprint")]
    [InlineData("contacts", "IPhysicsCapsuleContacts")]
    [InlineData("sweep", "IPhysicsCapsuleSweep")]
    public void OrdinaryRefusesEachMissingViewCapability(string missing, string capability)
    {
        using var world = new BepuPhysicsWorld();
        using var view = Missing(world, missing);
        Assert.Contains(capability, Assert.Throws<NotSupportedException>(() => OrdinaryCheck(Ordinary, view)).Message);
    }

    [Theory]
    [InlineData("lease", "IPhysicsQueryLeaseSource")]
    [InlineData("footprint", "IPhysicsSupportFootprint")]
    public void DirectRefusesEachMissingWorldCapability(string missing, string capability)
    {
        using var world = new BepuPhysicsWorld();
        using var view = Missing(world, missing);
        Assert.Contains(capability, Assert.Throws<NotSupportedException>(() =>
            CharacterMovement.Step(Standing, MoveCommand.Idle, Dt, Flat, Contact, world: view)).Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void UndefinedSelectorIsRefusedAtEveryEntry(int selector)
    {
        MoveTuning tuning = MoveTuning.Default with { Controller = (MovementControllerKind)selector };
        using var world = new BepuPhysicsWorld();
        using var view = world.CreateQueryViewExcludingStatics([]);
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementConfigurationValidation.ValidateLegacy(tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementConfigurationValidation.ValidateContactGeometry(tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementConfigurationValidation.ValidateDryDirect(tuning, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrdinaryCheck(tuning, view));
        Assert.Throws<ArgumentOutOfRangeException>(() => MovementConfigurationValidation.ValidateExplicit(tuning, Ordinary.Water));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterMovement.Step(Vector3.Zero, MoveCommand.Idle, Dt, Flat, tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterMovement.Step(Standing, MoveCommand.Idle, Dt, Flat, tuning));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterMovement.StepTowards(Standing, Vector2.UnitX, false, Dt, Flat, tuning));
    }

    static void OrdinaryCheck(MoveTuning tuning, IPhysicsWorldQueryView view) =>
        MovementConfigurationValidation.ValidateOrdinaryContact(tuning, view, true, true, true, true);

    static CapabilityView Missing(BepuPhysicsWorld world, string capability) => capability switch
    {
        "lease" => new WithoutLease(world),
        "footprint" => new WithoutFootprint(world),
        "contacts" => new WithoutContacts(world),
        "sweep" => new WithoutSweep(world),
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    // Each non-owning adapter hides exactly one capability and forwards the others to a real owner.
    abstract class CapabilityView(BepuPhysicsWorld world) : IPhysicsWorldQueryView
    {
        public IPhysicsWorld SourceWorld => world;
        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => throw new NotSupportedException();
        public void RemoveStatic(StaticHandle handle) => throw new NotSupportedException();
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => throw new NotSupportedException();
        public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
        public Pose GetDynamicPose(DynamicBodyHandle handle) => world.GetDynamicPose(handle);
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) => world.GetDynamicVelocity(handle, out linear, out angular);
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) => throw new NotSupportedException();
        public bool IsAwake(DynamicBodyHandle handle) => world.IsAwake(handle);
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
        public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
        public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
        public void Step(float dt) => throw new NotSupportedException();
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) => world.Raycast(origin, direction, maxDistance, out hit, filter);
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) => world.SweepCapsule(capsule, pose, direction, maxDistance, out hit, filter);
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) => world.ComputePenetration(capsule, pose, out mtv);
        public void Dispose() { }
        public IPhysicsQueryLease AcquireQueryReadLease() => world.AcquireQueryReadLease();
        public CapsuleContactResult QueryCapsuleContacts(CapsuleShape capsule, Pose pose, float maxSeparationMetres, Span<CapsuleContact> destination, QueryFilter filter = default) => world.QueryCapsuleContacts(capsule, pose, maxSeparationMetres, destination, filter);
        public CapsuleSweepResult SweepCapsuleCertified(CapsuleShape capsule, Pose pose, Vector3 displacement, QueryFilter filter = default) => world.SweepCapsuleCertified(capsule, pose, displacement, filter);
        public SupportFootprintResult QuerySupportFootprint(IPhysicsQueryLease lease, in SupportFootprintRequest request, Span<SupportFootprintMember> members, Span<ulong> joins, QueryFilter filter = default) => world.QuerySupportFootprint(lease, request, members, joins, filter);
        public void AssertFootprintCurrent(in SupportFootprintResult result, IPhysicsQueryLease lease) => world.AssertFootprintCurrent(result, lease);
    }

    sealed class WithoutLease(BepuPhysicsWorld world) : CapabilityView(world), IPhysicsSupportFootprint, IPhysicsCapsuleContacts, IPhysicsCapsuleSweep;
    sealed class WithoutFootprint(BepuPhysicsWorld world) : CapabilityView(world), IPhysicsQueryLeaseSource, IPhysicsCapsuleContacts, IPhysicsCapsuleSweep;
    sealed class WithoutContacts(BepuPhysicsWorld world) : CapabilityView(world), IPhysicsQueryLeaseSource, IPhysicsSupportFootprint, IPhysicsCapsuleSweep;
    sealed class WithoutSweep(BepuPhysicsWorld world) : CapabilityView(world), IPhysicsQueryLeaseSource, IPhysicsSupportFootprint, IPhysicsCapsuleContacts;
}
