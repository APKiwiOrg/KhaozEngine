using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics.Collidables;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;
using static KhaozEngine.Tests.Physics.CertifiedCurvedSweepTests;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedCurvedSweepLifetimeTests
{
    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedLeafFramesReuseViewsAndWarmQueries(string family)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(Shape(family, 0.25f), Centred(family, 1, Turn(1)));
        Vector3 start = new(-2, 0, 0), delta = new(4, 0, 0);
        HitContains(Read(world, start, delta, length: 0), 1.5, 1.5);
        var captures = (Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        InstalledSweepGeometry previous = captures[handle.Value]!;
        var old = Static(world, handle);
        world.Rebase(new(16, 8, -16));
        world.Rebase(world.Origin);
        HitContains(Read(world, start - world.Origin, delta, length: 0), 1.5, 1.5);
        Clear(Read(world, start - world.Origin, delta, length: 0, exclusions: [handle]), 4);
        world.RemoveStatic(handle);
        handle = world.AddStatic(Shape(family, 0.5f), Centred(family, 1, Turn(1), -world.Origin));
        var replacement = Static(world, handle);
        Assert.Equal(old.Handle, replacement.Handle);
        Assert.Equal(old.Shape, replacement.Shape);
        Assert.NotSame(previous, captures[handle.Value]);
        HitContains(Read(world, start - world.Origin, delta, length: 0), 1.25, 1.25);
        InstalledSweepGeometry current = captures[handle.Value]!;
        captures[handle.Value] = previous;
        Refused(Read(world, start - world.Origin, delta, length: 0));
        captures[handle.Value] = current;

        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        foreach ((float radius, float length) in new[] { (0.4f, 0.59999996f), (0.40000007f, 0.6000001f), (0.25f, 0f) })
        {
            var dimensions = new CapsuleQueryDimensions(radius, length);
            var shape = new CapsuleShape(radius, length);
            Pose pose = Pose.At(start - world.Origin);
            CapsuleSweepResult expected = world.SweepCapsuleCertified(shape, pose, delta);
            Assert.True(expected.IsComplete);
            SameBits(expected, world.SweepCapsuleCertified(dimensions, pose, delta));
            CapsuleSweepResult a = default, b = default;
            long bytes = AllocationMeasurement.Measure(nameof(CurvedLeafFramesReuseViewsAndWarmQueries), () =>
            {
                a = world.SweepCapsuleCertified(shape, pose, delta);
                b = world.SweepCapsuleCertified(dimensions, pose, delta);
            });
            Assert.Equal(0, bytes);
            SameBits(expected, a); SameBits(expected, b);
        }
        var boundaryDimensions = new CapsuleQueryDimensions(0.25f, 0);
        Pose boundaryPose = Pose.At(new Vector3(-0.75f, 0, 0) - world.Origin);
        HitContains(world.SweepCapsuleCertified(boundaryDimensions, boundaryPose, Vector3.Zero), 0, 0);
        CapsuleSweepResult boundary = default;
        long boundaryBytes = AllocationMeasurement.Measure(nameof(CurvedLeafFramesReuseViewsAndWarmQueries) + "_boundary", () =>
            boundary = world.SweepCapsuleCertified(boundaryDimensions, boundaryPose, Vector3.Zero));
        Assert.Equal(0, boundaryBytes);
        HitContains(boundary, 0, 0);
    }

    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedLeafWorkCeilingRefusesAtomically(string family)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(Shape(family, 0.25f), Centred(family, 1, Quaternion.Identity));
        HitContains(Read(world, Vector3.Zero, Vector3.Zero, length: 0), 0, 0);
        var entry = Static(world, handle);
        var geometry = ((Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;
        Sim(world).Statics.GetDescription(entry.Handle, out var installed);
        var work = new CapsuleSweepWork { Descendants = CapsuleGeneralSweepPolicy.MaximumDescendants };
        var scratch = new CapsuleSweepScratch();
        var shapes = Sim(world).Shapes;
        Refused(CapsuleGeneralSweep.Sweep(shapes, entry.Shape, installed.Pose, geometry,
            new(0.25f, 0), Vector3.Zero, Vector3.Zero, default, ref work, scratch));
        Assert.True(work.Exhausted);
        Assert.Equal(CapsuleGeneralSweepPolicy.MaximumDescendants, work.Descendants);
        CapsuleSweepResult measured = default;
        long bytes = AllocationMeasurement.Measure(nameof(CurvedLeafWorkCeilingRefusesAtomically), () =>
        {
            work = new() { Descendants = CapsuleGeneralSweepPolicy.MaximumDescendants };
            measured = CapsuleGeneralSweep.Sweep(shapes, entry.Shape, installed.Pose, geometry,
                new(0.25f, 0), Vector3.Zero, Vector3.Zero, default, ref work, scratch);
        });
        Assert.Equal(0, bytes);
        Refused(measured);
    }

    [Theory]
    [InlineData("sphere")]
    [InlineData("capsule")]
    [InlineData("cylinder")]
    public void CurvedDynamicSleepWakeAndMutationUseTheLeasePose(string family)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var body = world.AddDynamic(Shape(family, 0.25f), Centred(family, 1, Turn(1), new(40, 0, 0)), new DynamicBodyDescription(1));
        var entries = (Dictionary<int, (BepuPhysics.BodyHandle Handle, TypedIndex Shape)>)Field(world, "_dynamics");
        Mutate(world, sim => { var reference = sim.Bodies[entries[body.Value].Handle]; reference.Awake = false; });
        Assert.False(world.IsAwake(body));
        HitContains(Read(world, new(38, 0, 0), new(4, 0, 0), length: 0, filter: QueryFilter.DynamicsOnly), 1.5, 1.5);
        Clear(Read(world, new(38, 0, 0), new(4, 0, 0), length: 0, filter: QueryFilter.StaticsOnly), 4);
        world.SetDynamicVelocity(body, Vector3.UnitX, Vector3.Zero);
        Assert.True(world.IsAwake(body));
        world.Step(1f / 30);
        Pose pose = world.GetDynamicPose(body);
        Vector3 centre = pose.Position + (family == "cylinder" ? Vector3.Transform(new(0, 0.5f, 0), pose.Orientation) : Vector3.Zero);
        HitContains(Read(world, centre + new Vector3(-2, 0, 0), new(4, 0, 0), length: 0, filter: QueryFilter.DynamicsOnly), 1.5, 1.5);
        world.RemoveDynamic(body);
        Clear(Read(world, new(38, 0, 0), new(4, 0, 0), length: 0), 4);
    }

    public static IEnumerable<object[]> LogRows() => ContactFollowupTraversalTests.LogRows();

    [Theory]
    [MemberData(nameof(LogRows))]
    public void LyingLogLeasedTraversalAdvances(bool capsule, float radius, bool aboutX, int sign, bool stopOnProp)
    {
        var (scene, obstacle, direction) = ContactFollowupScenes.Log(capsule, radius, aboutX);
        using (scene)
        using (IPhysicsWorldQueryView view = scene.World.CreateQueryViewExcludingStatics([]))
        {
            Vector2 along = sign * direction;
            Vector2 target = stopOnProp ? Vector2.Zero : 0.75f * along;
            Vector3 feet = new(-0.75f * along.X, 0, -0.75f * along.Y);
            var tuning = MoveTuning.Default with { WalkSpeed = 1 };
            var environment = new EnvironmentAcquisitionFixture(view)
            { ContactSupportEnabled = true, Point = new(MovementAvailability.Known, new("world", "room"), null, false, 0, null) };
            environment.OnContactSupport = (in MovementContactSupportRequest _, out MovementContactSupportBinding binding) =>
            {
                binding = new(new("world", "prop"), new("world", "room"), null, environment.Identity);
                return MovementAvailability.Known;
            };
            environment.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> _) =>
            {
                spans[0] = new(0, 1, 0, 0, true);
                return new(MovementAvailability.Known, 1, 1, 0, 0, 0, environment.Identity);
            };
            MoveState state = new() { Position = feet + new Vector3(0, tuning.CapsuleHalfHeight + 0.001f, 0), Grounded = true };
            bool supported = false;
            for (int tick = 0; tick < ContactFollowupTraversalTests.MaxTicks; tick++)
            {
                var acquired = environment.Acquire();
                Assert.Equal(MovementAvailability.Known, acquired.Status);
                using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
                Vector2 remaining = target - new Vector2(state.Position.X, state.Position.Z);
                float dt = MathF.Min(1f / 60, remaining.Length());
                if (dt == 0) dt = 1f / 60;
                var input = new FramedMovementState(state, lease.Frame, new(new("world", "room"), null, lease.Identity));
                Vector2 command = remaining.LengthSquared() > 0 ? Vector2.Normalize(remaining) : Vector2.Zero;
                var result = ExplicitCharacterMovement.StepTowards(input, command, false, dt, tuning,
                    new(WaterTraversalMode.DryOnly), lease);
                Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
                state = result.State.State;
                feet = state.Position - new Vector3(0, tuning.CapsuleHalfHeight + 0.001f, 0);
                supported |= feet.Y > radius;
                if (Vector2.Distance(new(feet.X, feet.Z), target) <= 0.001f && state.StepDeltaY == 0) break;
            }
            Assert.True(supported);
            Assert.True(Vector2.Distance(new(feet.X, feet.Z), target) <= 0.001f, $"Stopped at {feet}");
            var query = new FootSupportQuery(target, feet.Y, ContactFollowupTraversalTests.FootRadius,
                tuning.StepHeight, tuning.StepHeight, ContactFollowupTraversalTests.CosMaxSlope);
            var oracle = ContactFollowupScenes.LogSupport(capsule, radius, aboutX, query);
            Assert.InRange(Math.Abs(feet.Y - oracle.Height), 0, 0.001);
            Assert.Equal(stopOnProp ? obstacle : scene["floor"], oracle.Static);
        }
    }
}
