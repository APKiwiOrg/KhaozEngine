using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Tests.Replication;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>The rig's clients by role. The value is the client index and its connection id minus one.</summary>
internal enum RigRole
{
    Mover = 0,
    Observer = 1,
    Replacement = 2,
}

/// <summary>Options for one <see cref="DeltaReliabilityRig"/>. Defaults run format 2 on both ends at a 512-byte
/// limit, the mover at the origin, the observer at (3, 0, 0) and a 24 m interest radius on either head.</summary>
internal sealed class DeltaRigOptions
{
    public bool ServerUnreliable { get; init; } = true;
    public bool ClientUnreliable { get; init; } = true;
    public ReplicationStreamOptions Stream { get; init; } = new();
    public ReplicationStreamOptions? ClientStream { get; init; }
    public int ServerLimit { get; init; } = 512;
    public RigRole FaultedClient { get; init; } = RigRole.Mover;

    /// <summary>The mover's command for a server tick index. Null submits idle commands.</summary>
    public Func<int, MoveCommand>? MoverScript { get; init; }

    public int InputStartTick { get; init; } = 8;
    public float InterestRadius { get; init; } = 24f;
    public Vector3 MoverSpawn { get; init; } = Vector3.Zero;
    public Vector3 ObserverSpawn { get; init; } = new(3f, 0f, 0f);
    public bool UseVisibilityTable { get; init; }

    /// <summary>The observer disconnects at this tick and a replacement client takes its slot.</summary>
    public int? ReplaceObserverAtTick { get; init; }

    /// <summary>Seeds the mover's writer slot so its keyframe carries this sequence.</summary>
    public uint? MoverKeyframeSequence { get; init; }

    /// <summary>Compare each client's live world to the expected projection after every poll that ingested.</summary>
    public bool CheckClientWorld { get; init; } = true;
}

/// <summary>One client of the rig: its endpoints, real <see cref="WorldClient"/>, lifetime and bookkeeping.</summary>
internal sealed class RigClient
{
    public RigRole Role { get; init; }
    public int Index => (int)Role;
    public int StartSubtick { get; init; }
    public int EndSubtick { get; set; } = int.MaxValue;
    public required DeltaFaultTransport Downstream { get; init; }
    public NetConnectionId Connection => new(Index + 1);
    public int Slot => Role == RigRole.Mover ? 0 : 1;
    public INetTransport? Endpoint { get; set; }
    public DeltaFaultTransport? Upstream { get; set; }
    public WorldClient Client { get; set; } = null!;
    public bool RequestedUnreliable { get; set; }
    public int Frames { get; set; }
    public double Clock { get; set; }
    public int InputAccumulator { get; set; } = DeltaFaultSchedule.SubticksPerFrame;
    public int ForwardCursor { get; set; }
    public int TraceCursor { get; set; }
    public uint? LastBaseline { get; set; }
    public int MaxStatePayload { get; set; }
    public HashSet<ReplicationPacketId> Accepted { get; } = new();
    public bool Started => Endpoint is not null;
}

/// <summary>
/// Two real <see cref="WorldClient"/>s and a real <see cref="WorldServer"/> or <see cref="ShardedWorldServer"/> over
/// the in-memory hub, each connection decorated by a <see cref="DeltaFaultTransport"/>, driven through a precomputed
/// finite event list at 120 Hz integer time. Within a subtick the order is: fault release, scheduled actions, client
/// input, the 30 Hz server tick, then each client's 60 Hz frame (poll, ingest checks, presentation, sampling). So at
/// phase zero a client's input lands immediately before the coincident server tick.
/// </summary>
/// <remarks>At every accepted format 2 id the rig asserts the reference projection, the server writer's retained
/// projection and the client's retained projection are equal, and after every ingest that the client's live world
/// equals the reference projection of the ingested state. Every served interest set must equal the rig's own
/// expected visible set. Unmatched faults, held frames or missing events fail the run.</remarks>
internal sealed class DeltaReliabilityRig
{
    private readonly Dictionary<int, List<Action>> actions = new();
    private readonly Dictionary<(int Slot, int Tick), IReadOnlyList<ProjectionEntry>> expectedBySlotTick = new();
    private readonly Dictionary<(int Slot, int Tick), Dictionary<long, RemoteSample>> truthBySlotTick = new();
    private readonly Dictionary<(int Client, ReplicationPacketId Id), int> buildTick = new();
    private readonly Dictionary<(int Client, ReplicationPacketId Id), IReadOnlyList<ProjectionEntry>> serverDump = new();
    private readonly Dictionary<int, World> servedWorldBySlot = new();
    private readonly Dictionary<long, Vector3> entityPositions = new();
    private readonly HashSet<(int Slot, long NetId)> hidden = new();
    private readonly List<int> sendCursor = new();
    private readonly IReadOnlyList<DeltaFault> faults;
    private bool ran;
    private bool seeded;

