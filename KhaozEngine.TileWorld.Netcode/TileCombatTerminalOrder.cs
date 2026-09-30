using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Transition order for one bounded set of validated terminals. Keyed lookups never reorder its results.</summary>
internal sealed class TileCombatTerminalOrder
{
    readonly int recordLimit;
    readonly SortedDictionary<long, ulong> lastAttackByAttacker = new();
    int records;
    bool resolved;
    bool cancelled;
    long lastCancelled;

    internal TileCombatTerminalOrder(int recordLimit)
    {
        if (recordLimit is < 1 or > TileProtocol.MaxPreparationRecords)
            throw new ArgumentOutOfRangeException(nameof(recordLimit));
        this.recordLimit = recordLimit;
    }

    internal bool TryAdd(in TileCombatTerminal record)
    {
        if (records >= recordLimit) return false;
        bool cancellation = record.Kind == TileCombatTerminalKind.Cancelled;
        if (!cancellation && record.Kind != TileCombatTerminalKind.Resolved) return false;
        if (cancellation && (resolved || (cancelled && record.AttackerNetId < lastCancelled))) return false;
        // A terminal consumes its attack ID, regardless of revision or category. Keeping one high-water value
        // per actor catches both reversed cancellations and an attempt that is cancelled and then resolved.
        if (lastAttackByAttacker.TryGetValue(record.AttackerNetId, out ulong previous) && record.AttackId <= previous)
            return false;

        lastAttackByAttacker[record.AttackerNetId] = record.AttackId;
        records++;
        if (cancellation)
        {
            cancelled = true;
            lastCancelled = record.AttackerNetId;
        }
        else resolved = true;
        return true;
    }

    internal void Clear()
    {
        lastAttackByAttacker.Clear();
        records = 0;
        resolved = false;
        cancelled = false;
        lastCancelled = 0;
    }
}
