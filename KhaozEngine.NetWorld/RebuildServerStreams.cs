using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// One server's format 2 state, owned by <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/> alike: the
/// validated stream limits, the replication cadence, the epoch allocator, one <see cref="RebuildServerStream"/> per
/// slot whose client asked for format 2, client frame routing, the serve-pass hooks and the restart of the shared
/// delta writer after its legacy sequence is exhausted. Hosts keep movement, visibility and the session lifecycle, and
/// reach back only through the command queue and two callbacks.
/// </summary>
/// <remarks>
/// <para>Restart lifecycle. At every host boundary, the top of <c>Poll</c> before any join is admitted and the serve
/// pass before its capture tick opens, an exhausted writer closes the admission gate and ends every session it serves:
/// each legacy delta slot and each slot whose format 2 stream owns its state. Each one is disconnected with
/// <see cref="ReplicationFailure.RestartToken"/>, so its client takes the reconnect backoff path with a fresh world and
/// view, and stays pending until its ordinary <c>Left</c> runs the host's slot cleanup. Pending slots are neither
/// served nor routed. Only when none is pending is the writer reset and admission reopened. The epoch allocator is
/// never reset, so fresh streams take epochs above the old high-water. Once the allocator has issued its last epoch
/// the gate stays closed for the server lifetime.</para>
/// <para>A host kick of a pending slot runs the host's leave at once, and that counts as the slot's leave: the session
/// server was already told to end the connection, and the slot is neither served nor routed.</para>
/// <para>The reset waits for the transport's own server-side Disconnected event. On a transport that raises none for a
/// disconnect the server asked for, such as <see cref="LoopbackTransport"/>, a restart with an attached session never
/// completes and admission stays closed.</para>
/// </remarks>
internal sealed class RebuildServerStreams
{
    private readonly NetServer net;
    private readonly ReplicationAdmissionGate gate;
    private readonly AoiDeltaReplicator? writer;
    private readonly ReplicationStreamOptions? options;
    private readonly DeltaRebuildOptions? limits;
    private readonly ReplicationCadence? cadence;
    private readonly ReplicationEpochAllocator epochs = new();
    private readonly Dictionary<int, RebuildServerStream> streams = new();
    private readonly List<(int Slot, string Token)> failedSlots = new();
    // Sessions ended for a writer restart whose Left has not run yet. The reset waits for this to empty.
    private readonly HashSet<int> pendingRestart = new();
    private readonly RemoteCommandQueue<MoveCommand> commands;
    private readonly Action<int> raiseMalformed;
    private readonly Action<int, string> disconnect;
    private bool allowance;

    /// <param name="net">The host's session server.</param>
    /// <param name="gate">The outermost authenticator <paramref name="net"/> was built with. Closed while the writer
    /// restarts and after epoch exhaustion.</param>
    /// <param name="writer">The host's shared delta writer, or null when delta replication is off. Restarted after
    /// legacy sequence exhaustion with or without format 2.</param>
    /// <param name="options">The host's stream options. Read only with <paramref name="limits"/>.</param>
    /// <param name="limits">The value <see cref="ValidateConfig"/> returned. Null keeps format 2 off: no cadence runs
    /// and every capability gets the server policy fallback.</param>
    /// <param name="tickSeconds">The host's configured tick, the cadence length.</param>
    /// <param name="commands">The host's MOVE command queue. Every 18-byte frame is validated and stored here.</param>
    /// <param name="raiseMalformed">Flags a slot for a malformed packet.</param>
    /// <param name="disconnect">Ends a slot's session with a reason token through the host's leave path.</param>
    /// <exception cref="ArgumentNullException">A required argument is null, or limits were given without a writer or
    /// options.</exception>
    internal RebuildServerStreams(NetServer net, ReplicationAdmissionGate gate, AoiDeltaReplicator? writer,
        ReplicationStreamOptions? options, DeltaRebuildOptions? limits, float tickSeconds,
        RemoteCommandQueue<MoveCommand> commands, Action<int> raiseMalformed, Action<int, string> disconnect)
    {
        this.net = net ?? throw new ArgumentNullException(nameof(net));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.writer = writer;
        this.commands = commands ?? throw new ArgumentNullException(nameof(commands));
        this.raiseMalformed = raiseMalformed ?? throw new ArgumentNullException(nameof(raiseMalformed));
        this.disconnect = disconnect ?? throw new ArgumentNullException(nameof(disconnect));
        if (limits is null) return;
        if (writer is null) throw new ArgumentNullException(nameof(writer));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.limits = limits;
        cadence = new ReplicationCadence(tickSeconds);
    }

