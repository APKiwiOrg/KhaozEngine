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
    [Theory]
    [InlineData("selection")]
    [InlineData("tuning")]
    [InlineData("water")]
    [InlineData("boundary")]
    [InlineData("frame")]
    public void CertificateNeverAuthorizesADifferentBindingOrFramedBasis(string change)
    {
        using var environment = new ExplicitPlayerEnvironment();
        var tuning = ExplicitPlayerEnvironment.Tuning;
        var water = new WaterTraversalPolicy(WaterTraversalMode.SurfaceSwimmer, 7);
        using var binding = EffectiveMovementBinding.PublicExplicit(tuning, environment.Context, water);
        using var other = EffectiveMovementBinding.PublicExplicit(change == "tuning" ? tuning with { CapsuleRadius = 0.3f } : tuning,
            environment.Context, change == "water" ? new(WaterTraversalMode.WadeOnly, 0) : water);
        var basis = ExplicitPlayerEnvironment.Initial;
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(environment.Scope(basis, default), out var lease));
        using var queries = lease!;
        var framed = new FramedMovementState(basis.Move, queries.Frame, null);
        using var work = new PlacementWork();
        var validation = binding.CertifyPlayerPlacement(framed, queries, out var certificate);
        Assert.Equal(MovementStepOutcome.Advanced, validation.Outcome);
        var changed = change switch
        {
            "selection" => new FramedMovementState(framed.State, framed.Frame, new(new("world", "room"), null, queries.Identity)),
            "frame" => new FramedMovementState(framed.State, new(default, Vector3.Zero, 2), null),
            _ => framed
        };
        var target = change is "tuning" or "water" ? other : binding;
        var boundary = change == "boundary" ? MovementBoundary.Rectangle(-8, -8, 8, 8) : null;
        var actual = target.StepPlayerWithPlacement(changed, MoveCommand.Idle, 0.1f, queries, certificate, boundary);
        long validations = work.Validations;
        Assert.Equal(0, work.Reuses);
        var expected = target.StepPlayer(changed, MoveCommand.Idle, 0.1f, queries, boundary);
        Assert.Equal(expected, actual);
        KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.AssertEqual(expected, actual,
            KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.Policy.IncludeContinuityHandle);
        Assert.Equal(change == "frame" ? 1 : 2, validations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CertificateFromADisposedReadCannotAuthorizeAnotherLeaseEvenWithoutGenerationChanges(bool changeGeometry)
    {
        using var scene = new OrdinaryPlayerWorld();
        using var simulator = scene.Simulator();
        var basis = scene.Initial();
        MovementColdPlacement oldCertificate;
        MovementQueryLeaseId oldId;
        using var work = new PlacementWork();
        using (var read = simulator.BeginExplicitRead(basis))
        {
            oldId = read!.Queries!.Id;
            var framed = new FramedMovementState(basis.Move, read.Queries.Frame, null);
            simulator.Binding.CertifyPlayerPlacement(framed, read.Queries, out oldCertificate, read.Boundary);
        }
        if (changeGeometry) scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(
            new("world", "certificate-wall"), new BoxShape(Vector3.One), Pose.At(new(12, 2, 0)))]);
        using var next = simulator.BeginExplicitRead(basis);
        Assert.True(next!.BasisValid);
        var queries = next.Queries!;
        Assert.Equal(changeGeometry, oldId.GeometryGeneration != queries.Id.GeometryGeneration);
        var state = new FramedMovementState(basis.Move, queries.Frame, null);
        long before = work.Validations;
        var actual = simulator.Binding.StepPlayerWithPlacement(state, MoveCommand.Idle, 0.1f, queries, oldCertificate, next.Boundary);
        Assert.Equal(before + 1, work.Validations);
        Assert.Equal(0, work.Reuses);
        Assert.Equal(simulator.Binding.StepPlayer(state, MoveCommand.Idle, 0.1f, queries, next.Boundary), actual);
    }

    [Fact]
    public void CopiesOfThePublicCertificateShareOneUse()
    {
        using var scene = new OrdinaryPlayerWorld();
        using var simulator = scene.Simulator();
        var basis = scene.Initial();
        using var work = new PlacementWork();
        using var read = simulator.BeginExplicitRead(basis);
        var queries = read!.Queries!;
        var framed = new FramedMovementState(basis.Move, queries.Frame, null);
        simulator.Binding.CertifyPlayerPlacement(framed, queries, out var certificate, read.Boundary);
        var copy = certificate;
        var first = simulator.Binding.StepPlayerWithPlacement(framed, MoveCommand.Idle, 0.1f, queries, certificate, read.Boundary);
        Assert.Equal(2, work.Validations);
        Assert.Equal(1, work.Reuses);
        var second = simulator.Binding.StepPlayerWithPlacement(framed, MoveCommand.Idle, 0.1f, queries, copy, read.Boundary);
        Assert.Equal(first, second);
        Assert.Equal(3, work.Validations);
        Assert.Equal(1, work.Reuses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicExplicitReadReusesOnlyItsIdenticalFirstBasis(bool wet)
    {
        using var environment = new ExplicitPlayerEnvironment(surfaceY: wet ? 0f : null);
        using var simulator = new PlayerMoveSimulator((_, _) => throw new InvalidOperationException(),
            ExplicitPlayerEnvironment.Tuning, null, null, environment.Physics, null, SamplerSpace.World,
            new ExplicitPlayerMovement(environment.Context, environment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7)));
        var basis = ExplicitPlayerEnvironment.Initial;
        if (wet) { basis.Move.Position.Y = -0.5f; basis.Move.Grounded = false; }
        using var work = new PlacementWork();
        using var read = simulator.BeginExplicitRead(basis);
        Assert.True(read!.BasisValid);
        var actual = simulator.Step(basis, new MoveCommand(Vector2.UnitX, false, 0), 0.1f);
        var outcome = simulator.LastExplicitOutcome;
        Assert.Equal(1, work.Validations);
        Assert.Equal(1, work.Reuses);
        var expected = ReferenceStep(simulator, read, basis, new MoveCommand(Vector2.UnitX, false, 0), 0.1f);
        Assert.Equal(expected.Outcome, outcome);
        AssertExact(expected.State, actual);
    }
}
