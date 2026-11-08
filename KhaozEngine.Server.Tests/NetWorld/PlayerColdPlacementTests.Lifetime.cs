using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class PlayerColdPlacementTests
{
    [Fact]
    public void FrameMismatchConsumesTheFirstAttemptAndPhysicsCannotChangeWithinTheRead()
    {
        using var scene = new OrdinaryPlayerWorld();
        using var simulator = scene.Simulator();
        var basis = scene.Initial();
        using var work = new PlacementWork();
        using var read = simulator.BeginExplicitRead(basis);
        var mismatched = basis;
        mismatched.FrameAnchor = new(256, 0);
        var actual = simulator.Step(mismatched, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
        Assert.Equal(MovementStepOutcome.FrameMismatch, simulator.LastExplicitOutcome);
        AssertExact(ReferenceStep(simulator, read!, mismatched, MoveCommand.Idle, OrdinaryPlayerWorld.Dt).State, actual);
        Assert.Throws<InvalidOperationException>(() => simulator.Frame = WorldFrame.Nearest(256, 0));
        Assert.Throws<InvalidOperationException>(() => scene.Physics.AddStatic(new BoxShape(Vector3.One), Pose.At(Vector3.Zero)));
        simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
        Assert.Equal(2, work.Validations);
        Assert.Equal(0, work.Reuses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidCommandOrDurationConsumesTheCertificate(bool commandInvalid)
    {
        using var scene = new OrdinaryPlayerWorld();
        using var simulator = scene.Simulator();
        var basis = scene.Initial();
        using var work = new PlacementWork();
        using var read = simulator.BeginExplicitRead(basis);
        var command = commandInvalid ? new MoveCommand(new(float.NaN, 0), false, 0) : MoveCommand.Idle;
        var actual = simulator.Step(basis, command, commandInvalid ? OrdinaryPlayerWorld.Dt : 0);
        Assert.Equal(MovementStepOutcome.EnvironmentInvalid, simulator.LastExplicitOutcome);
        AssertExact(ReferenceStep(simulator, read!, basis, command, commandInvalid ? OrdinaryPlayerWorld.Dt : 0).State, actual);
        simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
        Assert.Equal(2, work.Validations);
        Assert.Equal(0, work.Reuses);
    }

    [Fact]
    public void WaterGenerationChangeRefusesTheCapturedLeaseAndTheNextReadRevalidates()
    {
        using var field = new ColdWaterField();
        using var scene = new OrdinaryPlayerWorld(field: field);
        using var simulator = scene.Simulator();
        var basis = scene.Initial(-2);
        using var work = new PlacementWork();
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            field.Generation++;
            var actual = Assert.Throws<MovementReadException>(() => simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt));
            var expected = Assert.Throws<MovementReadException>(() => ReferenceStep(simulator, read, basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt));
            Assert.Equal(MovementReadRefusal.EnvironmentPinStale, actual.Reason);
            Assert.Equal(expected.Reason, actual.Reason);
            Assert.Equal(0, work.Reuses);
        }
        long before = work.Validations;
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            Assert.Equal(before + 1, work.Validations);
            simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt);
            Assert.Equal(before + 1, work.Validations);
            Assert.Equal(1, work.Reuses);
        }
    }

    sealed class ColdWaterField : IMovementWaterField, IDisposable
    {
        readonly OrdinaryShoreField shore = new();
        public string SemanticIdentity => shore.SemanticIdentity;
        public long Generation { get; set; }
        public MovementWaterExtent Extent => shore.Extent;
        internal MovementAvailability Status { get; set; } = MovementAvailability.Known;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
            shore.SampleCentreWater(body, physicsOrigin);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            Status == MovementAvailability.Known ? shore.TraceWater(query, physicsOrigin, identity, spans, contacts)
                : new(Status, 0, 0, 0, 0, 0, identity);
        public void Dispose() => shore.Dispose();
    }
}