    public DeltaReliabilityRig(bool sharded, int phaseSubticks, MoveTuning tuning, IReadOnlyList<DeltaFault> faults,
        DeltaRigOptions? options = null)
    {
        if (phaseSubticks is < 0 or >= DeltaFaultSchedule.SubticksPerServerTick)
            throw new ArgumentOutOfRangeException(nameof(phaseSubticks));
        Options = options ?? new DeltaRigOptions();
        Sharded = sharded;
        Phase = phaseSubticks;
        Tuning = tuning;
        this.faults = faults;
        DeltaFaultSchedule.Validate(faults);
        int count = Options.ReplaceObserverAtTick is null ? 2 : 3;
        INetTransport serverTransport = Hub.Server;
        for (int i = 0; i < count; i++)
        {
            var role = (RigRole)i;
            var down = new DeltaFaultTransport(serverTransport, FaultDirection.ServerToClient,
                FaultsFor(role, FaultDirection.ServerToClient), Options.ServerLimit)
            {
                Connection = new(i + 1),
            };
            serverTransport = down;
            Clients.Add(new RigClient
            {
                Role = role,
                Downstream = down,
                StartSubtick = role == RigRole.Replacement
                    ? DeltaFaultSchedule.Subtick(Options.ReplaceObserverAtTick!.Value) : 0,
            });
            sendCursor.Add(0);
        }
        if (Options.ReplaceObserverAtTick is int replaceTick)
        {
            Clients[1].EndSubtick = DeltaFaultSchedule.Subtick(replaceTick);
            At(replaceTick, () => Hub.DisconnectClient(Clients[1].Endpoint!));
        }
        Host = DeltaHost.Create(sharded, serverTransport, Options, tuning, Registry,
            Options.UseVisibilityTable ? IsVisible : null);
        Host.ServeObserved = OnServeObserved;
        Host.BeforeTick += OnBeforeTick;
        StartClient(Clients[0]);
        StartClient(Clients[1]);
    }

    public DeltaRigOptions Options { get; }
    public bool Sharded { get; }
    public int Phase { get; }
    public MoveTuning Tuning { get; }
    public InMemoryTransportHub Hub { get; } = new();
    public DeltaHost Host { get; }
    public ReplicationRegistry Registry { get; } = DeltaRigRegistry.Create();
    public List<RigClient> Clients { get; } = new();
    public DeltaReliabilityTrace Trace { get; } = new();
    public int ServerTick { get; private set; } = -1;
    public int SubtickNow { get; private set; }
    public AoiDeltaReplicator Writer => Host.Writer ?? throw new InvalidOperationException("Delta replication is off.");

    public RigClient this[RigRole role] => Clients[(int)role];

    /// <summary>Raised after each client poll and its ingest checks, before that frame's presentation.</summary>
    public event Action<RigClient, int>? AfterPoll;

    /// <summary>Runs <paramref name="action"/> before the server tick <paramref name="tick"/>, after fault release.</summary>
    public void At(int tick, Action action) => AtSubtick(DeltaFaultSchedule.Subtick(tick), action);

    /// <summary>Runs <paramref name="action"/> at <paramref name="subtick"/>, after fault release and before any input,
    /// server tick or frame of that subtick. Register before <see cref="Run"/>.</summary>
    public void AtSubtick(int subtick, Action action)
    {
        if (ran) throw new InvalidOperationException("Actions are part of the precomputed event list.");
        if (!actions.TryGetValue(subtick, out List<Action>? list)) actions[subtick] = list = new List<Action>();
        list.Add(action);
    }

