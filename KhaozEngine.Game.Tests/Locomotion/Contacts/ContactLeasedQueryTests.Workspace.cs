using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Fact]
    public void ReusedWorkspaceMatchesColdAfterRefusalAndInterleavedAirTick()
    {
        using var scene = new QueryScene(_ => 0);
        var state = new FramedMovementState(new MoveState { Position = new(0, 0.751f, 0), Grounded = true },
            scene.Lease.Frame, new(Room, null, scene.Lease.Identity));
        MovementStepResult Run(FramedMovementState input) => ContactController.StepLeased(input, Vector2.UnitX,
            1, false, false, 1f / 30, Tuning, new(WaterTraversalMode.DryOnly), scene.Lease);
        MovementStepResult cold = Run(state);
        Assert.Equal(MovementStepOutcome.Advanced, cold.Outcome);
        scene.Environment.OnSample = () => throw new InvalidOperationException("The producer refused this tick.");
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, Run(state).Outcome);
        scene.Environment.OnSample = null;
        var airborne = new FramedMovementState(state.State with
        { Position = new(0, 1.4f, 0), Grounded = false, VerticalVelocity = -1 }, state.Frame, state.Selection);
        MovementStepResult air = Run(airborne);
        Assert.Equal(MovementStepOutcome.Advanced, air.Outcome);
        Assert.False(air.State.State.Grounded);
        Assert.Equal(cold, Run(state));
    }

    [Fact]
    public void AnalyticConsistencyEndsAtTickButFiniteOwnerConsistencyEndsAtInterval()
    {
        using var geometry = new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 4, -4, 4, 0);
        using var scene = new QueryScene(geometry);
        var request = new MovementSupportRequest(new(new(0, 0.751f, 0), 0.25f, 0.75f, Room, null),
            0.3f, 0.3f, MathF.PI / 4, new(Room, Vector3.Zero));
        var sample = new SupportSample(SupportStatus.Walkable, 0, 0, Vector3.UnitY,
            geometry["floor"], 0, Vector3.Zero);
        scene.Lease.BeginContactTick();
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(request, out _));
        Assert.Equal(MovementAvailability.Known, scene.Lease.BindContactSupport(request, sample, out _));
        scene.Lease.EndContactTick();
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest query, out MovementAnalyticGround? ground) =>
        {
            ground = new(new(query.Body.Centre.X, query.Body.Centre.Z), 0, Vector3.UnitY,
                new("world", "floor"), Room, null, scene.Lease.Identity);
            return MovementAvailability.Known;
        };
        scene.Environment.OnContactSupport = (in MovementContactSupportRequest query, out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "changed"), Room, null, scene.Lease.Identity);
            return MovementAvailability.Known;
        };
        scene.Lease.BeginContactTick();
        Assert.Equal(MovementAvailability.Known, scene.Lease.SampleAnalyticGround(request, out _));
        Assert.Equal(MovementAvailability.Invalid, scene.Lease.BindContactSupport(request, sample, out _));
        scene.Lease.EndContactTick();
        scene.Lease.Dispose();
        using var next = Assert.IsType<MovementQueryLease>(scene.Environment.Acquire().Lease);
        Assert.Equal(MovementAvailability.Known, next.BindContactSupport(request, sample, out _));
    }

    [Fact]
    public void ExpiredTickCannotClearAReacquiredIntervalsConsistency()
    {
        using var scene = new QueryScene(_ => 0);
        scene.Lease.BeginContactTick();
        scene.Lease.Dispose();
        using var next = Assert.IsType<MovementQueryLease>(scene.Environment.Acquire().Lease);
        next.BeginContactTick();
        var request = new MovementSupportRequest(new(new(0, 0.751f, 0), 0.25f, 0.75f, Room, null),
            0.3f, 0.3f, MathF.PI / 4, new(Room, Vector3.Zero));
        Assert.Equal(MovementAvailability.Known, next.SampleAnalyticGround(request, out _));
        scene.Lease.EndContactTick();
        scene.Environment.OnAnalyticGround = (in MovementSupportRequest query, out MovementAnalyticGround? ground) =>
        {
            ground = null;
            return MovementAvailability.Known;
        };
        Assert.Equal(MovementAvailability.Invalid, next.SampleAnalyticGround(request, out _));
        next.EndContactTick();
    }
}
