using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

// Part B: the read-only local-movement accessors on WorldClient surface the predicted RenderedState so a consumer
// can fill CharacterSample.HasMovement for the local avatar instead of finite-differencing its own position.
public class WorldClientLocalMovementTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static (WorldServer server, WorldClient client, WorldServerConfig config) Connect()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        Assert.True(client.LocalNetId > 0);
        return (server, client, config);
    }

    [Fact]
    public void Local_accessors_report_grounded_at_rest_on_flat_ground()
    {
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect();

        // Settle with no input: the local avatar rests on the flat ground.
        for (int i = 0; i < 6; i++)
        {
            client.SendInput(MoveCommand.Idle);
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
        }

        Assert.True(client.LocalGrounded, "local player should read grounded at rest on flat ground");
        Assert.True(MathF.Abs(client.LocalVerticalVelocity) < 1e-3f,
            $"vertical velocity should be ~0 at rest, got {client.LocalVerticalVelocity}");

        // The composite accessor exposes the same state and agrees with the rendered snapshot position.
        PlayerMoveState rs = client.LocalRenderState;
        Assert.True(rs.Grounded);
    }

    [Fact]
    public void Jump_command_makes_local_accessors_report_airborne_and_rising()
    {
        (WorldServer server, WorldClient client, WorldServerConfig config) = Connect();

        // Establish a grounded basis first.
        for (int i = 0; i < 6; i++)
        {
            client.SendInput(MoveCommand.Idle);
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
        }
        Assert.True(client.LocalGrounded);

        // A jump command predicts an upward launch immediately on the predicted (rendered) state.
        client.SendInput(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true));

        Assert.False(client.LocalGrounded, "a jump should leave the ground");
        Assert.True(client.LocalVerticalVelocity > 0f,
            $"a jump should give a positive vertical velocity, got {client.LocalVerticalVelocity}");
    }
}