    /// <summary>Validates a host config once, before anything starts. Returns null and checks nothing when the
    /// opt-in is off, so a reliable-only host never starts throwing. Otherwise refuses an opt-in without the delta
    /// switch, checks every format 2 budget and returns the stream limits the host keeps.</summary>
    /// <exception cref="ArgumentException">The opt-in is on and the options are missing, the delta switch is off, or
    /// a budget is invalid.</exception>
    internal static DeltaRebuildOptions? ValidateConfig(bool deltaReplication, bool optIn,
        ReplicationStreamOptions? options, float tickSeconds)
    {
        if (!optIn) return null;
        ReplicationStreamOptions checkedOptions = options ?? throw new ArgumentException(
            "ReplicationStream is required with AllowUnreliableDeltaReplication.", "config");
        return checkedOptions.ValidateConfig(deltaReplication, optIn, tickSeconds);
    }

    /// <summary>The replication tick negotiation and repair deadlines count in. Zero without format 2, where none
    /// runs.</summary>
    internal long Tick => cadence?.Tick ?? 0;

    /// <summary>The last epoch issued.</summary>
    internal ulong EpochHighWater => epochs.HighWater;

    /// <summary>Test seam: records <paramref name="lastIssued"/> as the last epoch issued.</summary>
    internal void SeedEpochsForTest(ulong lastIssued) => epochs.SeedForTest(lastIssued);

    /// <summary>Friend-test diagnostic: sessions ended for a writer restart whose <c>Left</c> has not run
    /// yet.</summary>
    internal int PendingRestartCount => pendingRestart.Count;

    /// <summary>Test seam: invoked once per served slot after the visibility filter and before either writer.</summary>
    internal Action<int, World, IReadOnlySet<long>, long>? ServeObserved { get; set; }

    /// <summary>The slot's selection, default when its client never asked for format 2.</summary>
    internal ReplicationSelection SelectionOf(int slot) =>
        streams.TryGetValue(slot, out RebuildServerStream? stream) ? stream.Selection : default;

    /// <summary>The slot's stream, when its client asked for one.</summary>
    internal bool TryGetStream(int slot, out RebuildServerStream stream) => streams.TryGetValue(slot, out stream!);

    /// <summary>Routes one client payload right after the rate limiter. Length 18 is always a MOVE, validated and
    /// stored here. Any other length goes to the format 2 control decode, which claims its own sub-markers before the
    /// permissive MOVE fallback can see them. Malformed controls, and valid ones on the wrong channel, are flagged.
    /// False leaves the payload to the legacy ack, control and game-message decodes.</summary>
    internal bool Route(int slot, byte[] data, NetChannelReliability reliability)
    {
        // A session ended for a restart is only waiting for its Left. Nothing it still sends may touch writer state.
        if (pendingRestart.Contains(slot)) return true;
        if (data.Length == RebuildServerStream.MoveFrameBytes)
        {
            if (MoveProtocol.TryDecodeMove(data, out int seq, out MoveCommand cmd)) commands.Store(slot, seq, cmd);
            else raiseMalformed(slot);
            return true;
        }
        switch (RebuildProtocol.DecodeClientControl(data, out RebuildClientControl control))
        {
            case ControlReadResult.Unclaimed:
                return false;
            case ControlReadResult.Valid when RebuildServerStream.IsRequiredChannel(control.Kind, reliability):
                if (streams.TryGetValue(slot, out RebuildServerStream? stream))
                    stream.HandleControl(control, reliability, Tick);
                return true;
            default:
                raiseMalformed(slot);
                return true;
        }
    }

    /// <summary>Answers a slot's format 2 capability, creating its stream on first use. Without format 2 the stream
    /// holds no writer and answers with the server policy fallback.</summary>
    internal void OnCapability(int slot)
    {
        if (!streams.TryGetValue(slot, out RebuildServerStream? stream))
        {
            stream = new RebuildServerStream(net, slot, cadence is null ? null : writer, options, limits, epochs);
            streams[slot] = stream;
        }
        stream.OnRebuildCapability(Tick);
    }

    /// <summary>Advances the cadence from elapsed host time, once per host tick, before the serve pass. Keeps whether
    /// this tick crossed a cadence boundary: format 2 routine sends and keyframe chunks spend allowance only then, even
    /// on a short sharded frame that ran no simulation step.</summary>
    internal void Advance(float dt) => allowance = cadence?.Advance(dt) ?? false;

