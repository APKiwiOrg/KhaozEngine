using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

internal sealed class PlayerRangeMovementTestRig : IDisposable
{
    public const float TickSeconds = 1f / 30f;
    public static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        CapsuleRadius = 0.2f, CapsuleHalfHeight = 0.75f, MaxSlopeRadians = 0.8f,
        StepHeight = 0.4f, WalkSpeed = 6f, RunSpeed = 9f,
    };
    static readonly Func<float, float, float> Flat = (_, _) => 0f;
    static readonly Func<float, float, Vector3> Normal = (_, _) => Vector3.UnitY;
    readonly InMemoryTransportHub hub = new();
    readonly IPhysicsWorld serverPhysics;
    readonly IPhysicsWorld clientPhysics;
    readonly ObservedTransport wire;

    public WorldServer Server { get; }
    public WorldClient Client { get; }
    public GroundMoveContext Context { get; }
    public GroundNavigation Navigation { get; }
    public MoveToRange Mover { get; }
    public IReadOnlyList<byte[]> MoveFrames => wire.MoveFrames;
    public int LastAcknowledgedSequence => wire.LastAcknowledgedSequence;
    public int LastSubmittedSequence { get; private set; } = -1;
    public PlayerMoveState Authority
    {
        get
        {
            Assert.True(Server.TryGetPlayerState(0, out PlayerMoveState state));
            return state;
        }
    }

    public PlayerRangeMovementTestRig(bool wallAndBox = false)
    {
        serverPhysics = CreateWorld(wallAndBox);
        clientPhysics = CreateWorld(wallAndBox);
        Context = new GroundMoveContext(Flat, Normal, clientPhysics);
        using (var bake = PhysicsNavBake.Capture(Context,
            new(-2.125f, -2.125f, 2.125f, 1.875f, 0.25f, 4f, 5f, 0.8f, 512, 2048), _ => 0u))
            Navigation = bake.BuildProfile(Tuning, default);
        Mover = new MoveToRange(Navigation);

        Server = new WorldServer(hub.Server, new WorldServerConfig
        {
            TickSeconds = TickSeconds, InterestRadius = 500f, MaxPlayers = 1,
            SpawnPosition = _ => new Vector3(wallAndBox ? -1.5f : 0f, Tuning.CapsuleHalfHeight, 0f),
        }, Flat, Tuning, groundNormal: Normal, physics: serverPhysics);
        wire = new ObservedTransport(hub.CreateClient());
        Client = new WorldClient(wire, Flat, Tuning, new WorldClientConfig
        {
            TickSeconds = TickSeconds,
            Prediction = PredictionSettings.Default with { TickSeconds = TickSeconds, CorrectionDeadZone = 0f },
        }, groundNormal: Normal, physics: clientPhysics);
        for (int tick = 0; tick < 6; tick++)
        {
            Server.Poll();
            Server.Tick(TickSeconds);
            Client.Poll();
        }
        Assert.True(Client.Joined);
        Assert.Equal(Client.LocalNetId, wire.LocalNetId);
        Assert.Equal(-1, LastAcknowledgedSequence);
        Client.AdvancePresentation(TickSeconds);
    }

    public RangeSteering Steer(in ReachTarget target, float range, bool run = false) =>
        Mover.Tick(Client.LocalPredictedState.Move, Tuning, target, range, run, TickSeconds, Context);

    public RangeSteering SteerDirect(DirectMoveToRange driver, in ReachTarget target, float range, bool targetMoves,
        bool run = false) =>
        driver.Tick(Client.LocalPredictedState.Move, Tuning, target, range, run, targetMoves, TickSeconds, Context);

    public MoveCommand Approach(in ReachTarget target, float range, bool run = false, float cameraYaw = 0f) =>
        PlayerPathMovement.Command(Steer(target, range, run), run, cameraYaw);

    public int Submit(in MoveCommand command)
    {
        int sequence = Client.SendInput(command);
        Assert.Equal(LastSubmittedSequence + 1, sequence);
        LastSubmittedSequence = sequence;
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

    public void Ingest() => Client.Poll();

    public void Frame()
    {
        Serve();
        Ingest();
    }

    public void Stop()
    {
        Submit(MoveCommand.Idle);
        Frame();
    }

    public void Drain()
    {
        for (int tick = 0; tick < 16 && LastAcknowledgedSequence < LastSubmittedSequence; tick++) Frame();
        Assert.Equal(LastSubmittedSequence, LastAcknowledgedSequence);
        Assert.Equal(LastSubmittedSequence + 1, MoveFrames.Count);
        Assert.Equal(Authority.Position, Client.LocalPredictedState.Position);
    }

    public static MovementBody Shape(in PlayerMoveState state) =>
        new(state.Position, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight);

    public static PlayerMoveState ReplayOnFlatWorld(in PlayerMoveState basis, params MoveCommand[] commands)
    {
        using IPhysicsWorld physics = CreateWorld(wallAndBox: false);
        var simulator = new PlayerMoveSimulator(Flat, Tuning, groundNormal: Normal, physics: physics);
        PlayerMoveState state = basis;
        foreach (MoveCommand command in commands) state = simulator.Step(state, command, TickSeconds);
        return state;
    }

    public void AssertClear(in PlayerMoveState state)
    {
        var capsule = new CapsuleShape(Tuning.CapsuleRadius, 2f * (Tuning.CapsuleHalfHeight - Tuning.CapsuleRadius));
        if (serverPhysics.ComputePenetration(capsule, Pose.At(state.Position - serverPhysics.Origin), out Vector3 mtv))
        {
            Assert.Equal(0f, mtv.X);
            Assert.Equal(0f, mtv.Z);
            Assert.True(MathF.Abs(mtv.Y) <= 0.000001f, $"Native floor contact MTV: {mtv}");
        }
    }

    static IPhysicsWorld CreateWorld(bool wallAndBox)
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
        if (wallAndBox)
        {
            world.AddStatic(new BoxShape(new Vector3(0.025f, 1f, 0.7f)), Pose.At(new Vector3(0f, 1f, -0.5f)));
            world.AddStatic(new BoxShape(new Vector3(0.4f, 0.75f, 0.4f)), Pose.At(new Vector3(0.75f, 0.75f, -0.5f)));
        }
        world.Step(TickSeconds);
        return world;
    }

    public void Dispose()
    {
        Client.Dispose();
        wire.Dispose();
        hub.Dispose();
        clientPhysics.Dispose();
        serverPhysics.Dispose();
    }

    sealed class ObservedTransport(INetTransport inner) : INetTransport
    {
        public List<byte[]> MoveFrames { get; } = new();
        public int LastAcknowledgedSequence { get; private set; } = -1;
        public long LocalNetId { get; private set; } = -1;

        public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
        {
            if (SessionFrame.ReadOpcode(payload) == SessionOpcode.Data &&
                MoveProtocol.TryDecodeMove(SessionFrame.ReadBody(payload), out _, out _))
                MoveFrames.Add(payload.ToArray());
            inner.Send(target, payload, reliability);
        }

        public bool TryDequeueEvent(out NetEvent value)
        {
            if (!inner.TryDequeueEvent(out value)) return false;
            if (value.Type == NetEventType.Data && SessionFrame.ReadOpcode(value.Data) == SessionOpcode.Data &&
                MoveProtocol.TryDecodeServerFrame(SessionFrame.ReadBody(value.Data), out var kind, out byte[] body) &&
                kind is MoveProtocol.ServerFrameKind.Snapshot or MoveProtocol.ServerFrameKind.Delta &&
                MoveProtocol.TryDecodeSnapshotFrame(body, out long localNetId, out int sequence, out _))
            {
                LocalNetId = localNetId;
                LastAcknowledgedSequence = sequence;
            }
            return true;
        }

        public void Poll() => inner.Poll();
        public void Disconnect(NetConnectionId connection) => inner.Disconnect(connection);
        public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason) => inner.Disconnect(connection, reason);
        public void Dispose() => inner.Dispose();
    }
}
