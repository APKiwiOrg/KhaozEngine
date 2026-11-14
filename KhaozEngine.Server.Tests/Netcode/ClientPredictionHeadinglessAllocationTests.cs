using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

/// <summary>
/// A state that keeps the default heading members must not pay for them. Reading a default interface member on a
/// struct through the generic constraint boxes it, so with <c>InterpolateYaw</c> off neither <c>Predict</c>, a steady
/// <c>Reconcile</c> nor <c>RenderedState</c> may read <c>Yaw</c>. Joins <c>AllocSensitive</c> because it reads
/// <see cref="System.GC.GetAllocatedBytesForCurrentThread"/>.
/// </summary>
[Collection("AllocSensitive")]
public class ClientPredictionHeadinglessAllocationTests
{
    private const float Tick = 1f / 30f;

    // Implements every member Predict, AdvancePresentation, a non-snap Reconcile and RenderedState read, and none of
    // the heading members.
    private readonly record struct PlanarState(Vector2 Position) : IPredictedState<PlanarState>
    {
        Vector2 IPredictedState<PlanarState>.PredictionTarget => Position;
        float IPredictedState<PlanarState>.Vertical => 0f;
        float IPredictedState<PlanarState>.StepDeltaY => 0f;
        Vector2 IPredictedState<PlanarState>.FrameAnchor => Vector2.Zero;
        uint IPredictedState<PlanarState>.TeleportEpoch => 0;
        public PlanarState WithPosition(Vector2 position) => this with { Position = position };
        PlanarState IPredictedState<PlanarState>.WithRenderState(Vector2 position, float vertical)
            => this with { Position = position };
    }

    private readonly record struct PlanarCmd(Vector2 Velocity);

    private sealed class PlanarSimulator : ITickSimulator<PlanarState, PlanarCmd>
    {
        public PlanarState Step(in PlanarState state, in PlanarCmd command, float dt)
            => state with { Position = state.Position + command.Velocity * dt };
    }

    [Fact]
    public void PredictReconcileAndRenderAllocateNothingWithoutHeadingInterpolation()
        => AssertSteadyPredictionAllocatesNothing(new PlanarSimulator(), new PlanarState(Vector2.Zero),
            new PlanarCmd(new Vector2(1f, 0f)));

    // #1279: the shipped player state must implement every default member read on a prediction tick.
    [Fact]
    public void PlayerMovePredictionReconcileAndRenderAllocateNothing()
    {
        using var simulator = new PlayerMoveSimulator((x, z) => 0f, MoveTuning.Default);
        AssertSteadyPredictionAllocatesNothing(simulator, new PlayerMoveState(),
            new MoveCommand(new Vector2(1f, 0f), false, 0f));
    }

    // #1279: exercise the consumer's local render accessor after real input has seeded a moving prediction.
    [Fact]
    public void WorldClientLocalRenderStateAllocatesNothing()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        using var serverTransport = st;
        using var clientTransport = ct;
        using var server = new WorldServer(st, new WorldServerConfig { TickSeconds = Tick }, (x, z) => 0f,
            MoveTuning.Default);
        using var client = new WorldClient(ct, (x, z) => 0f, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Tick });
        for (int i = 0; i < 6; i++)
        {
            server.Poll();
            server.Tick(Tick);
            client.Poll();
        }
        Assert.True(client.Joined);

        Vector3 start = client.LocalRenderState.Position;
        Assert.True(client.SendInput(new MoveCommand(new Vector2(1f, 0f), false, 0f)) >= 0);
        Assert.NotEqual(start, client.LocalPredictedState.Position);
        PlayerMoveState rendered = default;
        void Render()
        {
            for (int i = 0; i < 100; i++)
            {
                client.AdvancePresentation(Tick / 1000f);
                rendered = client.LocalRenderState;
            }
        }

        Render();
        AllocAssert.NoPerCallAllocation("WorldClient AdvancePresentation and LocalRenderState", Render);
        Assert.NotEqual(start, rendered.Position);
    }

    // #1279: tile prediction also reads default step/frame members, and HasYaw when interpolation is enabled.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TilePredictionReconcileAndRenderAllocateNothing(bool interpolateYaw)
    {
        var simulator = new TileMoveSimulator(new TileCollisionMap(1), TileStepTicks.Default);
        // Idle needs no authored map or path search and still exercises every steady prediction member.
        AssertSteadyPredictionAllocatesNothing(simulator, TileMoveState.At(new TileCoord(5, 5, 0), TileDirection.N),
            TileCommand.Continue(TileMoveMode.Walk), interpolateYaw);
    }

    private static void AssertSteadyPredictionAllocatesNothing<TState, TCommand>(
        ITickSimulator<TState, TCommand> simulator, TState initial, TCommand command, bool interpolateYaw = false)
        where TState : struct, IPredictedState<TState>
    {
        // A small pending bound so the warm-up fills the command list to its steady capacity.
        var settings = PredictionSettings.Default with
        {
            TickSeconds = Tick, MaxPendingCommands = 4, InterpolateYaw = interpolateYaw,
        };
        var p = new ClientPrediction<TState, TCommand>(simulator, settings);
        p.Reset(initial);

        int tick = 0;
        for (int i = 0; i < 16; i++)
        {
            p.Predict(command);
            p.AdvancePresentation(Tick / 2f);
            _ = p.RenderedState;
        }

        // A basis equal to the prediction that acknowledges every predicted command takes the steady non-snap branch.
        Assert.True(p.PendingCommandCount > 0, "the warm-up leaves commands for the first Reconcile to acknowledge");
        bool snapped = false;
        int maxPending = 0;
        float x = 0f;
        void Steady()
        {
            for (int i = 0; i < 100; i++)
            {
                int seq = p.Predict(command);
                p.AdvancePresentation(Tick / 2f);
                snapped |= p.Reconcile(++tick, p.PredictedState, seq).HardSnapApplied;
                maxPending = Math.Max(maxPending, p.PendingCommandCount);
                x = p.RenderedState.Position.X;
            }
        }

        // One unmeasured pass warms the first Reconcile, which creates the pending list's cached key view once.
        Steady();
        AllocAssert.NoPerCallAllocation($"{typeof(TState).Name} Predict, Reconcile, AdvancePresentation and RenderedState",
            Steady);

        // Every Reconcile was applied and acknowledged its command, so none was ignored as stale.
        Assert.False(snapped);
        Assert.Equal(0, maxPending);
        Assert.True(x > 0f);
    }
}
