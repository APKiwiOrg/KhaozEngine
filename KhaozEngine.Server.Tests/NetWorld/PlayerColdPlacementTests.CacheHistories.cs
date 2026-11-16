using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.NetWorld.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class PlayerColdPlacementTests
{
    void CompareCacheHistory(string history)
    {
        using var controls = new ContactQueryCacheScope();
        using var scene = new WaterPrismScenario(history is "dry" or "collider" ? "dry-control"
            : history is "flat-water" or "rebase" ? "flat-control" : "bank-stand", crowded: history == "collider");
        scene.Colliders.Medium.ResetTraceMemoForTests();
        SupportProposalAdmission.ResetForTests();
        var cached = scene.Basis;
        var uncached = scene.Basis;
        long proposalHits = 0, proposalBypasses = 0;
        int smallHits = 0, resultHits = 0, cleared = 0;
        for (int tick = 0; tick < 12; tick++)
        {
            if (history == "rebase" && tick is 4 or 8)
            {
                scene.Rebase(tick == 4 ? new(128, 4, -256) : new(0, -2, 0));
                var anchor = new Vector2(scene.Simulator.Frame.Anchor.X, scene.Simulator.Frame.Anchor.Z);
                cached = cached.ToAnchor(anchor);
                uncached = uncached.ToAnchor(anchor);
            }
            var command = history is "collider" or "motion" or "reconcile" or "rebase"
                ? new MoveCommand(Vector2.UnitX, false, 0) : MoveCommand.Idle;
            // Both runs receive exactly the same carried bits, including correction and commitment state.
            AssertExact(cached, uncached);
            ContactQueryCacheScope.SetCached(true);
            using var optimizedWork = new SupportProposalAdmissionCapture();
            var actual = RunHistoryRead(scene.Simulator, cached, command, history == "reconcile", tick);
            proposalHits += optimizedWork.Hits;
            smallHits += TileWaterQueryWork.MemoHits;
            resultHits += TileWaterQueryWork.ResultHits;
            ContactQueryCacheScope.SetCached(false);
            using var referenceWork = new SupportProposalAdmissionCapture();
            var expected = RunHistoryRead(scene.Simulator, uncached, command, history == "reconcile", tick);
            proposalBypasses += referenceWork.Bypasses;
            cleared += MovementWaterBufferWork.ClearedReferences;
            Assert.Equal(0, TileWaterQueryWork.MemoHits);
            Assert.Equal(0, TileWaterQueryWork.ResultHits);
            Assert.Equal(0, referenceWork.Hits);
            Assert.Equal(expected.Outcome, actual.Outcome);
            AssertExact(expected.State, actual.State);
            cached = actual.State;
            uncached = expected.State;
        }
        output.WriteLine($"{history}: proposal-hits={proposalHits}, bypasses={proposalBypasses}, " +
            $"small-hits={smallHits}, result-hits={resultHits}, uncached-cleared={cleared}");
        Assert.True(proposalHits > 0, "Optimized history must execute proposal-cache hits.");
        Assert.True(proposalBypasses > 0, "Uncached history must execute the original admission predicate.");
        if (history == "flat-water") Assert.True(resultHits > 0, "Flat water must replay the result tier.");
        if (history is "shore" or "motion" or "reconcile" or "rebase")
            Assert.True(smallHits + resultHits > 0, "Shore history must execute water-cache hits.");
        if (history is not "dry" and not "collider") Assert.True(cleared > 0);
    }

    static void AssertHistoryKernelExact(PlayerMoveSimulator simulator, PlayerMoveReadScope read,
        PlayerMoveState basis, MoveCommand command)
    {
        AssertKernelExact(simulator, read, basis, command, WaterPrismScenario.Dt);
        if (TileMediumSampler.BypassTraceMemoForTests) return;
        var queries = read.Queries!;
        var framed = new FramedMovementState(OrdinaryPlayerMovement.ToPhysics(basis.Move, queries.Frame), queries.Frame, null);
        var actual = simulator.Binding.StepPlayer(framed, command, WaterPrismScenario.Dt, queries, read.Boundary);
        TileMediumSampler.BypassTraceMemoForTests = SupportProposalAdmission.BypassForTests = true;
        try
        {
            var expected = simulator.Binding.StepPlayer(framed, command, WaterPrismScenario.Dt, queries, read.Boundary);
            KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.AssertEqual(expected, actual,
                KhaozEngine.Tests.Locomotion.Contacts.LocomotionStateBits.Policy.IncludeContinuityHandle);
        }
        finally { TileMediumSampler.BypassTraceMemoForTests = SupportProposalAdmission.BypassForTests = false; }
    }

    static (PlayerMoveState State, MovementStepOutcome? Outcome) RunHistoryRead(PlayerMoveSimulator simulator,
        PlayerMoveState basis, MoveCommand command, bool reconcile, int tick)
    {
        if (reconcile)
        {
            var prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator,
                PredictionSettings.Default with { TickSeconds = WaterPrismScenario.Dt });
            var wrong = basis;
            wrong.Position += new Vector3(0.125f, 0, 0);
            prediction.Reset(wrong);
            int first;
            using (var read = simulator.BeginExplicitRead(wrong))
            {
                Assert.True(read!.BasisValid, $"Cold placement {read.Outcome}, tick {tick}, position {basis.Position}, origin {read.Queries?.Frame.PhysicsOrigin}.");
                first = prediction.Predict(command);
                prediction.Predict(MoveCommand.Idle);
                prediction.Predict(command);
            }
            basis.Move.Commitment = new(1, Vector2.UnitX, 3, 4, 25, 0.15f, 0, 3);
            using (var read = simulator.BeginExplicitRead(basis))
            {
                Assert.True(read!.BasisValid, $"Cold placement {read.Outcome}, tick {tick}, position {basis.Position}, origin {read.Queries?.Frame.PhysicsOrigin}.");
                prediction.Reconcile(tick + 1, basis, first);
                Assert.Equal(2, prediction.PendingCommandCount);
                // Compare full selection/commitment/publication outputs within this exact live lease.
                AssertHistoryKernelExact(simulator, read, basis, command);
                return (prediction.PredictedState, simulator.LastExplicitOutcome);
            }
        }
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid, $"Cold placement {read.Outcome}, tick {tick}, position {basis.Position}, origin {read.Queries?.Frame.PhysicsOrigin}.");
            var actual = simulator.Step(basis, command, WaterPrismScenario.Dt);
            var outcome = simulator.LastExplicitOutcome;
            var expected = ReferenceStep(simulator, read, basis, command, WaterPrismScenario.Dt);
            Assert.Equal(expected.Outcome, outcome);
            AssertExact(expected.State, actual);
            AssertHistoryKernelExact(simulator, read, basis, command);
            return (actual, outcome);
        }
    }
}
