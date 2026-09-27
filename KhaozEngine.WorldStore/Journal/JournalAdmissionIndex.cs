using System;
using System.Collections.Generic;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>
/// Admission-order indexes for the executor's live gauges. Every mutation runs under the executor gate. The all
/// and uncommitted lists share the operation sequence, while retained nodes make completion and acknowledgement
/// removal constant work even when those transitions arrive out of order.
/// </summary>
internal sealed class JournalAdmissionIndex
{
    private readonly LinkedList<AdmittedJournalOperation> admitted = new();
    private readonly LinkedList<AdmittedJournalOperation> uncommitted = new();

    internal void Admit(AdmittedJournalOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.AdmissionNode is not null || operation.UncommittedNode is not null)
            throw new InvalidOperationException("The journal operation is already indexed.");
        if (admitted.Last is { } last && last.Value.Sequence >= operation.Sequence)
            throw new InvalidOperationException("Journal operations must enter the admission index in sequence order.");
        operation.AdmissionNode = admitted.AddLast(operation);
        operation.UncommittedNode = uncommitted.AddLast(operation);
    }

    internal void Complete(AdmittedJournalOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        LinkedListNode<AdmittedJournalOperation> node = operation.UncommittedNode
            ?? throw new InvalidOperationException("The journal operation is no longer uncommitted.");
        uncommitted.Remove(node);
        operation.UncommittedNode = null;
    }

    internal void Acknowledge(AdmittedJournalOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.UncommittedNode is not null)
            throw new InvalidOperationException("An uncommitted journal operation cannot be acknowledged.");
        LinkedListNode<AdmittedJournalOperation> node = operation.AdmissionNode
            ?? throw new InvalidOperationException("The journal operation is no longer admitted.");
        admitted.Remove(node);
        operation.AdmissionNode = null;
    }

    internal JournalAdmissionGaugeSnapshot Snapshot()
    {
        int inspected = 0;
        DateTimeOffset? oldestAdmission = null;
        DateTimeOffset? oldestUncommitted = null;
        if (admitted.First is { } admittedFirst)
        {
            oldestAdmission = admittedFirst.Value.AdmittedAtUtc;
            inspected++;
        }
        if (uncommitted.First is { } uncommittedFirst)
        {
            oldestUncommitted = uncommittedFirst.Value.AdmittedAtUtc;
            inspected++;
        }
        return new JournalAdmissionGaugeSnapshot(
            admitted.Count,
            uncommitted.Count,
            oldestAdmission,
            oldestUncommitted,
            inspected);
    }

    internal Guid[] OperationIdsInOrder()
    {
        var ids = new Guid[admitted.Count];
        int index = 0;
        for (LinkedListNode<AdmittedJournalOperation>? node = admitted.First; node is not null; node = node.Next)
            ids[index++] = node.Value.Commit.Identity.OperationId;
        return ids;
    }
}

internal readonly record struct JournalAdmissionGaugeSnapshot(
    long AdmittedOperations,
    long UncommittedOperations,
    DateTimeOffset? OldestAdmission,
    DateTimeOffset? OldestUncommitted,
    int InspectedOperations);
