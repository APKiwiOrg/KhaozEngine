using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ClientReconcileTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Reconcile_converges_local_to_authoritative_basis()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);

        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });
        var forward = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 3; i++) pred.Predict(forward);          // client predicts forward
        int lastSeq = 2;

        // Inject a misprediction: the server says the player is somewhere else (all commands acked).
        var serverBasis = new PlayerMoveState { Position = new Vector3(4f, 0f, -1f) };
        ReconciliationResult result = pred.Reconcile(authoritativeTick: 1, serverBasis, lastAcknowledgedSeq: lastSeq);

        Assert.True(result.PositionError > settings.CorrectionDeadZone, $"error {result.PositionError}");
        // All commands acked + no pending => predicted snaps to the authoritative basis.
        Assert.Equal(4f, pred.PredictedState.Position.X, 4);
        Assert.Equal(-1f, pred.PredictedState.Position.Z, 4);

        // The visible correction decays toward the authoritative position over time.
        for (int i = 0; i < 240; i++) pred.AdvancePresentation(1f / 60f);
        Assert.Equal(4f, pred.RenderedState.Position.X, 2);
        Assert.Equal(-1f, pred.RenderedState.Position.Z, 2);
    }

    [Fact]
    public void Unacked_commands_replay_on_top_of_basis()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);

        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });
        var forward = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 3; i++) pred.Predict(forward);

        // Server has acked nothing; basis at origin -> the 3 unacked commands replay on top.
        var basis = new PlayerMoveState { Position = Vector3.Zero };
        pred.Reconcile(authoritativeTick: 1, basis, lastAcknowledgedSeq: -1);

        float expectedZ = -MoveTuning.Default.WalkSpeed * settings.TickSeconds * 3f;
        Assert.Equal(expectedZ, pred.PredictedState.Position.Z, 4);
    }
}