    /// <summary>The serve-pass hook for one slot, after the host opened its one writer capture tick. True when format 2
    /// owns the slot's state this tick, so the legacy writers must not serve it. A faulted stream is owned even before
    /// its negotiation ended, so it reports its restart instead of being served legacy state.</summary>
    internal bool Serve(int slot, World served, IReadOnlySet<long> filteredInterest, long ownerNetId, int movementAck)
    {
        if (pendingRestart.Contains(slot)) return true;   // ended for a restart: neither writer serves it
        ServeObserved?.Invoke(slot, served, filteredInterest, ownerNetId);
        if (!streams.TryGetValue(slot, out RebuildServerStream? stream)) return false;
        if (stream.ServesLegacy && !stream.Faulted) return false;
        stream.Serve(served, filteredInterest, ownerNetId, movementAck, Tick, allowance);
        return true;
    }

    /// <summary>Ends every session whose stream reported a typed failure. Runs after the serve pass, so no writer sees
    /// a slot vanish mid-pass. A slot already ended for a restart is skipped: it got its restart token, and its leave
    /// waits for its own Left.</summary>
    internal void EndServe()
    {
        if (streams.Count == 0) return;
        failedSlots.Clear();
        foreach (KeyValuePair<int, RebuildServerStream> entry in streams)
            if (entry.Value.Failure is ReplicationFailure failure && !pendingRestart.Contains(entry.Key))
                failedSlots.Add((entry.Key, failure.Token));
        foreach ((int slot, string token) in failedSlots) disconnect(slot, token);
    }

    /// <summary>Runs the writer restart lifecycle at a host boundary: the top of <c>Poll</c> before any join is
    /// admitted, and the serve pass through <see cref="OpenCaptureTick"/>. See the remarks on this type.</summary>
    /// <param name="legacyDeltaSlots">The host's joined slots that receive legacy deltas.</param>
    internal void CheckRestart(IReadOnlyCollection<int> legacyDeltaSlots)
    {
        // No epoch is left for this server lifetime, so no fresh stream could negotiate.
        if (EpochsExhausted) gate.Close();
        if (writer is null || !writer.LegacySequenceExhausted) return;
        gate.Close();
        // Every session the writer serves, read afresh at each boundary: a slot that started receiving legacy deltas or
        // took a format 2 stream since the last check is ended too.
        foreach (int slot in legacyDeltaSlots) EndForRestart(slot);
        foreach (KeyValuePair<int, RebuildServerStream> entry in streams)
            if (!entry.Value.ServesLegacy) EndForRestart(entry.Key);
        if (pendingRestart.Count > 0) return;
        writer.ResetAfterLegacySequenceExhaustion();
        if (!EpochsExhausted) gate.Open();
    }

    /// <summary>Opens the writer's one capture tick for the host serve pass, after the restart check. While the writer
    /// is exhausted and waits for ended sessions no capture is attempted, and only snapshot slots are served.</summary>
    /// <param name="legacyDeltaSlots">The host's joined slots that receive legacy deltas.</param>
    internal void OpenCaptureTick(IReadOnlyCollection<int> legacyDeltaSlots)
    {
        CheckRestart(legacyDeltaSlots);
        if (writer is not null && !writer.LegacySequenceExhausted) writer.BeginTick();
    }

    private bool EpochsExhausted => epochs.HighWater == ulong.MaxValue;

    // Ends one session for the restart through the session server only, so its client takes the backoff reconnect path.
    // The host's leave runs off the transport's own Disconnected event, never early.
    private void EndForRestart(int slot)
    {
        if (pendingRestart.Add(slot)) net.Disconnect(slot, ReplicationFailure.RestartToken);
    }

    /// <summary>A slot's cleanup when its session left: it is no longer pending a restart and its stream is
    /// dropped.</summary>
    internal void Left(int slot)
    {
        pendingRestart.Remove(slot);
        Forget(slot);
    }

    /// <summary>Drops a slot's stream on join, and through <see cref="Left"/> on leave. The host forgets the writer
    /// slot itself. A faulted stream is dropped too: its connection is already ending, so no restart token is sent on
    /// top.</summary>
    internal void Forget(int slot)
    {
        if (streams.Remove(slot, out RebuildServerStream? stream)) stream.Forget();
    }
}