    /// <summary>Walks the precomputed event list through server tick <paramref name="serverTicks"/> - 1, then checks
    /// every fault matched, nothing is still held and every presentation event produced a row.</summary>
    public void Run(int serverTicks = 180)
    {
        if (ran) throw new InvalidOperationException("A rig runs once.");
        ran = true;
        List<RigEvent> events = BuildEvents(serverTicks);
        foreach (RigEvent e in events)
        {
            SubtickNow = e.Subtick;
            switch (e.Kind)
            {
                case RigEventKind.Release:
                    foreach (RigClient c in Clients)
                    {
                        c.Downstream.AdvanceTo(e.Subtick);
                        c.Upstream?.AdvanceTo(e.Subtick);
                    }
                    break;
                case RigEventKind.Actions:
                    foreach (Action action in actions[e.Subtick]) action();
                    break;
                case RigEventKind.Start:
                    StartClient(Clients[e.Client]);
                    break;
                case RigEventKind.Input:
                    Input(Clients[e.Client], e.Subtick);
                    break;
                case RigEventKind.Server:
                    ServerTick = e.Subtick / DeltaFaultSchedule.SubticksPerServerTick;
                    Host.Poll();
                    Host.Tick(DeltaFaultSchedule.TickSeconds);
                    AfterServer();
                    break;
                case RigEventKind.Frame:
                    Frame(Clients[e.Client], e.Subtick);
                    break;
            }
        }
        foreach (RigClient c in Clients)
        {
            Assert.True(c.Downstream.Unmatched.Count == 0, $"unmatched {c.Role} faults: {string.Join(", ", c.Downstream.Unmatched)}");
            if (c.Upstream is { } up)
                Assert.True(up.Unmatched.Count == 0, $"unmatched {c.Role} faults: {string.Join(", ", up.Unmatched)}");
            Assert.Equal(0, c.Downstream.HeldCount);
            Assert.Equal(0, c.Upstream?.HeldCount ?? 0);
            Trace.AssertComplete(c.Index, events.Count(e => e.Kind == RigEventKind.Frame && e.Client == c.Index));
            ReplicationStreamOptions stream = Options.ClientStream ?? Options.Stream;
            int cap = Options.ServerLimit > 0
                ? Math.Min(Options.ServerLimit, Options.Stream.MaxTransportPayloadBytes)
                : Options.Stream.MaxTransportPayloadBytes;
            Trace.AssertMaxima(c.Index, stream.Limits, cap);
        }
    }

    /// <summary>The reference projection served to <paramref name="role"/> at <paramref name="tick"/>.</summary>
    public IReadOnlyList<ProjectionEntry> ExpectedAt(RigRole role, int tick) =>
        expectedBySlotTick[(this[role].Slot, tick)];

    /// <summary>The server tick a format 2 id was built for <paramref name="role"/>.</summary>
    public int BuildTickOf(RigRole role, ReplicationPacketId id) => buildTick[((int)role, id)];

    /// <summary>The authoritative sample of <paramref name="netId"/> served to <paramref name="role"/> at
    /// <paramref name="tick"/>.</summary>
    public bool TryGetTruth(RigRole role, int tick, long netId, out RemoteSample sample)
    {
        sample = default;
        return truthBySlotTick.TryGetValue((this[role].Slot, tick), out Dictionary<long, RemoteSample>? map)
            && map.TryGetValue(netId, out sample);
    }

    public long NetIdOf(RigRole role)
    {
        Assert.True(Host.TryGetPlayerNetId(this[role].Slot, out long netId), $"{role} is joined");
        return netId;
    }

    public long SpawnEntity(float x, float z, Action<World, Entity>? configure = null)
    {
        long netId = Host.SpawnEntity(x, z, configure);
        entityPositions[netId] = new Vector3(x, 0f, z);
        return netId;
    }

    public void MoveEntity(long netId, float x, float z)
    {
        Assert.True(Host.TryGetEntity(netId, out World world, out Entity entity), $"entity {netId} exists");
        WorldFrame frame = world.TryGet(entity, out ReplicatedPosition p) ? p.Frame : WorldFrame.Origin;
        world.Set(entity, ReplicatedPosition.FromWorld(new Vector3(x, 0f, z), frame));
        entityPositions[netId] = new Vector3(x, 0f, z);
    }

    public void Set<T>(long netId, T value) where T : struct, IComponent
    {
        Assert.True(Host.TryGetEntity(netId, out World world, out Entity entity), $"entity {netId} exists");
        world.Set(entity, value);
    }

    public void Remove<T>(long netId) where T : struct, IComponent
    {
        Assert.True(Host.TryGetEntity(netId, out World world, out Entity entity), $"entity {netId} exists");
        world.Remove<T>(entity);
    }

    /// <summary>Sets a component on <paramref name="role"/>'s own player entity, in the world it was last served
    /// from, which on the sharded head is the cell that owns it.</summary>
    public void SetOnPlayer<T>(RigRole role, T value) where T : struct, IComponent
    {
        World world = servedWorldBySlot[this[role].Slot];
        world.Set(FindEntity(world, NetIdOf(role)), value);
    }

