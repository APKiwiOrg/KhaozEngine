using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

// The local entity's FacingYaw reads the predicted heading eased between ticks when InterpolateYaw is set.
public class WorldClientLocalYawTests
{
    private const float Tick = 1f / 30f;
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;

    private static (WorldServer server, WorldClient client) Connect(bool interpolateYaw)
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = Tick, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig
        {
            TickSeconds = Tick,
            Prediction = PredictionSettings.Default with { TickSeconds = Tick, InterpolateYaw = interpolateYaw },
        });
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(Tick); client.Poll(); }
        Assert.True(client.Joined);
        for (int i = 0; i < 6; i++)
        {
            client.SendInput(MoveCommand.Idle);
            server.Poll(); server.Tick(Tick); client.Poll();
        }
        return (server, client);
    }

    private static float LocalFacing(WorldClient client) => client.Snapshot().Single(e => e.IsLocal).FacingYaw;

    [Fact]
    public void PlayerMoveStateCarriesYawThroughRenderState()
    {
        var move = new MoveState { Position = new Vector3(1f, 2f, 3f), VerticalVelocity = -4f, FacingYaw = 0.4f };
        IPredictedState<PlayerMoveState> state =
            new PlayerMoveState { Move = move, TeleportEpoch = 7, FrameAnchor = new Vector2(64f, -32f) };

        Assert.True(state.HasYaw);
        Assert.Equal(0.4f, state.Yaw);

        PlayerMoveState rendered = state.WithRenderState(new Vector2(5f, 6f), 7f, -1.2f);
        PlayerMoveState positional = state.WithRenderState(new Vector2(5f, 6f), 7f);
        Assert.Equal(new Vector3(5f, 7f, 6f), rendered.Move.Position);
        Assert.Equal(-1.2f, rendered.Move.FacingYaw);
        Assert.Equal(0.4f, positional.Move.FacingYaw);
        PlayerMoveState withoutYaw = rendered;
        withoutYaw.Move.FacingYaw = positional.Move.FacingYaw;
        Assert.Equal(positional, withoutYaw);
    }

    [Fact]
    public void LocalEntityFacingYawIsInterpolated()
    {
        (_, WorldClient client) = Connect(interpolateYaw: true);
        Assert.Equal(0f, LocalFacing(client));

        client.SendInput(new MoveCommand(Vector2.Zero, false, 0.6f, faceCamera: true));
        client.AdvancePresentation(1f / 60f);

        Assert.Equal(0.3f, LocalFacing(client), 1e-5f);
    }

    [Fact]
    public void LocalEntityFacingYawSnapsWhenNotInterpolated()
    {
        (_, WorldClient client) = Connect(interpolateYaw: false);
        Assert.Equal(0f, LocalFacing(client));

        client.SendInput(new MoveCommand(Vector2.Zero, false, 0.6f, faceCamera: true));
        client.AdvancePresentation(1f / 60f);

        Assert.Equal(BitConverter.SingleToInt32Bits(0.6f), BitConverter.SingleToInt32Bits(LocalFacing(client)));
    }
}
