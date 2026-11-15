using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.NetWorld.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class PlayerColdPlacementTests
{
    // #1756. Numeric query replay never renews the public certificate's authority.
    [Fact]
    public void ActiveQueryCachesCannotRenewSpentCertificatesOrAuthorizeNewLeases()
    {
        using var controls = new ContactQueryCacheScope();
        using var scene = new WaterPrismScenario("flat-control");
        var simulator = scene.Simulator;
        var basis = scene.Basis;
        using var work = new PlacementWork();
        ContactQueryCacheScope.SetCached(true);
        using var proposals = new SupportProposalAdmissionCapture();
        MovementColdPlacement certificate;
        MovementQueryLeaseId firstId;
        MovementQueryLease firstLease;
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            var queries = read.Queries!;
            firstId = queries.Id;
            firstLease = queries;
            var framed = new FramedMovementState(OrdinaryPlayerMovement.ToPhysics(basis.Move, queries.Frame), queries.Frame, null);
            Assert.Equal(MovementStepOutcome.Advanced,
                simulator.Binding.CertifyPlayerPlacement(framed, queries, out certificate, read.Boundary).Outcome);
            var copy = certificate;
            long validations = work.Validations;
            var first = simulator.Binding.StepPlayerWithPlacement(framed, MoveCommand.Idle,
                WaterPrismScenario.Dt, queries, certificate, read.Boundary);
            Assert.Equal(validations, work.Validations);
            Assert.Equal(1, work.Reuses);
            var spent = simulator.Binding.StepPlayerWithPlacement(framed, MoveCommand.Idle,
                WaterPrismScenario.Dt, queries, copy, read.Boundary);
            Assert.Equal(validations + 1, work.Validations);
            Assert.Equal(1, work.Reuses);
            KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.AssertEqual(first, spent,
                KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.Policy.IncludeContinuityHandle);
        }
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            var queries = read.Queries!;
            Assert.NotSame(firstLease, queries);
            Assert.Equal(firstId.GeometryGeneration, queries.Id.GeometryGeneration);
            var framed = new FramedMovementState(OrdinaryPlayerMovement.ToPhysics(basis.Move, queries.Frame), queries.Frame, null);
            long validations = work.Validations;
            var stale = simulator.Binding.StepPlayerWithPlacement(framed, MoveCommand.Idle,
                WaterPrismScenario.Dt, queries, certificate, read.Boundary);
            Assert.Equal(validations + 1, work.Validations);
            Assert.Equal(1, work.Reuses);
            var expected = simulator.Binding.StepPlayer(framed, MoveCommand.Idle,
                WaterPrismScenario.Dt, queries, read.Boundary);
            KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.AssertEqual(expected, stale,
                KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.Policy.IncludeContinuityHandle);
        }
        output.WriteLine($"certificate: proposal-hits={proposals.Hits}, result-hits={TileWaterQueryWork.ResultHits}");
        Assert.True(proposals.Hits > 0);
        Assert.True(TileWaterQueryWork.ResultHits > 0);
    }
}