    /// <summary>The world <paramref name="role"/>'s slot was last served from.</summary>
    public World ServedWorld(RigRole role) => servedWorldBySlot[this[role].Slot];

    public void Hide(RigRole viewer, long netId) => hidden.Add((this[viewer].Slot, netId));

    public void Show(RigRole viewer, long netId) => hidden.Remove((this[viewer].Slot, netId));

    public void SetServerLimit(RigRole role, int bytes) => this[role].Downstream.MaxPayloadBytes = bytes;

    public static Entity FindEntity(World world, long netId)
    {
        foreach (Entity e in world.Query().With<NetId>().Entities())
            if (world.Get<NetId>(e).Value == netId) return e;
        throw new XunitException($"net id {netId} is not in the world");
    }

    private bool IsVisible(int slot, long netId) => !hidden.Contains((slot, netId));

    private IReadOnlyList<DeltaFault> FaultsFor(RigRole role, FaultDirection direction) =>
        role == Options.FaultedClient ? faults.Where(f => f.Direction == direction).ToList() : Array.Empty<DeltaFault>();

    private void StartClient(RigClient c)
    {
        INetTransport endpoint = Hub.CreateClient();
        Assert.Equal(c.Connection.Value, Clients.Count(x => x.Started) + 1);
        c.Endpoint = endpoint;
        c.Upstream = new DeltaFaultTransport(endpoint, FaultDirection.ClientToServer,
            FaultsFor(c.Role, FaultDirection.ClientToServer), 0);
        c.Upstream.AdvanceTo(SubtickNow);
        c.RequestedUnreliable = Options.ClientUnreliable;
        var config = new WorldClientConfig
        {
            TickSeconds = DeltaFaultSchedule.TickSeconds,
            RequestUnreliableDeltaReplication = Options.ClientUnreliable,
            ReplicationStream = Options.ClientStream ?? Options.Stream,
            PresentationTraceEnabled = true,
        };
        c.Client = new WorldClient(c.Upstream, static (x, z) => 0f, Tuning, config, registry: DeltaRigRegistry.Create());
    }

    private void OnBeforeTick(float dt)
    {
        if (seeded || Options.MoverKeyframeSequence is not uint sequence) return;
        if (!Host.TryGetRebuildStream(this[RigRole.Mover].Slot, out RebuildServerStream stream) || !stream.Accepted)
            return;
        Writer.SeedRebuildSequenceForTest(this[RigRole.Mover].Slot, sequence);
        seeded = true;
    }

    private void OnServeObserved(int slot, World served, IReadOnlySet<long> interest, long owner)
    {
        servedWorldBySlot[slot] = served;
        IReadOnlyList<ProjectionEntry> expected =
            DeltaReliabilityReference.ExpectedProjection(served, Registry, interest, owner);
        expectedBySlotTick[(slot, ServerTick)] = expected;
        Assert.True(Host.TryGetPlayerState(slot, out PlayerMoveState viewer), $"slot {slot} has a player");
        HashSet<long> visible = DeltaReliabilityReference.ExpectedVisible(slot, owner, viewer.Position,
            KnownPositions(), Options.InterestRadius, Options.UseVisibilityTable ? IsVisible : null);
        Check(visible.SetEquals(interest), $"slot {slot} served [{string.Join(",", interest.Order())}], " +
            $"expected visible [{string.Join(",", visible.Order())}]");
        Check(DeltaReliabilityReference.Members(expected).SetEquals(interest), $"slot {slot} interest is not all served");
        var truth = new Dictionary<long, RemoteSample>();
        foreach (Entity e in served.Query().With<NetId>().Entities())
        {
            long netId = served.Get<NetId>(e).Value;
            if (!interest.Contains(netId) || !served.TryGet(e, out ReplicatedPosition p)) continue;
            served.TryGet(e, out MovementState ms);
            truth[netId] = new RemoteSample(p.Value, ms.FacingYawQ, ms.Grounded, ms.Swimming, ms.TeleportEpoch);
        }
        truthBySlotTick[(slot, ServerTick)] = truth;
    }

    private IEnumerable<KeyValuePair<long, Vector3>> KnownPositions()
    {
        foreach (int slot in Host.JoinedSlots)
            if (Host.TryGetPlayerNetId(slot, out long id) && Host.TryGetPlayerState(slot, out PlayerMoveState s))
                yield return new KeyValuePair<long, Vector3>(id, s.Position);
        foreach (KeyValuePair<long, Vector3> entity in entityPositions)
            if (Host.TryGetEntity(entity.Key, out _, out _)) yield return entity;
    }

