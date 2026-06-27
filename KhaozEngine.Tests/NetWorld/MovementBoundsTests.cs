using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class MovementBoundsTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand East = new(new Vector2(1f, 0f), run: true, cameraYaw: 0f);

    [Fact]
    public void Simulator_clamps_player_inside_circle_bounds()
    {
        var bounds = new CircleBounds(new Vector2(0f, 0f), 5f);
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 200; i++) s = sim.Step(s, East, 1f / 30f);   // drive east forever
        Assert.True(bounds.Contains(s.Position.X, s.Position.Z));
        Assert.True(s.Position.X <= 5f + 1e-3f);
        Assert.Equal(5f, s.Position.X, 2);                                // pinned on the edge
    }

    [Fact]
    public void Simulator_slides_along_a_rect_edge_keeping_tangential_progress()
    {
        // wall at x=5; drive into +X and forward (Move.Y -> -Z) -> x pins at 5, z keeps advancing (slide).
        var bounds = new RectBounds(-100f, -100f, 5f, 100f);
        var sim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var diag = new MoveCommand(Vector2.Normalize(new Vector2(1f, 1f)), run: true, cameraYaw: 0f);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 120; i++) s = sim.Step(s, diag, 1f / 30f);
        Assert.Equal(5f, s.Position.X, 2);                                // clamped to the wall
        Assert.True(s.Position.Z < -5f, $"no tangential slide: z={s.Position.Z}");
    }

    [Fact]
    public void Slope_gate_blocks_a_step_onto_too_steep_ground()
    {
        // a near-vertical wall for x>2 (normal.Y ~ 0) -> stepping east past x=2 is blocked.
        Func<float, float, Vector3> normal = (x, z) => x > 2f ? new Vector3(1f, 0.05f, 0f) : new Vector3(0f, 1f, 0f);
        var sim = new PlayerMoveSimulator((x, z) => 0f, MoveTuning.Default, groundNormal: normal);
        var s = new PlayerMoveState { Position = Vector3.Zero };
        for (int i = 0; i < 200; i++) s = sim.Step(s, East, 1f / 30f);
        Assert.True(s.Position.X <= 2f + 1e-3f, $"climbed the steep wall to x={s.Position.X}");
    }

    [Fact]
    public void Bounded_prediction_reconciles_against_a_bounded_server_with_no_persistent_error()
    {
        var bounds = new CircleBounds(new Vector2(0f, 0f), 5f);
        var clientSim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var serverSim = new PlayerMoveSimulator(Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var settings = PredictionSettings.Default with { TickSeconds = 1f / 30f };
        var pred = new ClientPrediction<PlayerMoveState, MoveCommand>(clientSim, settings);

        pred.Reset(new PlayerMoveState { Position = Vector3.Zero });
        var serverState = new PlayerMoveState { Position = Vector3.Zero };
        int seq = 0;
        for (int i = 0; i < 200; i++)
        {
            pred.Predict(East);                                   // client predicts into the wall (bounded)
            serverState = serverSim.Step(serverState, East, settings.TickSeconds);   // server steps the same (bounded)
            ReconciliationResult r = pred.Reconcile(authoritativeTick: i, serverState, lastAcknowledgedSeq: seq++);
            // both clamp identically -> reconciliation error stays tiny (prediction not broken at the wall).
            Assert.True(r.PositionError < 0.5f, $"tick {i}: error {r.PositionError}");
        }
        Assert.Equal(serverState.Position.X, pred.PredictedState.Position.X, 2);
    }

    [Fact]
    public void WorldServer_holds_a_player_inside_bounds()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var bounds = new CircleBounds(new Vector2(0f, 0f), 6f);
        var cfg = new WorldServerConfig { TickSeconds = 1f / 30f, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var client = new NetClient(ct);
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        for (int i = 0; i < 300; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, East), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState s));
        Assert.True(bounds.Contains(s.Position.X, s.Position.Z), $"escaped to {s.Position}");
    }

    [Fact]
    public void ShardedWorldServer_holds_a_player_inside_bounds()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var bounds = new CircleBounds(new Vector2(0f, 0f), 8f);
        var cfg = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f, CellSize = 10f, OverlapMargin = 4f, InterestRadius = 4f,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default, groundNormal: null, bounds: bounds);
        var client = new NetClient(ct);
        for (int i = 0; i < 10; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }
        Assert.True(server.TryGetPlayerNetId(client.Slot, out _));
        for (int i = 0; i < 300; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, East), NetChannelReliability.ReliableOrdered);
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState s));
        Assert.True(bounds.Contains(s.Position.X, s.Position.Z), $"escaped to {s.Position}");
    }
}
