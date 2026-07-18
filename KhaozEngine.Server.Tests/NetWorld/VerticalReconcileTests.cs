using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Client prediction / reconciliation of the vertical axis: the same step on server and client (parity), an
/// injected vertical misprediction converging, and an end-to-end loopback jump tracking the authoritative server.
/// </summary>
public class VerticalReconcileTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static MoveCommand Forward(bool jump = false) => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: jump);
    static MoveCommand Jump => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);

    [Fact]
    public void Server_step_and_client_prediction_produce_identical_vertical_state()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });
        var server = new PlayerMoveState { Position = Vector3.Zero };

        // A mixed stream: settle, jump, coast, walk-and-jump, fall.
        MoveCommand[] stream =
        {
            MoveCommand.Idle, Jump, Forward(), Forward(), Forward(), Forward(jump: true),
            Forward(), MoveCommand.Idle, MoveCommand.Idle, Jump, MoveCommand.Idle, Forward(),
        };
        foreach (MoveCommand cmd in stream)
        {
            pred.Predict(cmd);
            server = sim.Step(server, cmd, settings.TickSeconds);
            Assert.Equal(server.Position.Y, pred.PredictedState.Position.Y, 5);
            Assert.Equal(server.VerticalVelocity, pred.PredictedState.VerticalVelocity, 5);
            Assert.Equal(server.Grounded, pred.PredictedState.Grounded);
        }
    }

    [Fact]
    public void Injected_vertical_misprediction_reconciles_and_converges()
    {
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(sim, settings);
        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });

        int seq = -1;
        for (int i = 0; i < 4; i++) seq = pred.Predict(Forward(jump: i == 0));   // predicts a jump + walk

        // Server says the player is airborne with a different vertical velocity/height (all commands acked).
        var basis = new PlayerMoveState
        {
            Move = new MoveState { Position = new Vector3(0f, 4f, -0.3f), VerticalVelocity = 2.5f, Grounded = false },
        };
        pred.Reconcile(authoritativeTick: 1, basis, lastAcknowledgedSeq: seq);
        Assert.Equal(4f, pred.PredictedState.Position.Y, 4);                  // corrected to the basis
        Assert.Equal(2.5f, pred.PredictedState.VerticalVelocity, 4);

        // Converged: from the shared basis, both sides step the same stream with everything acked -> no desync,
        // no snap (predicted equals the authority every tick).
        var server = basis;
        for (int i = 0; i < 60; i++)
        {
            int s = pred.Predict(Forward());
            server = sim.Step(server, Forward(), settings.TickSeconds);
            pred.Reconcile(authoritativeTick: i + 2, server, lastAcknowledgedSeq: s);
            Assert.Equal(server.Position.Y, pred.PredictedState.Position.Y, 4);
            Assert.Equal(server.VerticalVelocity, pred.PredictedState.VerticalVelocity, 4);
            Assert.Equal(server.Grounded, pred.PredictedState.Grounded);
        }
    }

    [Fact]
    public void End_to_end_loopback_jump_rises_and_lands_converged()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined && client.LocalNetId > 0);

        float groundedY = LocalY(client);
        float maxY = groundedY;
        for (int i = 0; i < 40; i++)
        {
            client.SendInput(i == 0 ? Jump : MoveCommand.Idle);
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            client.AdvancePresentation(config.TickSeconds); // drive the render smoothing (the vertical axis now eases through it)
            maxY = MathF.Max(maxY, LocalY(client));
        }
        Assert.True(maxY > groundedY + 0.5f, $"the predicted player never left the ground (peak {maxY} vs {groundedY})");

        // Settle and confirm both land at the same height (converged, no permanent vertical offset).
        for (int i = 0; i < 60; i++)
        {
            client.SendInput(MoveCommand.Idle);
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            client.AdvancePresentation(config.TickSeconds);
        }
        (float landedY, bool grounded) = ServerState(server, client.LocalNetId);
        Assert.True(grounded, "server player should be grounded after the arc");
        Assert.Equal(landedY, LocalY(client), 2);
    }

    [Fact]
    public void Lagging_reconcile_preserves_the_predicted_jump_arc()
    {
        // The decisive WorldClient test: with unacked commands in flight, the authoritative basis MUST carry the
        // vertical velocity, or the replay collapses the predicted jump. The client predicts a jump arc 6 ticks
        // ahead while the server lags, then reconciles - a correct vertical basis reproduces the prediction exactly.
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined && client.LocalNetId > 0);
        float groundedY = LocalY(client);

        // Predict a jump + 5 idle ticks ahead of the server (commands queue up, server has not acked them yet).
        client.SendInput(Jump);
        for (int i = 0; i < 5; i++) client.SendInput(MoveCommand.Idle);
        float predictedApex = LocalY(client);
        Assert.True(predictedApex > groundedY + 0.3f, $"client should predict an airborne arc, got {predictedApex}");

        // The server now processes only the first two commands, then the client reconciles against that lagging
        // basis with four commands still pending (replayed on top).
        server.Poll();
        server.Tick(config.TickSeconds);
        server.Tick(config.TickSeconds);
        client.Poll();

        float reconciled = LocalY(client);
        Assert.Equal(predictedApex, reconciled, 2);   // a vVel-less basis would collapse this back toward the ground
    }

    static float LocalY(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.Y;
        throw new XunitException("no local entity in client snapshot");
    }

    static (float y, bool grounded) ServerState(WorldServer server, long netId)
    {
        float y = float.NaN;
        bool grounded = false;
        server.World.ForEach<NetId, ReplicatedPosition, MovementState>(
            (Entity e, ref NetId id, ref ReplicatedPosition p, ref MovementState ms) =>
            {
                if (id.Value == netId) { y = p.Value.Y; grounded = ms.Grounded; }
            });
        return (y, grounded);
    }
}