    // Maps each format 2 id sent this tick to its build tick, reference projection and the writer's retained copy.
    private void AfterServer()
    {
        foreach (RigClient c in Clients)
        {
            List<FaultSend> sends = c.Downstream.Sends;
            for (int i = sendCursor[c.Index]; i < sends.Count; i++)
            {
                FaultSend send = sends[i];
                if (!TryReadStateId(send.Kind, send.Payload, out ReplicationPacketId id, out _, out bool first)) continue;
                if (first && !buildTick.ContainsKey((c.Index, id)))
                {
                    buildTick[(c.Index, id)] = ServerTick;
                    expectedBySlotTick.TryGetValue((c.Slot, ServerTick), out IReadOnlyList<ProjectionEntry>? e);
                    Check(e is not null, $"{c.Role} id {id} was built without a serve observation");
                }
            }
            sendCursor[c.Index] = sends.Count;
            if (Host.Writer is not { } writer) continue;
            foreach (((int client, ReplicationPacketId id), int _) in buildTick)
                if (client == c.Index && !serverDump.ContainsKey((client, id))
                    && writer.TryGetRetainedProjectionForTest(c.Slot, id, out ReplicationProjection retained))
                    serverDump[(client, id)] = ProjectionDump.Of(retained);
        }
    }

    /// <summary>Reads a format 2 state id from a server payload: a datagram, or a chunk with whether it is the first
    /// and last of its keyframe.</summary>
    public static bool TryReadStateId(byte kind, byte[] payload, out ReplicationPacketId id, out bool last,
        out bool first)
    {
        id = default;
        last = first = false;
        if (kind == FaultFrameKind.RebuildDelta)
        {
            ReadOnlySpan<byte> body = payload.AsSpan(2 + RebuildProtocol.EnvelopeBytes);
            id = new ReplicationPacketId(BinaryPrimitives.ReadUInt64LittleEndian(body[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(body[10..]));
            last = first = true;
            return true;
        }
        if (kind != FaultFrameKind.KeyframeChunk) return false;
        id = new ReplicationPacketId(BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(2)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(10)));
        int index = payload[18 + 3], count = payload[18 + 4];
        first = index == 0;
        last = index == count - 1;
        return true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new XunitException(message);
    }

    private enum RigEventKind
    {
        Release,
        Actions,
        Start,
        Input,
        Server,
        Frame,
    }

    private readonly record struct RigEvent(int Subtick, RigEventKind Kind, int Client);

    // The whole finite run, in dispatch order, before anything runs.
    private List<RigEvent> BuildEvents(int serverTicks)
    {
        var events = new List<RigEvent>();
        for (int s = 0; s < serverTicks * DeltaFaultSchedule.SubticksPerServerTick; s++)
        {
            bool serverEvent = s % DeltaFaultSchedule.SubticksPerServerTick == 0;
            events.Add(new RigEvent(s, RigEventKind.Release, -1));
            if (actions.ContainsKey(s)) events.Add(new RigEvent(s, RigEventKind.Actions, -1));
            foreach (RigClient c in Clients)
                if (c.StartSubtick == s && s > 0) events.Add(new RigEvent(s, RigEventKind.Start, c.Index));
            foreach (RigClient c in Clients)
                if (FrameAt(c, s)) events.Add(new RigEvent(s, RigEventKind.Input, c.Index));
            if (serverEvent) events.Add(new RigEvent(s, RigEventKind.Server, -1));
            foreach (RigClient c in Clients)
                if (FrameAt(c, s)) events.Add(new RigEvent(s, RigEventKind.Frame, c.Index));
        }
        return events;
    }

    private bool FrameAt(RigClient c, int s) => s >= c.StartSubtick && s < c.EndSubtick && s >= Phase
        && (s - Phase) % DeltaFaultSchedule.SubticksPerFrame == 0;

    // Input production runs on its own 30 Hz accumulator at the client's frames, from the input start tick once the
    // client holds its first state. The mover follows its script, every other client submits idle commands.
    private void Input(RigClient c, int subtick)
    {
        c.InputAccumulator += DeltaFaultSchedule.SubticksPerFrame;
        if (c.InputAccumulator < DeltaFaultSchedule.SubticksPerServerTick) return;
        c.InputAccumulator -= DeltaFaultSchedule.SubticksPerServerTick;
        int tick = subtick / DeltaFaultSchedule.SubticksPerServerTick;
        if (tick < Options.InputStartTick || !c.Client.Joined || c.Client.LocalNetId < 0) return;
        MoveCommand command = c.Role == RigRole.Mover && Options.MoverScript is { } script ? script(tick) : MoveCommand.Idle;
        int seq = c.Client.SendInput(command);
        Check(seq >= 0, $"{c.Role} input at subtick {subtick} was not submitted");
        Trace.Submits.Add(new DeltaSubmitRow(c.Index, c.Frames, subtick, tick, seq, command));
    }

    private void Frame(RigClient c, int subtick)
    {
        WorldClient client = c.Client;
        WorldClientRebuildDiagnostics before = client.RebuildDiagnosticsForTest;
        bool live = client.RebuildStreamForTest?.OwnsLiveness ?? false;
        var survivors = new Dictionary<long, Entity>(client.ViewForTest.Entities);
        client.Poll(DeltaFaultSchedule.FrameSeconds);
        foreach (KeyValuePair<long, Entity> kv in client.ViewForTest.Entities)
            if (survivors.TryGetValue(kv.Key, out Entity was))
                Check(was == kv.Value && client.WorldForTest.IsAlive(kv.Value),
                    $"{c.Role} frame {c.Frames}: surviving net id {kv.Key} changed its entity");
        WorldClientRebuildDiagnostics after = client.RebuildDiagnosticsForTest;
        ClientDeltaRebuild? rebuild = client.DeltaRebuildForTest;
        List<FaultForward> forwards = c.Downstream.Forwards;
        var ingests = new List<DeltaIngestRow>();
        for (int i = c.ForwardCursor; i < forwards.Count; i++)
        {
            FaultForward fw = forwards[i];
            if (fw.Kind is FaultFrameKind.RebuildDelta or FaultFrameKind.KeyframeChunk or FaultFrameKind.ReplicationMode)
                c.MaxStatePayload = Math.Max(c.MaxStatePayload, fw.Payload.Length);
            if (fw.Kind is FaultFrameKind.Snapshot or FaultFrameKind.LegacyDelta)
            {
                if (!live) ingests.Add(new DeltaIngestRow(c.Index, c.Frames, subtick, c.Clock,
                    fw.SentSubtick / DeltaFaultSchedule.SubticksPerServerTick, null, false));
                continue;
            }
            if (!TryReadStateId(fw.Kind, fw.Payload, out ReplicationPacketId id, out bool last, out _)) continue;
            if (fw.Kind == FaultFrameKind.RebuildDelta)
                c.LastBaseline = BinaryPrimitives.ReadUInt32LittleEndian(fw.Payload.AsSpan(2 + RebuildProtocol.EnvelopeBytes + 14));
            if (!last || rebuild is null || c.Accepted.Contains(id)) continue;
            if (!rebuild.TryGetRetainedForTest(id, out ReplicationProjection retained)) continue;
            c.Accepted.Add(id);
            VerifyAccept(c, id, retained);
            ingests.Add(new DeltaIngestRow(c.Index, c.Frames, subtick, c.Clock, BuildTickOf(c.Role, id), id,
                fw.Kind == FaultFrameKind.KeyframeChunk));
        }
        int delivered = forwards.Count - c.ForwardCursor;
        c.ForwardCursor = forwards.Count;
        Check(after.IngestCount - before.IngestCount == ingests.Count,
            $"{c.Role} frame {c.Frames}: {after.IngestCount - before.IngestCount} ingests, {ingests.Count} expected");
        Trace.Ingests.AddRange(ingests);
        if (ingests.Count > 0 && Options.CheckClientWorld) VerifyClientWorld(c, ingests[^1].SourceTick);
        AfterPoll?.Invoke(c, subtick);
        double clockBefore = c.Clock;
        client.AdvancePresentation(DeltaFaultSchedule.FrameSeconds);
        c.Clock += DeltaFaultSchedule.FrameSeconds;
        Record(c, subtick, clockBefore, before, after, ingests.Count, delivered);
        c.Frames++;
    }

    private void VerifyAccept(RigClient c, ReplicationPacketId id, ReplicationProjection retained)
    {
        int tick = BuildTickOf(c.Role, id);
        IReadOnlyList<ProjectionEntry> expected = expectedBySlotTick[(c.Slot, tick)];
        Check(serverDump.TryGetValue((c.Index, id), out IReadOnlyList<ProjectionEntry>? server),
            $"{c.Role} accepted {id} the writer never retained");
        Compare(expected, server!, $"{c.Role} writer projection {id} built at tick {tick}");
        Compare(expected, ProjectionDump.Of(retained), $"{c.Role} retained projection {id} built at tick {tick}");
    }

    private void VerifyClientWorld(RigClient c, int sourceTick) => Compare(expectedBySlotTick[(c.Slot, sourceTick)],
        LiveProjection(c.Role), $"{c.Role} live world after ingesting tick {sourceTick} state");

    /// <summary>The client's live world read through the reference serializer: every entity its view holds, owner
    /// scoped to its own net id.</summary>
    public IReadOnlyList<ProjectionEntry> LiveProjection(RigRole role)
    {
        WorldClient client = this[role].Client;
        World world = client.WorldForTest;
        var members = new HashSet<long>();
        foreach (KeyValuePair<long, Entity> kv in client.ViewForTest.Entities)
            if (world.IsAlive(kv.Value)) members.Add(kv.Key);
        return DeltaReliabilityReference.ExpectedProjection(world, Registry, members, client.LocalNetId);
    }

    public void Compare(IReadOnlyList<ProjectionEntry> expected, IReadOnlyList<ProjectionEntry> actual, string what)
    {
        try
        {
            ProjectionDump.AssertEqual(expected, actual);
        }
        catch (XunitException e)
        {
            throw new XunitException($"[server tick {ServerTick}, subtick {SubtickNow}] {what}: {e.Message}");
        }
    }

    private void Record(RigClient c, int subtick, double clockBefore, WorldClientRebuildDiagnostics before,
        WorldClientRebuildDiagnostics after, int ingests, int delivered)
    {
        WorldClient client = c.Client;
        IReadOnlyList<PresentationTrace.Row> rows = client.PresentationTrace!.Rows;
        var held = new HashSet<long>();
        float reconcileError = float.NaN;
        for (int i = c.TraceCursor; i < rows.Count; i++)
        {
            if (rows[i].IsLocal) reconcileError = rows[i].ReconcileError;
            else if (rows[i].Held) held.Add(rows[i].EntityId);
        }
        c.TraceCursor = rows.Count;
        Vector3? rendered = null;
        float heading = 0f;
        bool grounded = false, swimming = false;
        foreach (EntityRenderState e in client.Snapshot())
        {
            if (e.IsLocal)
            {
                rendered = e.Position;
                heading = e.FacingYaw;
                grounded = e.Grounded;
                swimming = e.Swimming;
                continue;
            }
            Trace.Remotes.Add(new DeltaRemoteRow(c.Index, c.Frames, e.Id.Value, e.Position, e.FacingYaw, e.Grounded,
                e.Swimming, held.Contains(e.Id.Value)));
        }
        bool own = Host.TryGetPlayerNetId(c.Slot, out long netId) && netId == client.LocalNetId;
        Vector3 authoritative = own && Host.TryGetPlayerState(c.Slot, out PlayerMoveState s) ? s.Position : default;
        RebuildUsage usage = own && Host.Writer is { } writer ? writer.RebuildUsageForTest(c.Slot) : default;
        ClientDeltaRebuild? rebuild = client.DeltaRebuildForTest;
        Trace.Frames.Add(new DeltaFrameRow(c.Index, c.Frames, subtick, ServerTick, Phase, clockBefore, c.Clock,
            c.RequestedUnreliable, client.ReplicationSelection, client.ConnectionState, rebuild?.LatestAcceptedId,
            c.LastBaseline, after.LastMovementAck, before.PendingPredictionCommands, after.PendingPredictionCommands,
            ingests, after.IngestCount, after.AcceptedCount, authoritative,
            client.LocalNetId >= 0 ? client.LocalPredictedState : null, rendered, heading, grounded, swimming,
            client.LocalTeleportEpoch, reconcileError, rebuild?.RetainedCountForTest ?? 0,
            rebuild?.RetainedBytesForTest ?? 0, usage.RetainedCount, usage.RetainedBytes, c.MaxStatePayload,
            after.MaxTransportPayloadBytes, delivered));
    }
}

/// <summary>One adapter over <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/>, configured identically
/// for the acceptance rig.</summary>
internal sealed class DeltaHost
{
    private readonly WorldServer? flat;
    private readonly ShardedWorldServer? sharded;

