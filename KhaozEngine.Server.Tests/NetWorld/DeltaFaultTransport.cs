using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>One frame a fault transport saw on its send path, before any fault.</summary>
/// <param name="Subtick">The subtick the frame was sent.</param>
/// <param name="Connection">The target connection.</param>
/// <param name="Kind">Its <see cref="FaultFrameKind"/> family.</param>
/// <param name="Ordinal">Its 1-based ordinal within the family, 0 when unclassified or injected.</param>
/// <param name="Reliability">The channel it was sent on.</param>
/// <param name="Payload">A copy of the transport payload, session frame byte included.</param>
/// <param name="Fault">The fault applied, if any.</param>
/// <param name="Limit">The limit this decorator reported for the connection when the frame was sent, 0 when it
/// forwarded the query.</param>
internal readonly record struct FaultSend(int Subtick, NetConnectionId Connection, byte Kind, int Ordinal,
    NetChannelReliability Reliability, byte[] Payload, DeltaFault? Fault, int Limit);

/// <summary>One frame a fault transport handed to the transport it wraps.</summary>
/// <param name="Subtick">The subtick it was handed on.</param>
/// <param name="SentSubtick">The subtick it was originally sent.</param>
/// <param name="Connection">The target connection.</param>
/// <param name="Kind">Its family.</param>
/// <param name="Ordinal">Its ordinal, 0 when unclassified or injected.</param>
/// <param name="Reliability">The channel.</param>
/// <param name="Payload">The transport payload.</param>
/// <param name="Faulted">True when a fault held or duplicated this copy.</param>
/// <param name="Injected">True for a hand-built frame the test injected.</param>
internal readonly record struct FaultForward(int Subtick, int SentSubtick, NetConnectionId Connection, byte Kind,
    int Ordinal, NetChannelReliability Reliability, byte[] Payload, bool Faulted, bool Injected);

/// <summary>
/// A deterministic <see cref="INetTransport"/> decorator over an existing endpoint that applies a finite fault table
/// to the frames it sends. It forwards polling, events, stats, disconnects and the packet limit query, and answers that
/// query with <see cref="MaxPayloadBytes"/> for its connection when that is positive. Wrap a client endpoint with
/// <see cref="FaultDirection.ClientToServer"/>, or the server endpoint with <see cref="FaultDirection.ServerToClient"/>
/// scoped to one <see cref="Connection"/>. Decorators compose: a server endpoint carries one per client.
/// </summary>
/// <remarks>
/// <para>Time is the 120 Hz subtick the rig passes to <see cref="AdvanceTo"/>, which hands on every held frame whose
/// release has come. A delayed or queued reliable frame holds every later reliable frame to its connection, so reliable
/// order is never broken and nothing reliable is lost. Unreliable frames may drop, duplicate or wait at most
/// <see cref="DeltaFaultSchedule.MaxOrdinaryDelaySubticks"/>. At most <see cref="DeltaFaultSchedule.MaxQueuedFrames"/>
/// frames wait at once. Anything outside those bounds is a fixture error, never a silent change of schedule.</para>
/// <para>A disconnect first hands on every frame still held for that connection, so data stays ahead of the terminal
/// event.</para>
/// </remarks>
internal sealed class DeltaFaultTransport : INetTransport
{
    private readonly INetTransport inner;
    private readonly FaultDirection direction;
    private readonly Dictionary<(byte Kind, int Ordinal), DeltaFault> faults = new();
    private readonly HashSet<DeltaFault> unmatched = new();
    private readonly Dictionary<(int Connection, byte Kind), int> ordinals = new();
    private readonly List<Held> held = new();
    private long sequence;

