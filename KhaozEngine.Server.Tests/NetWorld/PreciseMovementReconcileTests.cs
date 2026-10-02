using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class PreciseMovementReconcileTests
{
    const float TickSeconds = 1f / 30f;
    static readonly Func<float, float, float> Flat = (_, _) => 0f;
    static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        CapsuleHalfHeight = 0.5f,
        WadeMinSpeedScale = 0.5f,
        SwimEnterDepthFraction = 10f,
        SwimExitDepthFraction = 9f,
        StrafeSpeedScale = 0.5f,
        BackpedalSpeedScale = 0.25f,
        BackpedalAllowsRun = false,
    };

    static MoveCommand Precise(Vector2 axis, bool run = false, bool faceCamera = false) =>
        new(axis, run, 0f, jump: false, faceCamera, scaleSpeedByAxis: true);

    [Theory]
    [InlineData(false, 1f, 0.5f, -0.2251f)]
    [InlineData(false, 1.5f, 0.75f, -0.33765f)]
    [InlineData(true, 1f, 0.2f, -0.09004f)]
    [InlineData(true, 1.5f, 0.3f, -0.13506f)]
    public void WireDecodedPreciseInputMatchesPrediction(bool wading, float speedScale, float expectedX, float expectedZ)
    {
        using var fixture = new Fixture(wading);
        fixture.Server.SetSpeedScale(PlayerRef.Slot(0), speedScale);
        fixture.Ingest(fixture.Serve());
        Assert.Equal(speedScale, fixture.Client.LocalRenderState.Move.SpeedScale);

        MoveCommand[] commands =
        [
            new(new Vector2(0f, 0.25f), false, 0f),
            Precise(new Vector2(0f, 0.25f)),
            MoveCommand.Idle,
            Precise(new Vector2(0.5f, 0f), run: true, faceCamera: true),
            Precise(new Vector2(0f, -0.5f), run: true, faceCamera: true),
            new(new Vector2(0.25f, 0f), true, 0f),
            Precise(new Vector2(0f, 0.0005f)),
        ];

        foreach (MoveCommand command in commands)
        {
            int sequence = fixture.Submit(command);
            PlayerMoveState authoritativeAtAcknowledgedTick = fixture.Serve();
            PlayerMoveState predictedAtAcknowledgedTick = fixture.Prediction.PredictedState;

            // Capture before ingest so reconciliation cannot hide a lost command flag.
            Assert.Equal(authoritativeAtAcknowledgedTick.Position, predictedAtAcknowledgedTick.Position);
            Assert.Equal(predictedAtAcknowledgedTick.Position, fixture.Client.LocalRenderState.Position);
            fixture.Ingest(authoritativeAtAcknowledgedTick);
            Assert.Equal(sequence, fixture.Wire.LastAcknowledgedSequence);
            Assert.Equal(0, fixture.Prediction.PendingCommandCount);
            Assert.False(authoritativeAtAcknowledgedTick.Swimming);
        }

        // At 30 Hz the dry stream travels +0.5 X and -0.2251 Z. Wading contributes 0.5 * 0.8.
        Assert.Equal(expectedX, fixture.Authority.Position.X, 6);
        Assert.Equal(expectedZ, fixture.Authority.Position.Z, 6);
        Assert.Equal(fixture.Authority.Position, fixture.Client.LocalRenderState.Position);
    }

    [Fact]
    public void UnackedPreciseCommandsReplayTheirFraction()
    {
        using var fixture = new Fixture();
        int acknowledgedSequence = fixture.Submit(new MoveCommand(Vector2.UnitY, false, 0f));
        fixture.Server.SetSpeedScale(PlayerRef.Slot(0), 0.5f);
        PlayerMoveState authoritativeAtAcknowledgedTick = fixture.Serve();

        fixture.Submit(Precise(new Vector2(0f, 0.25f)));
        int lastSequence = fixture.Submit(Precise(new Vector2(0f, 0.5f)));
        fixture.Client.AdvancePresentation(TickSeconds / 2f);
        Assert.Equal(-0.35f, fixture.Client.LocalRenderState.Position.Z, 6);
        Assert.Equal(3, fixture.Prediction.PendingCommandCount);
        Assert.Equal(-1, fixture.Wire.LastAcknowledgedSequence);

        ReconciliationResult correction = fixture.Ingest(authoritativeAtAcknowledgedTick);
        Assert.Equal(acknowledgedSequence, fixture.Wire.LastAcknowledgedSequence);
        Assert.Equal(2, fixture.Prediction.PendingCommandCount);
        Assert.Equal(0.175f, correction.PositionError, 6);
        Assert.Equal(0.175f, fixture.Client.NetStats.LastCorrectionMeters, 6);

        // Basis -0.1 plus two replayed steps: -(6 * 0.5 * (0.25 + 0.5) / 30) = -0.075.
        const float ExpectedPreciseTravel = -0.175f;
        PlayerMoveState replayed = fixture.Prediction.PredictedState;
        Assert.Equal(ExpectedPreciseTravel, replayed.Position.Z, 6);
        Assert.Equal(replayed.Position, fixture.Client.LocalRenderState.Position);
        Assert.Equal(0.5f, replayed.Move.SpeedScale);

        // Drain one real queued command per tick before comparing the same acknowledged command.
        fixture.Ingest(fixture.Serve());
        Assert.Equal(acknowledgedSequence + 1, fixture.Wire.LastAcknowledgedSequence);
        Assert.Equal(1, fixture.Prediction.PendingCommandCount);
        Assert.Equal(ExpectedPreciseTravel, fixture.Client.LocalRenderState.Position.Z, 6);
        authoritativeAtAcknowledgedTick = fixture.Serve();
        PlayerMoveState predictedAtAcknowledgedTick = fixture.Prediction.PredictedState;
        Assert.Equal(authoritativeAtAcknowledgedTick.Position, predictedAtAcknowledgedTick.Position);
        fixture.Ingest(authoritativeAtAcknowledgedTick);
        Assert.Equal(lastSequence, fixture.Wire.LastAcknowledgedSequence);
        Assert.Equal(0, fixture.Prediction.PendingCommandCount);
        Assert.Equal(authoritativeAtAcknowledgedTick.Position, fixture.Client.LocalRenderState.Position);
    }

    [Fact]
    public void IdleAfterPreciseInputDoesNotKeepMoving()
    {
        using var fixture = new Fixture();
        fixture.Submit(Precise(new Vector2(0f, 0.25f)));
        fixture.Ingest(fixture.Serve());
        Vector3 stoppedPosition = fixture.Authority.Position;
        Assert.Equal(-0.05f, stoppedPosition.Z, 6);

        int idleSequence = fixture.Submit(MoveCommand.Idle);
        PlayerMoveState afterIdle = fixture.Serve();
        Assert.Equal(stoppedPosition, afterIdle.Position);
        Assert.Equal(stoppedPosition, fixture.Prediction.PredictedState.Position);
        Assert.Equal(stoppedPosition, fixture.Client.LocalRenderState.Position);
        Assert.Equal(0f, fixture.Client.LocalHorizontalSpeed);
        fixture.Ingest(afterIdle);
        Assert.Equal(idleSequence, fixture.Wire.LastAcknowledgedSequence);
        Assert.Equal(0, fixture.Prediction.PendingCommandCount);

        // An empty queue also supplies neutral input rather than retaining the precise command.
        fixture.Ingest(fixture.Serve());
        Assert.Equal(idleSequence, fixture.Wire.LastAcknowledgedSequence);
        Assert.Equal(stoppedPosition, fixture.Authority.Position);
        Assert.Equal(stoppedPosition, fixture.Client.LocalRenderState.Position);
    }

    sealed class Fixture : IDisposable
    {
        readonly InMemoryTransportHub hub = new();
        int authoritativeTick;
        public WorldServer Server { get; }
        public WorldClient Client { get; }
        public ObservedTransport Wire { get; }
        public ClientPrediction<PlayerMoveState, MoveCommand> Prediction { get; }
        public PlayerMoveState Authority
        {
            get
            {
                Assert.True(Server.TryGetPlayerState(0, out PlayerMoveState state));
                return state;
            }
        }

        public Fixture(bool wading = false)
        {
            Func<float, float, float, MovementMedium>? medium = wading
                ? (_, _, _) => new MovementMedium(1f, inWater: true, wadeSpeedScale: 0.8f)
                : null;
            var settings = PredictionSettings.Default with { TickSeconds = TickSeconds, CorrectionRate = 0f };
            Server = new WorldServer(hub.Server, new WorldServerConfig
            {
                TickSeconds = TickSeconds, InterestRadius = 500f, MaxPlayers = 1,
            }, Flat, Tuning, medium: medium);
            Wire = new ObservedTransport(hub.CreateClient());
            Client = new WorldClient(Wire, Flat, Tuning,
                new WorldClientConfig { TickSeconds = TickSeconds, Prediction = settings }, medium: medium);
            Prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(
                new PlayerMoveSimulator(Flat, Tuning, medium: medium), settings);
            for (int i = 0; i < 6; i++)
            {
                Server.Poll();
                Server.Tick(TickSeconds);
                Client.Poll();
            }
            Assert.True(Client.Joined);
            Assert.Equal(Client.LocalNetId, Wire.LocalNetId);
            Assert.Equal(-1, Wire.LastAcknowledgedSequence);
            Prediction.Reset(Authority);
            Client.AdvancePresentation(TickSeconds);
        }

        public int Submit(in MoveCommand command)
        {
            int sequence = Client.SendInput(command);
            Assert.Equal(Prediction.Predict(command), sequence);
            Client.AdvancePresentation(TickSeconds / 2f);
            return sequence;
        }

        public PlayerMoveState Serve()
        {
            Server.Poll();
            Server.Tick(TickSeconds);
            Client.AdvancePresentation(TickSeconds / 2f);
            return Authority;
        }

        public ReconciliationResult Ingest(in PlayerMoveState basis)
        {
            Client.Poll();
            ReconciliationResult result = Prediction.Reconcile(++authoritativeTick, basis, Wire.LastAcknowledgedSequence);
            // Finish interpolation and disable correction smoothing to inspect the simulation endpoint.
            Client.AdvancePresentation(TickSeconds);
            return result;
        }

        public void Dispose()
        {
            Client.Dispose();
            hub.Dispose();
        }
    }

    sealed class ObservedTransport(INetTransport inner) : INetTransport
    {
        public int LastAcknowledgedSequence { get; private set; } = -1;
        public long LocalNetId { get; private set; } = -1;

        public bool TryDequeueEvent(out NetEvent value)
        {
            if (!inner.TryDequeueEvent(out value)) return false;
            if (value.Type == NetEventType.Data && SessionFrame.ReadOpcode(value.Data) == SessionOpcode.Data
                && MoveProtocol.TryDecodeServerFrame(SessionFrame.ReadBody(value.Data), out var kind, out byte[] body)
                && kind is MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta
                && MoveProtocol.TryDecodeSnapshotFrame(body, out long localNetId, out int sequence, out _))
            {
                LocalNetId = localNetId;
                LastAcknowledgedSequence = sequence;
            }
            return true;
        }

        public void Poll() => inner.Poll();
        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability) =>
            inner.Send(target, payload, reliability);
        public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
        public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => inner.Disconnect(connection, reason);
        public void Dispose() => inner.Dispose();
    }
}