    private DeltaHost(WorldServer? flat, ShardedWorldServer? sharded)
    {
        this.flat = flat;
        this.sharded = sharded;
    }

    public static DeltaHost Create(bool sharded, INetTransport transport, DeltaRigOptions options, MoveTuning tuning,
        ReplicationRegistry registry, Func<int, long, bool>? visible)
    {
        Vector3 Spawn(int slot) => slot == 0 ? options.MoverSpawn : options.ObserverSpawn;
        static float Flat(float x, float z) => 0f;
        if (!sharded)
        {
            var config = new WorldServerConfig
            {
                TickSeconds = DeltaFaultSchedule.TickSeconds,
                InterestRadius = options.InterestRadius,
                MaxPlayers = 8,
                SpawnPosition = Spawn,
                AllowUnreliableDeltaReplication = options.ServerUnreliable,
                ReplicationStream = options.Stream,
                EntityVisibleToSlot = visible,
            };
            return new DeltaHost(new WorldServer(transport, config, Flat, tuning, registry: registry), null);
        }
        var shardedConfig = new ShardedWorldServerConfig
        {
            TickSeconds = DeltaFaultSchedule.TickSeconds,
            InterestRadius = options.InterestRadius,
            MaxPlayers = 8,
            SpawnPosition = Spawn,
            AllowUnreliableDeltaReplication = options.ServerUnreliable,
            ReplicationStream = options.Stream,
            EntityVisibleToSlot = visible,
        };
        return new DeltaHost(null, new ShardedWorldServer(transport, shardedConfig, Flat, tuning, registry: registry));
    }

