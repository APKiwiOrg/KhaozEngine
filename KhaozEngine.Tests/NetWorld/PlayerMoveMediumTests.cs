using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The movement-medium seam at the netcode layer: the medium provider threads through <see cref="PlayerMoveSimulator"/>
/// (used by both the authoritative server tick and the client's prediction replay). Because the GAME supplies the SAME
/// pure delegate on both heads, wading predicts in lockstep. A null provider is bit-identical to the pre-medium
/// simulator, so every existing movement test stays green untouched.
/// </summary>
public class PlayerMoveMediumTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };

    static Func<float, float, float, MovementMedium> Water(float surfaceY, float zoneScale = 1f)
        => (x, z, feetY) => new MovementMedium(surfaceY, inWater: true, zoneScale);

    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    [Fact]
    public void Null_medium_simulator_is_bit_identical_to_the_pre_medium_simulator()
    {
        var withoutMedium = new PlayerMoveSimulator(Flat, Unit);
        var withNull = new PlayerMoveSimulator(Flat, Unit, medium: null);
        var s0 = new PlayerMoveState { Position = new Vector3(0f, 0.5f, 0f), Grounded = true };
        var a = withoutMedium.Step(s0, Forward, 1f / 30f);
        var b = withNull.Step(s0, Forward, 1f / 30f);
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VerticalVelocity, b.VerticalVelocity);
    }

    [Fact]
    public void Deep_water_slows_the_simulator_step_to_the_floor_scale()
    {
        var dry = new PlayerMoveSimulator(Flat, Unit);
        var wet = new PlayerMoveSimulator(Flat, Unit, medium: Water(1.0f));   // >= chest depth -> floor 0.45
        var s0 = new PlayerMoveState { Position = new Vector3(0f, 0.5f, 0f), Grounded = true };
        var d = dry.Step(s0, Forward, 1f);
        var w = wet.Step(s0, Forward, 1f);
        float dryDist = MathF.Abs(d.Position.Z - s0.Position.Z);
        float wetDist = MathF.Abs(w.Position.Z - s0.Position.Z);
        Assert.Equal(dryDist * Unit.WadeMinSpeedScale, wetDist, 5);
    }

    [Fact]
    public void Same_provider_same_inputs_produce_the_same_output_on_two_sims_both_heads()
    {
        // The "both heads" determinism pin: two independent simulators (stand in for server + client-prediction) with
        // the SAME pure provider step the SAME command from the SAME state to a bit-identical result.
        Func<float, float, float, MovementMedium> zoned = Water(0.5f, zoneScale: 0.8f);
        var server = new PlayerMoveSimulator(Flat, Unit, medium: zoned);
        var client = new PlayerMoveSimulator(Flat, Unit, medium: zoned);
        var s0 = new PlayerMoveState { Position = new Vector3(2f, 0.5f, -3f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(1f, 1f), run: true, cameraYaw: 0.6f);
        var a = s0; var b = s0;
        for (int i = 0; i < 20; i++) { a = server.Step(a, cmd, 1f / 60f); b = client.Step(b, cmd, 1f / 60f); }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VerticalVelocity, b.VerticalVelocity);
    }

    // ---- Prediction alignment through the real loopback server/client harness ----

    static (WorldServer server, WorldClient client, WorldServerConfig config) Connect(
        Func<float, float, float, MovementMedium>? medium)
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        // Both heads get the SAME provider (the game's contract). Unit tuning so wading maths reads cleanly.
        var server = new WorldServer(st, config, Flat, Unit, medium: medium);
        var client = new WorldClient(ct, Flat, Unit,
            new WorldClientConfig { TickSeconds = config.TickSeconds }, medium: medium);
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config);
    }

    [Fact]
    public void Client_prediction_stays_aligned_with_the_server_while_wading()
    {
        // Deep water everywhere: the client predicts the slowed wade locally, the server is authoritative over the
        // same slowed wade. With the SAME provider on both heads the predicted local position tracks the server's
        // authoritative position with no rubber-band correction.
        Func<float, float, float, MovementMedium> deep = Water(1.0f);   // chest-deep -> floor scale
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect(deep);

        for (int i = 0; i < 40; i++)
        {
            client.SendInput(Forward);
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
        }

        Assert.True(server.TryGetPlayerNetId(0, out _));
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState authoritative));
        PlayerMoveState predicted = client.LocalRenderState;

        // Aligned: the predicted planar position matches the authoritative one within a small tolerance (a fraction of
        // one tick's slowed travel), i.e. prediction did not diverge from the server's wade.
        float dx = predicted.Position.X - authoritative.Position.X;
        float dz = predicted.Position.Z - authoritative.Position.Z;
        float err = MathF.Sqrt(dx * dx + dz * dz);
        Assert.True(err < 0.05f, $"predicted {predicted.Position} vs authoritative {authoritative.Position}, err {err}");

        // And it actually moved forward (into -Z), so we tested a live wade, not a stalled avatar.
        Assert.True(authoritative.Position.Z < -0.5f, $"expected forward wade progress, got {authoritative.Position.Z}");
    }

    [Fact]
    public void Wading_client_travels_slower_than_a_dry_client_over_the_same_inputs()
    {
        // End-to-end behaviour pin: the exact same input stream over the loopback reaches a nearer point when the
        // world reports deep water than on dry land, and the ratio is the wade floor scale.
        (WorldServer dryServer, WorldClient dryClient, WorldServerConfig cfg) = Connect(medium: null);
        (WorldServer wetServer, WorldClient wetClient, _) = Connect(Water(1.0f));

        for (int i = 0; i < 60; i++)
        {
            dryClient.SendInput(Forward); wetClient.SendInput(Forward);
            dryServer.Poll(); dryServer.Tick(cfg.TickSeconds); dryClient.Poll();
            wetServer.Poll(); wetServer.Tick(cfg.TickSeconds); wetClient.Poll();
        }

        Assert.True(dryServer.TryGetPlayerState(0, out PlayerMoveState dry));
        Assert.True(wetServer.TryGetPlayerState(0, out PlayerMoveState wet));
        float dryDist = MathF.Abs(dry.Position.Z);
        float wetDist = MathF.Abs(wet.Position.Z);
        Assert.True(wetDist < dryDist, $"wading ({wetDist}) should trail dry ({dryDist})");
        // Both travelled at a steady speed for the whole run, so the distance ratio is the floor scale.
        Assert.Equal(dryDist * Unit.WadeMinSpeedScale, wetDist, 2);
    }
}
