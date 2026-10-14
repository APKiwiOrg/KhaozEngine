using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Contacts;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

[Collection("AllocSensitive")]
public sealed class OrdinaryContactAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1f, 1e-30f, 1d, 1)]
    [InlineData(1f, -1e-30f, 1d, -1)]
    [InlineData(1f, -1f, 0d, 0)]
    [InlineData(float.Epsilon, -float.Epsilon, double.Epsilon, -1)]
    [InlineData(float.Epsilon, 0f, double.Epsilon, 1)]
    [InlineData(-float.Epsilon, 0f, -double.Epsilon, -1)]
    [InlineData(float.MaxValue, -float.MaxValue, double.Epsilon, -1)]
    [InlineData(float.MaxValue, float.MaxValue, double.MaxValue, -1)]
    [InlineData(-float.MaxValue, -float.MaxValue, -double.MaxValue, 1)]
    [InlineData(0f, 0f, 0d, 0)]
    public void RectangularComparisonKeepsExactSignsWithoutAllocating(float start, float delta, double plane, int sign)
    {
        using var region = SupportAllocationRegion.Start();
        int cold = MovementBoundaryArithmetic.Compare(start, delta, plane);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int warm = MovementBoundaryArithmetic.Compare(start, delta, plane);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(sign, cold);
        Assert.Equal(sign, warm);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void RectangularBoundaryProofAllocatesNothingForAnInteriorSmallCorrection()
    {
        var boundary = MovementBoundary.Rectangle(-20, -20, 20, 20);
        Vector3 start = new(0.4f, 0.75f, 0), delta = new(MathF.ScaleB(1, -54), 0, 0), end = start + delta;
        using var region = SupportAllocationRegion.Start();
        var cold = boundary.ProveSegment(start, delta, end, out bool coldAdmitted);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var warm = boundary.ProveSegment(start, delta, end, out bool warmAdmitted);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(MovementAvailability.Known, cold);
        Assert.Equal(MovementAvailability.Known, warm);
        Assert.True(coldAdmitted);
        Assert.True(warmAdmitted);
        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData("flat box")]
    [InlineData("SurfaceSwimmer")]
    [InlineData("water crossing")]
    public void WarmOrdinaryContactTickAllocatesOnlyNamedLifetimeTokensAndReadScope(string row)
    {
        var tokens = Inventory();
        using var scene = new OrdinaryAllocationScene(row);
        using var simulator = scene.Simulator();
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        // Frame scope, witness and boundary belong to configuration, before the tick comparison.
        _ = simulator.Binding.CreateOrdinaryScope(frame);
        _ = simulator.Binding.CaptureOrdinaryBoundary(frame);
        var basis = scene.Basis;
        var command = scene.Command;
        _ = Stopwatch.GetTimestamp();
        PlayerMoveState coldMove, measuredMove;
        MovementStepOutcome? coldOutcome, warmOutcome;
        long coldBytes, warmTickBytes, kernelAndFieldWarmBytes, coldElapsed, warmElapsed;
        using (var region = SupportAllocationRegion.Start())
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            using (var read = simulator.BeginExplicitRead(basis))
            {
                coldMove = simulator.Step(basis, command, OrdinaryAllocationScene.Dt);
                coldOutcome = simulator.LastExplicitOutcome;
            }
            coldElapsed = Stopwatch.GetTimestamp() - started;
            coldBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            using (var read = simulator.BeginExplicitRead(basis))
            {
                long kernelBefore = GC.GetAllocatedBytesForCurrentThread();
                measuredMove = simulator.Step(basis, command, OrdinaryAllocationScene.Dt);
                warmOutcome = simulator.LastExplicitOutcome;
                kernelAndFieldWarmBytes = GC.GetAllocatedBytesForCurrentThread() - kernelBefore;
            }
            warmElapsed = Stopwatch.GetTimestamp() - started;
            warmTickBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        using var reference = scene.Reference();
        var scope = reference.CreateOrdinaryScope(frame);
        Assert.Equal(MovementAvailability.Known, reference.TryAcquire(scope, out var queries));
        MovementStepResult referenceMove;
        using (queries) referenceMove = reference.Step(new(basis.Move, frame, null), command,
            OrdinaryAllocationScene.Dt, queries!);
        output.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, Release. " +
            $"TieredCompilation {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}. " +
            $"Stopwatch frequency {Stopwatch.Frequency} Hz.");
        output.WriteLine($"ReadLease {tokens.Physics} B, OrdinaryMovementEnvironmentPin {tokens.Pin} B, " +
            $"MovementQueryLease and context release binding {tokens.Movement} B, PlayerMoveReadScope {tokens.ReadScope} B.");
        output.WriteLine($"{row}. Cold {coldBytes} B, {coldElapsed} ticks. Warm {warmTickBytes} B, {warmElapsed} ticks. " +
            $"Kernel and field {kernelAndFieldWarmBytes} B. Outcomes {coldOutcome}/{warmOutcome}. " +
            $"Position {measuredMove.Position}, swimming {measuredMove.Swimming}.");
        Assert.Equal(MovementStepOutcome.Advanced, coldOutcome);
        Assert.Equal(MovementStepOutcome.Advanced, warmOutcome);
        Assert.Equal(MovementStepOutcome.Advanced, referenceMove.Outcome);
        Assert.Equal(coldMove, measuredMove);
        Assert.Equal(referenceMove.State.State, measuredMove.Move);
        Assert.True(measuredMove.Position.X > basis.Position.X);
        if (row == "flat box") Assert.True(measuredMove.Grounded);
        else Assert.True(measuredMove.Swimming);
        if (row == "water crossing") Assert.False(basis.Swimming);
        Assert.Equal(tokens.Physics + tokens.Pin + tokens.Movement + tokens.ReadScope, warmTickBytes);
        Assert.Equal(0, kernelAndFieldWarmBytes);
    }

    static (long Physics, long Pin, long Movement, long ReadScope) Inventory()
    {
        using var scene = new OrdinaryAllocationScene("flat box");
        using var view = scene.Physics.CreateQueryViewExcludingStatics([]);
        var environment = new OrdinaryMovementEnvironment(view, scene.Registration, scene.Tuning,
            scene.Ground, null, scene.Boundary);
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 1);
        var scope = environment.CreateScope(frame);
        Assert.Equal(MovementAvailability.Known, environment.Prepare(scope).Availability);
        var workspace = new ContactTickWorkspace();
        Action<PlayerMoveReadScope> releaseRead = ReleasedRead;
        using var region = SupportAllocationRegion.Start();
        using (var cold = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease()) { }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var physics = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        long physicsBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(MovementAvailability.Known, environment.TryPinPrepared(scope, physics, out var coldPin));
        coldPin!.Dispose();
        before = GC.GetAllocatedBytesForCurrentThread();
        var availability = environment.TryPinPrepared(scope, physics, out var pin);
        long pinBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(MovementAvailability.Known, availability);
        var witness = pin!.Witness;
        before = GC.GetAllocatedBytesForCurrentThread();
        var movement = new MovementQueryLease(view, physics, pin, witness, frame, pin.EnvironmentGeneration,
            new Action(Released), workspace);
        long movementBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        movement.Dispose();
        using (var coldRead = new PlayerMoveReadScope(null, MovementStepOutcome.Advanced, releaseRead)) { }
        before = GC.GetAllocatedBytesForCurrentThread();
        var read = new PlayerMoveReadScope(null, MovementStepOutcome.Advanced, releaseRead);
        long readBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        read.Dispose();
        return (physicsBytes, pinBytes, movementBytes, readBytes);
    }

    static void Released() { }
    static void ReleasedRead(PlayerMoveReadScope read) { }

    sealed class OrdinaryAllocationScene : IDisposable
    {
        internal const float Dt = 0.1f;
        internal BepuPhysicsWorld Physics { get; } = new(Vector3.Zero);
        internal MoveTuning Tuning { get; } = MoveTuning.Default with
        {
            Controller = MovementControllerKind.Contact,
            Water = new(WaterTraversalMode.SurfaceSwimmer, 7),
            CapsuleRadius = 0.25f,
            CapsuleHalfHeight = 0.75f,
            WalkSpeed = 4,
            RunSpeed = 8,
            Gravity = 20,
            StepHeight = 0.4f
        };
        internal OrdinaryMovementRegistration Registration { get; }
        internal MovementBoundary Boundary { get; } = MovementBoundary.Rectangle(-20, -20, 20, 20);
        internal Func<float, float, float> Ground { get; }
        internal IMovementWaterField? Water { get; }
        internal PlayerMoveState Basis { get; }
        internal MoveCommand Command { get; } = new(Vector2.UnitX, false, 0);

        internal OrdinaryAllocationScene(string row)
        {
            float h = row == "flat box" ? 0 : -4;
            Ground = (_, _) => h;
            Water = row == "flat box" ? null : MovementWaterField.Level("world", 0, -4);
            var minimum = new Vector3(-4, h - 0.5f, -4);
            var maximum = new Vector3(4, h, 4);
            var builder = OrdinaryMovementRegistration.CreateBuilder("world", Physics,
                new(new(-20, -8, -20), new(20, 8, 20), h, h));
            var handle = Physics.AddStatic(new BoxShape((maximum - minimum) * 0.5f),
                Pose.At((maximum + minimum) * 0.5f));
            builder.Add(handle, 0, int.MaxValue, new("world", "floor"), minimum, maximum);
            using var lease = Physics.AcquireQueryReadLease();
            Registration = builder.Build(lease);
            Basis = new()
            {
                Move = new()
                {
                    Position = new(0, row == "flat box" ? 0.75f : row == "water crossing" ? 1.25f : -1, 0),
                    Grounded = row == "flat box",
                    Swimming = row == "SurfaceSwimmer",
                    WaterExcursion = row == "SurfaceSwimmer" ? WaterExcursionState.Surface : WaterExcursionState.None,
                    VerticalVelocity = row == "water crossing" ? -12 : 0
                },
                FrameAnchor = Vector2.Zero
            };
        }

        internal PlayerMoveSimulator Simulator() => new(Ground, Tuning, null, null, Physics,
            null, SamplerSpace.World, null, Water, Registration, Boundary);
        internal EffectiveMovementBinding Reference() => EffectiveMovementBinding.Ordinary(Tuning, Physics,
            Registration, Ground, null, Boundary, Water);
        public void Dispose() => Physics.Dispose();
    }
}
