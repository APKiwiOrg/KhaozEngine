using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Tunables for <see cref="WorldClient"/>.</summary>
public sealed class WorldClientConfig
{
    /// <summary>Fixed client prediction tick, seconds. Must match the server tick for clean reconciliation.</summary>
    public float TickSeconds { get; init; } = 1f / 30f;
    /// <summary>Override prediction settings; defaults to <see cref="PredictionSettings.Default"/> at <see cref="TickSeconds"/>.</summary>
    public PredictionSettings? Prediction { get; init; }
}

/// <summary>
/// Client glue over the shipped netcode: wraps a <see cref="NetClient"/> session, a
/// <see cref="ClientReplicationView"/> for remote entities, and <see cref="ClientPrediction{TState,TCommand}"/>
/// for the local avatar. Per frame the sample <see cref="Poll"/>s (ingests AoI snapshots; reconciles the local
/// player against the authoritative basis), <see cref="SendInput"/>s once per tick (predicts + transmits), and
/// reads <see cref="Snapshot"/> to render a capsule per entity (local predicted, remotes replicated). Render-free.
/// </summary>
public sealed class WorldClient
{
    private readonly NetClient net;
    private readonly World world = new();
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private readonly ClientReplicationView view;
    private readonly ClientPrediction<PlayerMoveState, MoveCommand> prediction;
    private int authoritativeTick;

    public WorldClient(INetTransport transport, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        config ??= new WorldClientConfig();
        net = new NetClient(transport, token);
        view = new ClientReplicationView(registry);
        var simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal);
        PredictionSettings settings = config.Prediction ?? (PredictionSettings.Default with { TickSeconds = config.TickSeconds });
        prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator, settings);
    }

    /// <summary>Net id of the local player, or -1 until the first snapshot identifies it.</summary>
    public int LocalNetId { get; private set; } = -1;
    /// <summary>True once the session handshake has joined.</summary>
    public bool Joined { get; private set; }

    /// <summary>Pumps the session: ingests AoI snapshots, applies remote replication, reconciles the local avatar.</summary>
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    Joined = true;
                    break;
                case ClientSessionEventKind.Data:
                    OnSnapshot(ev.Data);
                    break;
                case ClientSessionEventKind.Disconnected:
                    Joined = false;
                    break;
            }
        }
    }

    /// <summary>Predicts one command forward and transmits it. Returns the assigned seq.</summary>
    public int SendInput(in MoveCommand cmd)
    {
        int seq = prediction.Predict(cmd);
        net.Send(MoveProtocol.EncodeMove(seq, cmd), NetChannelReliability.ReliableOrdered);
        return seq;
    }

    /// <summary>Advances the prediction's inter-tick smoothing (call once per render frame).</summary>
    public void AdvancePresentation(float dt) => prediction.AdvancePresentation(dt);

    /// <summary>The current renderable set: local player predicted, remotes from the latest replicated position.</summary>
    public IReadOnlyList<EntityRenderState> Snapshot()
    {
        var list = new List<EntityRenderState>();
        foreach (KeyValuePair<int, Entity> kv in view.Entities)
        {
            if (!world.IsAlive(kv.Value)) continue;
            bool isLocal = kv.Key == LocalNetId;
            Vector3 pos;
            if (isLocal)
            {
                pos = prediction.RenderedState.Position;
            }
            else
            {
                world.TryGet(kv.Value, out ReplicatedPosition rp);
                pos = rp.Value;
            }
            list.Add(new EntityRenderState(new NetId(kv.Key), pos, isLocal));
        }
        return list;
    }

    private void OnSnapshot(byte[] data)
    {
        if (!MoveProtocol.TryDecodeSnapshotFrame(data, out int localNetId, out int ackSeq, out byte[] snapshot)) return;
        bool first = LocalNetId < 0;
        LocalNetId = localNetId;
        view.Apply(world, snapshot);

        if (view.TryGetEntity(localNetId, out Entity local) && world.TryGet(local, out ReplicatedPosition p))
        {
            var basis = new PlayerMoveState { Position = p.Value };
            if (first) prediction.Reset(basis);                  // seed prediction at the authoritative spawn
            prediction.Reconcile(authoritativeTick++, basis, ackSeq);
        }
    }
}
