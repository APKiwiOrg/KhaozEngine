using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileWorldServer
{
    // Only connected, preparation-enabled viewers own history. Lingering bodies are not viewers.
    Dictionary<int, HashSet<(long Attacker, ulong AttackId)>>? preparationViewers;
    SortedSet<int>? preparationOverflowViewers;
    bool servingPreparations;

    /// <summary>Preparation sets refused atomically because a viewer exceeded the wire record budget.</summary>
    public long RejectedCombatPreparationFrameSetCount { get; private set; }

    static void ValidatePreparationBudgets(TileWorldServerConfig config)
    {
        if (config.MaxPlayers < 0 || config.MaxActorsPerCell < 0
            || checked((long)config.MaxPlayers + config.MaxActorsPerCell) > TileProtocol.MaxPreparationRecords)
            throw new ArgumentOutOfRangeException(nameof(config), "Enabled preparation exceeds the minimum viewer record budget.");
    }

    void JoinPreparationViewer(int slot)
    {
        if (preparation is null) return;
        preparationViewers ??= new();
        preparationViewers[slot] = new();
    }

    void SendPreparationsTo(int slot, HashSet<long> interest)
    {
        if (preparation is null || preparationViewers is null
            || !preparationViewers.TryGetValue(slot, out var previous)
            || preparationOverflowViewers?.Contains(slot) == true) return;
        var states = new List<TileCombatPreparation>();
        foreach (long id in netIdBySlot.Values)
            if (!Collect(id)) { RejectPreparationFrameSet(slot); return; }
        foreach (long id in actorNetIds)
            if (!Collect(id)) { RejectPreparationFrameSet(slot); return; }
        states.Sort(static (a, b) => a.AttackerNetId.CompareTo(b.AttackerNetId));

        var terminals = new List<TileCombatTerminal>();
        foreach (CombatPreparationEnded ended in preparation.Ended)
        {
            if (!Visible(ended.AttackerNetId, ended.TargetNetId, ended.AttackId)) continue;
            if (terminals.Count == TileProtocol.MaxPreparationRecords) { RejectPreparationFrameSet(slot); return; }
            terminals.Add(new(ended.AttackerNetId, ended.TargetNetId, ended.AttackId, ended.Revision,
                ended.PresentationKey, ended.ImpactTick, TileCombatTerminalKind.Cancelled, ended.Reason, 0, 0, 0));
        }
        terminals.Sort(static (a, b) => a.AttackerNetId != b.AttackerNetId
            ? a.AttackerNetId.CompareTo(b.AttackerNetId) : a.AttackId.CompareTo(b.AttackId));
        foreach (PreparedCombatEvent result in preparation.Results)
        {
            TileCombatEvent outcome = result.Outcome;
            if (!Visible(outcome.AttackerNetId, outcome.TargetNetId, result.AttackId)) continue;
            if (terminals.Count == TileProtocol.MaxPreparationRecords) { RejectPreparationFrameSet(slot); return; }
            terminals.Add(new(outcome.AttackerNetId, outcome.TargetNetId, result.AttackId, result.Revision,
                result.PresentationKey, result.ImpactTick, TileCombatTerminalKind.Resolved,
                TileCombatPreparationEndReason.None, outcome.Amount, outcome.Kind,
                (byte)((outcome.Landed ? 1 : 0) | (outcome.Killed ? 2 : 0))));
        }
        if (!SendPreparationFrameSet(slot, states, terminals)) return;
        previous.Clear();
        foreach (TileCombatPreparation state in states) previous.Add((state.AttackerNetId, state.AttackId));

        bool Collect(long id)
        {
            // The authoritative owner carries the schedule created after this tick's border-ghost sync.
            if (!TryGetCombatPreparation(id, out var state)
                || (!interest.Contains(state.AttackerNetId) && !interest.Contains(state.TargetNetId))) return true;
            if (states.Count == TileProtocol.MaxPreparationRecords) return false;
            states.Add(state);
            return true;
        }
        bool Visible(long attacker, long target, ulong attackId) =>
            interest.Contains(attacker) || interest.Contains(target) || previous.Contains((attacker, attackId));
    }

    // Production seam: count before indexing, validate across chunk boundaries, and encode BOTH sets before
    // emitting either. Artificial oversized collections exercise the same refusal without creating 65k actors.
    internal bool SendPreparationFrameSet(int slot, IReadOnlyList<TileCombatPreparation> states,
        IReadOnlyList<TileCombatTerminal> terminals)
    {
        if (states.Count > TileProtocol.MaxPreparationRecords || terminals.Count > TileProtocol.MaxPreparationRecords)
            return RejectPreparationFrameSet(slot);
        for (int i = 0; i < states.Count; i++)
            if (!TileProtocol.ValidPreparationRecord(states[i], TickCount)
                || (i > 0 && states[i - 1].AttackerNetId >= states[i].AttackerNetId))
                throw new ArgumentException("Invalid complete preparation state set.", nameof(states));
        var order = new TileCombatTerminalOrder(TileProtocol.MaxPreparationRecords);
        for (int i = 0; i < terminals.Count; i++)
            if (!TileProtocol.ValidPreparationTerminal(terminals[i], TickCount) || !order.TryAdd(terminals[i]))
                throw new ArgumentException("Invalid complete preparation terminal set.", nameof(terminals));

        int stateChunks = Math.Max(1, (states.Count + 254) / 255);
        int terminalChunks = (terminals.Count + 254) / 255;
        var frames = new List<byte[]>(stateChunks + terminalChunks);
        for (int i = 0; i < stateChunks; i++)
            frames.Add(TileProtocol.EncodePreparationChunk(new(TickCount, (ushort)i, (ushort)stateChunks),
                states, i * 255, Math.Min(255, states.Count - i * 255)));
        for (int i = 0; i < terminalChunks; i++)
            frames.Add(TileProtocol.EncodePreparationTerminalChunk(new(TickCount, (ushort)i, (ushort)terminalChunks),
                terminals, i * 255, Math.Min(255, terminals.Count - i * 255)));
        foreach (byte[] frame in frames) net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
        return true;
    }

    void DisconnectPreparationOverflowViewers()
    {
        if (preparationOverflowViewers is not { Count: > 0 }) return;
        int[] slots = new int[preparationOverflowViewers.Count];
        preparationOverflowViewers.CopyTo(slots);
        preparationOverflowViewers.Clear();
        // Run after TickCount advances. Removing a participant can cancel a freshly scheduled successor, whose
        // terminal belongs to the next tick rather than preceding this tick's resolved older attack ID.
        foreach (int slot in slots) Kick(slot, TileServerReason.CombatPreparationOverflow);
    }

    bool RejectPreparationFrameSet(int slot)
    {
        RejectedCombatPreparationFrameSetCount++;
        if (servingPreparations)
        {
            preparationOverflowViewers ??= new();
            preparationOverflowViewers.Add(slot);
        }
        else Kick(slot, TileServerReason.CombatPreparationOverflow);
        return false;
    }
}
