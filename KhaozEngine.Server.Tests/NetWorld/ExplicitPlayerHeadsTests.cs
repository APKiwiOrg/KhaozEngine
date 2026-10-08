using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class ExplicitPlayerHeadsTests
{
    [Fact]
    public void BothHeadsUseExplicitQueriesAndPublishBeforeTheirPinsRelease()
    {
        using var pair = new Pair();
        pair.Pump(6);
        Assert.True(pair.Client.Joined);
        int serverPublications = 0, clientPublications = 0;
        pair.ServerEnvironment.OnPinDispose = () =>
        {
            Assert.True(pair.Server.TryGetPlayerState(0, out var state));
            Assert.InRange(state.Position.X, 0.3999f, 0.4001f);
            Assert.Throws<InvalidOperationException>(() => pair.ServerEnvironment.Physics.Step(0.1f));
            serverPublications++;
        };
        pair.ClientEnvironment.OnPinDispose = () =>
        {
            Assert.InRange(pair.Client.LocalPredictedState.Position.X, 0.3999f, 0.4001f);
            Assert.Throws<InvalidOperationException>(() => pair.ClientEnvironment.Physics.Step(0.1f));
            clientPublications++;
        };
        Assert.True(pair.Client.SendInput(new(Vector2.UnitX, false, 0)) >= 0);
        pair.Pump(1);
        Assert.True(serverPublications > 0);
        Assert.True(clientPublications > 0);
        Assert.Equal(0, pair.Client.PendingPredictionCommands);
        Assert.Equal(0, pair.LegacyCalls);
        pair.ServerEnvironment.OnPinDispose = null;
        pair.ClientEnvironment.OnPinDispose = null;
        pair.ServerEnvironment.Physics.Step(0.1f);
        pair.ClientEnvironment.Physics.Step(0.1f);
    }

    [Fact]
    public void ServerRefusalAcknowledgesTheJumpAndItCannotLaunchAfterReadinessReturns()
    {
        using var pair = new Pair();
        pair.Pump(6);
        Assert.True(pair.Client.Joined);
        pair.ServerEnvironment.Availability = MovementAvailability.Unresolved;
        int sequence = pair.Client.SendInput(new(Vector2.Zero, false, 0, jump: true));
        Assert.True(sequence >= 0);
        pair.Pump(1);
        Assert.True(pair.Server.TryGetPlayerState(0, out var held));
        Assert.True(held.Grounded);
        Assert.Equal(0, held.VerticalVelocity);
        Assert.Equal(0, held.Move.JumpBufferRemaining);
        Assert.Equal(0, pair.Client.PendingPredictionCommands);
        pair.ServerEnvironment.Availability = MovementAvailability.Known;
        pair.Client.SendInput(MoveCommand.Idle);
        pair.Pump(1);
        Assert.True(pair.Server.TryGetPlayerState(0, out var next));
        Assert.True(next.Grounded);
        Assert.Equal(0, next.VerticalVelocity);
        Assert.Equal(0, pair.Client.LocalPredictedState.VerticalVelocity);
        Assert.Equal(0, pair.LegacyCalls);
    }

    [Fact]
    public void UnresolvedSpawnNeverPublishesAPlayerOrCallsLegacyTerrain()
    {
        using var pair = new Pair(serverReady: false);
        int joined = 0;
        pair.Server.PlayerJoined += (_, _) => joined++;
        pair.Pump(6);
        Assert.False(pair.Server.TryGetPlayerState(0, out _));
        Assert.False(pair.Client.Joined);
        Assert.Equal(0, joined);
        Assert.Equal(0, pair.LegacyCalls);
        pair.ServerEnvironment.Physics.Step(0.1f);
    }

    [Fact]
    public void ClientDefersAnUnclassifiedCorrectionThenAppliesItBeforeNewPrediction()
    {
        using var pair = new Pair();
        pair.Pump(6);
        Assert.True(pair.Client.Joined);
        var before = pair.Client.LocalPredictedState.Position;
        pair.ClientEnvironment.Availability = MovementAvailability.Unresolved;
        pair.Client.SendInput(new(Vector2.UnitX, false, 0));
        pair.Pump(1);
        Assert.True(pair.Server.TryGetPlayerState(0, out var authority));
        Assert.InRange(authority.Position.X, 0.3999f, 0.4001f);
        Assert.Equal(before, pair.Client.LocalPredictedState.Position);
        pair.ClientEnvironment.Availability = MovementAvailability.Known;
        pair.Client.SendInput(MoveCommand.Idle);
        Assert.InRange(pair.Client.LocalPredictedState.Position.X, 0.3999f, 0.4001f);
        Assert.Equal(0, pair.LegacyCalls);
    }

    [Fact]
    public void AFirstUnclassifiedBasisDoesNotRenderAnAvatarAtTheDefaultOrigin()
    {
        using var pair = new Pair(clientReady: false);
        pair.Pump(6);
        Assert.DoesNotContain(pair.Client.Snapshot(), item => item.IsLocal);
        pair.ClientEnvironment.Availability = MovementAvailability.Known;
        pair.Pump(1);
        Assert.Contains(pair.Client.Snapshot(), item => item.IsLocal && item.Position.Y > 0.7f);
        Assert.Equal(0, pair.LegacyCalls);
    }

    sealed class Pair : IDisposable
    {
        public readonly ExplicitPlayerEnvironment ServerEnvironment = new();
        public readonly ExplicitPlayerEnvironment ClientEnvironment = new();
        public WorldServer Server { get; }
        public WorldClient Client { get; }
        readonly INetTransport serverTransport;
        public int LegacyCalls;
        public Pair(bool serverReady = true, bool clientReady = true)
        {
            ServerEnvironment.Availability = serverReady ? MovementAvailability.Known : MovementAvailability.Unresolved;
            ClientEnvironment.Availability = clientReady ? MovementAvailability.Known : MovementAvailability.Unresolved;
            var (st, ct) = LoopbackTransport.CreatePair();
            serverTransport = st;
            var serverConfig = new WorldServerConfig
            {
                TickSeconds = 0.1f,
                MaxPlayers = 2,
                InterestRadius = 100,
                FrameAnchoring = false,
                SpawnPosition = _ => new Vector3(0, 0.751f, 0),
                ExplicitMovement = Settings(ServerEnvironment)
            };
            Server = new(st, serverConfig, Legacy, ExplicitPlayerEnvironment.Tuning, physics: ServerEnvironment.Physics);
            Client = new(ct, Legacy, ExplicitPlayerEnvironment.Tuning,
                new WorldClientConfig
                {
                    TickSeconds = 0.1f,
                    FrameAnchoring = false,
                    ExplicitMovement = Settings(ClientEnvironment)
                }, physics: ClientEnvironment.Physics);
        }
        float Legacy(float x, float z) { LegacyCalls++; return 0; }
        static ExplicitPlayerMovement Settings(ExplicitPlayerEnvironment environment) =>
            new(environment.Context, environment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7));
        public void Pump(int count)
        {
            for (int tick = 0; tick < count; tick++) { Server.Poll(); Server.Tick(0.1f); Client.Poll(); }
        }
        public void Dispose()
        {
            Client.Dispose(); serverTransport.Dispose();
            ClientEnvironment.Dispose(); ServerEnvironment.Dispose();
        }
    }
}
