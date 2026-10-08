using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

// Finite dry room and real solid floor. This is not a native producer or a second movement solver.
internal sealed class ExplicitPlayerEnvironment : IMovementEnvironmentProvider, IDisposable
{
    public BepuPhysicsWorld Physics { get; } = new(Vector3.Zero);
    public IPhysicsWorldQueryView View { get; }
    public MovementEnvironmentContext Context { get; }
    public MovementQueryIdentity Identity { get; } = new("explicit-player-fixture", 1, "dry-room");
    public string WorldId => "world";
    public MovementAvailability Availability = MovementAvailability.Known;
    public readonly List<Vector3> RebuiltPositions = [];
    public int Prepared;
    public int PinsDisposed;
    public Action? OnPinDispose;
    public static MoveTuning Tuning => MoveTuning.Default with
    {
        CapsuleRadius = 0.25f,
        CapsuleHalfHeight = 0.75f,
        WalkSpeed = 4,
        RunSpeed = 8,
        Gravity = 20,
        GroundedEpsilon = 0.05f,
        StepHeight = 0.4f,
        SwimSpeed = 3
    };
    public static PlayerMoveState Initial => new()
    {
        Move = new MoveState { Position = new(0, 0.751f, 0), Grounded = true },
        FrameAnchor = Vector2.Zero
    };

    public ExplicitPlayerEnvironment()
    {
        Physics.AddStatic(new BoxShape(new(8, 0.125f, 8)), Pose.At(new(0, -0.125f, 0)));
        View = Physics.CreateQueryViewExcludingStatics([]);
        Context = new(View, this, Identity);
    }

    public MovementQueryScope Scope(PlayerMoveState state, WorldFrame frame) =>
        new(new(-16), new(16), 1, 4, WorldId, null, Identity, new(frame, Physics.Origin, 1));

    public MovementPreparationResult Prepare(in MovementQueryScope scope)
    {
        Prepared++;
        return new(Availability, Identity);
    }
    public MovementAvailability TryPinPrepared(in MovementQueryScope scope, IPhysicsQueryLease physicsLease,
        out IMovementEnvironmentPin? pin)
    {
        pin = new Pin(this, scope, physicsLease);
        return MovementAvailability.Known;
    }

    sealed class Pin : IMovementEnvironmentPin
    {
        readonly ExplicitPlayerEnvironment owner;
        readonly IPhysicsQueryLease physics;
        public Pin(ExplicitPlayerEnvironment owner, MovementQueryScope scope, IPhysicsQueryLease physics)
        {
            this.owner = owner;
            this.physics = physics;
            if (MovementScopeWitness.TryCreate(scope, owner.Identity, new[] { "dry-room", "solid-floor" }, true,
                out var witness) != MovementAvailability.Known) throw new InvalidOperationException("Invalid fixture witness.");
            Witness = witness!;
        }
        public IPhysicsWorldQueryView PhysicsView => owner.View;
        public long GeometryGeneration => physics.GeometryGeneration;
        public long EnvironmentGeneration => 1;
        public MovementFrameDescriptor Frame => Witness.Scope.Frame;
        public MovementScopeWitness Witness { get; }
        public void AssertCurrent() => physics.AssertCurrent();
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body) =>
            new(MovementAvailability.Known, new("world", "room"), null, false, 1, null);
        public MovementSupportSet EnumerateSupport(in MovementSupportRequest request, Span<MovementSupportCandidate> candidates)
        {
            bool floor = Math.Abs(request.Body.Centre.X) < 8 && Math.Abs(request.Body.Centre.Z) < 8;
            if (floor) candidates[0] = new(new("world", "floor"), new("world", "room"),
                new(request.Body.Centre.X, 0, request.Body.Centre.Z), Vector3.UnitY, null);
            return new(MovementAvailability.Known, floor ? 1 : 0, floor ? 1 : 0, owner.Identity);
        }
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts)
        {
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, 0, owner.Identity);
        }
        public MovementAvailability RebuildSelection(in FramedMovementState state, out MovementSelection selection)
        {
            owner.RebuiltPositions.Add(state.State.Position);
            selection = new(new("world", "room"), null, owner.Identity);
            return MovementAvailability.Known;
        }
        public void Dispose() { owner.OnPinDispose?.Invoke(); owner.PinsDisposed++; }
    }
    public void Dispose() { View.Dispose(); Physics.Dispose(); }
}
