using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

internal sealed record TilePreparationDispatch(
    long? AppliedStateTick, PreparedCombatEvent[] Results, CombatPreparationEnded[] Ended);

/// <summary>Atomic visible state and terminal deduplication have separate lifetimes.</summary>
internal sealed class TileCombatPreparationLedger
{
    static readonly TilePreparationDispatch Empty = new(null, Array.Empty<PreparedCombatEvent>(), Array.Empty<CombatPreparationEnded>());
    Dictionary<long, TileCombatPreparation> active = new();
    readonly Dictionary<long, History> history = new();
    long stateTick = -1;
    long terminalTick = -1;

    struct History
    {
        internal ulong SampleId;
        internal uint Revision;
        internal ulong PreviousId;
        internal uint PreviousRevision;
        internal ulong TerminalId;
    }

    internal int HistoryCount => history.Count;

    internal TilePreparationDispatch ApplyState(TilePreparationStateFrame frame)
    {
        if (frame.ServerTick <= stateTick || frame.Records.Length > TileProtocol.MaxPreparationRecords) return Empty;
        var next = new Dictionary<long, TileCombatPreparation>();
        var incoming = new HashSet<long>();
        foreach (TileCombatPreparation sample in frame.Records) incoming.Add(sample.AttackerNetId);
        // A later full snapshot is the global stale-frame fence. Retain histories for the previous/current active
        // sets through this tick's terminals, then drop departed terminal-only actors on the next complete state.
        // At most two state sets plus one terminal set are retained, independent of session duration.
        var gone = new List<long>();
        foreach (long attacker in history.Keys)
            if (!active.ContainsKey(attacker) && !incoming.Contains(attacker)) gone.Add(attacker);
        foreach (long attacker in gone) history.Remove(attacker);
        foreach (TileCombatPreparation sample in frame.Records)
        {
            long attacker = sample.AttackerNetId;
            history.TryGetValue(attacker, out History held);
            if (sample.AttackId <= held.TerminalId) continue;
            if (sample.AttackId < held.SampleId || (sample.AttackId == held.SampleId && sample.Revision < held.Revision))
            {
                if (active.TryGetValue(attacker, out var newer) && newer.ImpactTick > frame.ServerTick) next[attacker] = newer;
                continue;
            }
            if (sample.AttackId > held.SampleId)
            {
                held.PreviousId = held.SampleId;
                held.PreviousRevision = held.Revision;
            }
            held.SampleId = sample.AttackId;
            held.Revision = sample.Revision;
            history[attacker] = held;
            next[attacker] = sample;
        }
        active = next;
        stateTick = frame.ServerTick;
        return new(stateTick, Array.Empty<PreparedCombatEvent>(), Array.Empty<CombatPreparationEnded>());
    }

    internal TilePreparationDispatch ApplyTerminals(TilePreparationTerminalFrame frame)
    {
        // The reliable protocol sends exactly one complete terminal set after the same tick's full state set.
        // A malformed/missing state set cannot make a terminal-only stream grow the lifecycle history forever.
        if (frame.ServerTick != stateTick || frame.ServerTick <= terminalTick
            || frame.Records.Length > TileProtocol.MaxPreparationRecords) return Empty;
        var results = new List<PreparedCombatEvent>();
        var ended = new List<CombatPreparationEnded>();
        foreach (TileCombatTerminal terminal in frame.Records)
        {
            long attacker = terminal.AttackerNetId;
            history.TryGetValue(attacker, out History held);
            if (terminal.AttackId <= held.TerminalId
                || (terminal.AttackId == held.SampleId && terminal.Revision < held.Revision)
                || (terminal.AttackId == held.PreviousId && terminal.Revision < held.PreviousRevision)) continue;
            held.TerminalId = terminal.AttackId;
            history[attacker] = held;
            if (active.TryGetValue(attacker, out var sample) && sample.AttackId <= terminal.AttackId)
                active.Remove(attacker);
            if (terminal.Kind == TileCombatTerminalKind.Resolved)
                results.Add(new(terminal.ImpactTick, terminal.AttackId, terminal.Revision, terminal.PresentationKey,
                    new(attacker, terminal.TargetNetId, terminal.Amount, terminal.HitKind,
                        (terminal.Flags & 1) != 0, (terminal.Flags & 2) != 0)));
            else
                ended.Add(new(frame.ServerTick, attacker, terminal.TargetNetId, terminal.AttackId,
                    terminal.Revision, terminal.PresentationKey, terminal.ImpactTick, terminal.Reason));
        }
        terminalTick = frame.ServerTick;
        return new(null, results.ToArray(), ended.ToArray());
    }

    internal bool TryGet(long attacker, out TileCombatPreparation preparation) => active.TryGetValue(attacker, out preparation);

    internal void Forget(long netId)
    {
        active.Remove(netId);
        history.Remove(netId);
        var gone = new List<long>();
        foreach (var pair in active)
            if (pair.Value.TargetNetId == netId) gone.Add(pair.Key);
        foreach (long attacker in gone) { active.Remove(attacker); history.Remove(attacker); }
    }

    internal void Clear()
    {
        active.Clear();
        history.Clear();
        stateTick = terminalTick = -1;
    }
}