    /// <param name="inner">The endpoint this decorates.</param>
    /// <param name="direction">The direction of the frames this endpoint sends.</param>
    /// <param name="faults">The faults for this endpoint. Every one must travel in <paramref name="direction"/>.</param>
    /// <param name="maxPayloadBytes">The packet limit reported for the scoped connection. Zero forwards the query.</param>
    /// <exception cref="InvalidOperationException">The schedule is a fixture error.</exception>
    public DeltaFaultTransport(INetTransport inner, FaultDirection direction, IReadOnlyList<DeltaFault> faults,
        int maxPayloadBytes)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.direction = direction;
        DeltaFaultSchedule.Validate(faults);
        foreach (DeltaFault fault in faults)
        {
            if (fault.Direction != direction)
                throw DeltaFaultSchedule.FixtureError($"a {fault.Direction} fault given to a {direction} endpoint");
            this.faults[(fault.FrameKind, fault.Ordinal)] = fault;
            unmatched.Add(fault);
        }
        MaxPayloadBytes = maxPayloadBytes;
    }

    /// <summary>The only connection this decorator faults, records and answers the limit for. Null for all, which
    /// suits a client endpoint.</summary>
    public NetConnectionId? Connection { get; init; }

    /// <summary>The limit reported for the scoped connection on both channels. Zero forwards the query, which an
    /// in-memory endpoint answers as unknown.</summary>
    public int MaxPayloadBytes { get; set; }

    /// <summary>The current subtick.</summary>
    public int Subtick { get; private set; }

    /// <summary>Every in-scope frame sent through this decorator, in order.</summary>
    public List<FaultSend> Sends { get; } = new();

    /// <summary>Every in-scope frame handed to the wrapped transport, in order.</summary>
    public List<FaultForward> Forwards { get; } = new();

    /// <summary>Frames waiting for release.</summary>
    public int HeldCount => held.Count;

    /// <summary>The most frames that ever waited at once.</summary>
    public int MaxHeldCount { get; private set; }

    /// <summary>Scheduled faults that no frame has matched yet.</summary>
    public IReadOnlyCollection<DeltaFault> Unmatched => unmatched;

    /// <summary>Moves time to <paramref name="subtick"/> and hands on every held frame whose release has come, in send
    /// order, keeping each connection's reliable frames in order.</summary>
    /// <exception cref="InvalidOperationException">Time moved backwards.</exception>
    public void AdvanceTo(int subtick)
    {
        if (subtick < Subtick) throw DeltaFaultSchedule.FixtureError($"time moved back from {Subtick} to {subtick}");
        Subtick = subtick;
        var blocked = new HashSet<int>();
        for (int i = 0; i < held.Count;)
        {
            Held h = held[i];
            bool reliable = h.Reliability == NetChannelReliability.ReliableOrdered;
            if (reliable && blocked.Contains(h.Connection.Value)) { i++; continue; }
            if (h.Release > subtick)
            {
                if (reliable) blocked.Add(h.Connection.Value);
                i++;
                continue;
            }
            held.RemoveAt(i);
            Forward(h.Connection, h.Payload, h.Reliability, h.SentSubtick, h.Kind, h.Ordinal, faulted: true, h.Injected);
        }
    }

    /// <summary>Hands a hand-built frame on at <paramref name="releaseSubtick"/>, outside every fault and ordinal,
    /// behind any reliable frame still held for its connection.</summary>
    public void InjectAt(NetConnectionId target, byte[] payload, NetChannelReliability reliability, int releaseSubtick)
    {
        if (releaseSubtick < Subtick) throw DeltaFaultSchedule.FixtureError("an injection in the past");
        Hold(target, (byte[])payload.Clone(), reliability, releaseSubtick, FaultFrameKind.Unclassified, 0,
            injected: true);
        AdvanceTo(Subtick);
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (!InScope(target))
        {
            inner.Send(target, payload, reliability);
            return;
        }
        byte[] copy = payload.ToArray();
        byte kind = direction == FaultDirection.ServerToClient
            ? FaultFrameKind.ClassifyServer(copy)
            : FaultFrameKind.ClassifyClient(copy, reliability);
        int ordinal = 0;
        if (kind != FaultFrameKind.Unclassified)
        {
            ordinals.TryGetValue((target.Value, kind), out ordinal);
            ordinals[(target.Value, kind)] = ++ordinal;
        }
        DeltaFault? fault = faults.TryGetValue((kind, ordinal), out DeltaFault found) ? found : null;
        Sends.Add(new FaultSend(Subtick, target, kind, ordinal, reliability, copy, fault, MaxPayloadBytes));
        if (fault is not DeltaFault f)
        {
            SendInOrder(target, copy, reliability, kind, ordinal);
            return;
        }
        Apply(f, target, copy, reliability, kind, ordinal);
    }

    public void Poll() => inner.Poll();

    public bool TryDequeueEvent(out NetEvent ev) => inner.TryDequeueEvent(out ev);

    public void Disconnect(NetConnectionId connection)
    {
        Flush(connection);
        inner.Disconnect(connection);
    }

    public void Disconnect(NetConnectionId connection, ReadOnlySpan<byte> reason)
    {
        Flush(connection);
        inner.Disconnect(connection, reason);
    }

    public NetTransportStats Stats => inner.Stats;

    public int MaxUnfragmentedPayloadBytes(NetConnectionId connection, NetChannelReliability reliability) =>
        InScope(connection) && MaxPayloadBytes > 0
            ? MaxPayloadBytes
            : inner.MaxUnfragmentedPayloadBytes(connection, reliability);

    public void Dispose() => inner.Dispose();

    private bool InScope(NetConnectionId target) => Connection is not NetConnectionId scoped || scoped == target;

    private void Apply(DeltaFault fault, NetConnectionId target, byte[] copy, NetChannelReliability reliability,
        byte kind, int ordinal)
    {
        if (Subtick > DeltaFaultSchedule.Subtick(DeltaFaultSchedule.LastFaultTick))
            throw DeltaFaultSchedule.FixtureError($"fault {fault} matched at subtick {Subtick}, after the fault window");
        bool reliable = reliability == NetChannelReliability.ReliableOrdered;
        if (reliable && fault.Action != FaultAction.Delay)
            throw DeltaFaultSchedule.FixtureError($"{fault.Action} of a reliable frame, {fault}");
        unmatched.Remove(fault);
        switch (fault.Action)
        {
            case FaultAction.Drop:
                return;
            case FaultAction.Delay:
                if (fault.ReleaseSubtick <= Subtick)
                    throw DeltaFaultSchedule.FixtureError($"delay {fault} does not release after subtick {Subtick}");
                if (!reliable && fault.ReleaseSubtick - Subtick > DeltaFaultSchedule.MaxOrdinaryDelaySubticks)
                    throw DeltaFaultSchedule.FixtureError(
                        $"delay {fault} exceeds {DeltaFaultSchedule.MaxOrdinaryDelaySubticks} subticks from {Subtick}");
                Hold(target, copy, reliability, fault.ReleaseSubtick, kind, ordinal, injected: false);
                return;
            case FaultAction.Duplicate:
                if (fault.ReleaseSubtick < Subtick
                    || fault.ReleaseSubtick - Subtick > DeltaFaultSchedule.MaxOrdinaryDelaySubticks)
                    throw DeltaFaultSchedule.FixtureError($"duplicate {fault} releases outside the delay bound");
                SendInOrder(target, copy, reliability, kind, ordinal);
                Hold(target, copy, reliability, fault.ReleaseSubtick, kind, ordinal, injected: false);
                AdvanceTo(Subtick);
                return;
        }
    }

    // A reliable frame waits behind any reliable frame still held for its connection. Everything else goes now.
    private void SendInOrder(NetConnectionId target, byte[] copy, NetChannelReliability reliability, byte kind,
        int ordinal)
    {
        if (reliability == NetChannelReliability.ReliableOrdered && ReliableHeldFor(target))
        {
            Hold(target, copy, reliability, Subtick, kind, ordinal, injected: false);
            return;
        }
        Forward(target, copy, reliability, Subtick, kind, ordinal, faulted: false, injected: false);
    }

    private bool ReliableHeldFor(NetConnectionId target)
    {
        foreach (Held h in held)
            if (h.Connection == target && h.Reliability == NetChannelReliability.ReliableOrdered) return true;
        return false;
    }

    private void Hold(NetConnectionId target, byte[] payload, NetChannelReliability reliability, int release, byte kind,
        int ordinal, bool injected)
    {
        if (held.Count >= DeltaFaultSchedule.MaxQueuedFrames)
            throw DeltaFaultSchedule.FixtureError(
                $"frame {held.Count + 1} queued, the bound is {DeltaFaultSchedule.MaxQueuedFrames}");
        held.Add(new Held(++sequence, target, payload, reliability, release, Subtick, kind, ordinal, injected));
        MaxHeldCount = Math.Max(MaxHeldCount, held.Count);
    }

    private void Flush(NetConnectionId connection)
    {
        for (int i = 0; i < held.Count;)
        {
            Held h = held[i];
            if (h.Connection != connection) { i++; continue; }
            held.RemoveAt(i);
            Forward(h.Connection, h.Payload, h.Reliability, h.SentSubtick, h.Kind, h.Ordinal, faulted: true, h.Injected);
        }
    }

    private void Forward(NetConnectionId target, byte[] payload, NetChannelReliability reliability, int sentSubtick,
        byte kind, int ordinal, bool faulted, bool injected)
    {
        Forwards.Add(new FaultForward(Subtick, sentSubtick, target, kind, ordinal, reliability, payload, faulted,
            injected));
        inner.Send(target, payload, reliability);
    }

    private readonly record struct Held(long Sequence, NetConnectionId Connection, byte[] Payload,
        NetChannelReliability Reliability, int Release, int SentSubtick, byte Kind, int Ordinal, bool Injected);
}