    public AoiDeltaReplicator? Writer => flat is not null ? flat.DeltaReplicatorForTest : sharded!.DeltaReplicatorForTest;

    public IReadOnlyCollection<int> JoinedSlots => flat?.JoinedSlots ?? sharded!.JoinedSlots;

    public Action<int, World, IReadOnlySet<long>, long>? ServeObserved
    {
        set
        {
            if (flat is not null) flat.ServeObservedForTest = value;
            else sharded!.ServeObservedForTest = value;
        }
    }

    public event Action<float> BeforeTick
    {
        add
        {
            if (flat is not null) flat.OnBeforeTick += value;
            else sharded!.OnBeforeTick += value;
        }
        remove
        {
            if (flat is not null) flat.OnBeforeTick -= value;
            else sharded!.OnBeforeTick -= value;
        }
    }

    public event ServerGameMessageHandler GameMessage
    {
        add
        {
            if (flat is not null) flat.OnGameMessage += value;
            else sharded!.OnGameMessage += value;
        }
        remove
        {
            if (flat is not null) flat.OnGameMessage -= value;
            else sharded!.OnGameMessage -= value;
        }
    }

    public void Poll()
    {
        if (flat is not null) flat.Poll();
        else sharded!.Poll();
    }

    public void Tick(float dt)
    {
        if (flat is not null) flat.Tick(dt);
        else sharded!.Tick(dt);
    }

    public bool TryGetPlayerNetId(int slot, out long netId) => flat is not null
        ? flat.TryGetPlayerNetId(slot, out netId) : sharded!.TryGetPlayerNetId(slot, out netId);

    public bool TryGetPlayerState(int slot, out PlayerMoveState state) => flat is not null
        ? flat.TryGetPlayerState(slot, out state) : sharded!.TryGetPlayerState(slot, out state);

    public void SetPlayerState(int slot, in PlayerMoveState state, bool teleport)
    {
        if (flat is not null) flat.SetPlayerState(slot, state, teleport);
        else sharded!.SetPlayerState(slot, state, teleport);
    }

    public long SpawnEntity(float x, float z, Action<World, Entity>? configure) =>
        flat?.SpawnEntity(x, z, configure) ?? sharded!.SpawnEntity(x, z, configure);

    public bool TryGetEntity(long netId, out World world, out Entity entity) => flat is not null
        ? flat.TryGetEntity(netId, out world, out entity) : sharded!.TryGetEntity(netId, out world, out entity);

    public bool TryGetRebuildStream(int slot, out RebuildServerStream stream) => flat is not null
        ? flat.TryGetRebuildStreamForTest(slot, out stream) : sharded!.TryGetRebuildStreamForTest(slot, out stream);

    public bool TryGetReplicationSelection(int slot, out ReplicationSelection selection) => flat is not null
        ? flat.TryGetReplicationSelection(slot, out selection) : sharded!.TryGetReplicationSelection(slot, out selection);

    public void BroadcastNotice(in ServerNotice notice)
    {
        if (flat is not null) flat.BroadcastNotice(notice);
        else sharded!.BroadcastNotice(notice);
    }

    public void SendGameMessageTo(int slot, ushort kind, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (flat is not null) flat.SendGameMessageTo(slot, kind, payload, reliability);
        else sharded!.SendGameMessageTo(slot, kind, payload, reliability);
    }
}
