using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>Resolves known pre-upgrade client crafts without weakening journal fingerprint validation.</summary>
public static class ContainerCraftReplay
{
    /// <summary>Resolves legacy craft evidence with maximum journal limits.</summary>
    public static Task<ContainerCraftReplayResult> ResolveLegacyAsync(IMutationJournalStore store,
        string streamKey, JournalOperationIdentity requestedIdentity, CancellationToken cancellationToken = default)
        => ResolveLegacyAsync(store, streamKey, requestedIdentity, JournalLimits.Maximum, cancellationToken);

    /// <summary>
    /// Checks a versioned single client craft identity against its legacy fingerprint and the retained
    /// head craft event in the receipt's named stream. Schema 1 audit bodies and schema 2 envelopes provide
    /// plan evidence. Missing or compacted evidence returns no receipt. This method never submits a mutation.
    /// </summary>
    /// <remarks>
    /// Call this explicitly for a known pre-upgrade client operation. A fingerprint mismatch alone does not
    /// authorize fallback. Server batch identities need evidence for their entire ordered operation list
    /// and are not accepted here. Store failures and cancellation propagate to the caller.
    /// Pass the store's configured limits. The evidence read uses the smaller of its event payload and
    /// aggregate event read budgets. A permitted budget that cannot fit the evidence returns no receipt.
    /// </remarks>
    public static async Task<ContainerCraftReplayResult> ResolveLegacyAsync(IMutationJournalStore store,
        string streamKey, JournalOperationIdentity requestedIdentity, JournalLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(requestedIdentity);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentException.ThrowIfNullOrEmpty(streamKey);
        if (!ContainerOperationIntent.TryReadCraft(requestedIdentity.NormalizedIntent,
            out ContainerOperation requested, out int planId, out ReadOnlyMemory<byte> legacyCanonical))
            throw new ArgumentException("Legacy craft resolution requires one versioned client craft intent.",
                nameof(requestedIdentity));

        var legacyIdentity = new JournalOperationIdentity(requestedIdentity.OperationId,
            requestedIdentity.AuthenticatedScope, requestedIdentity.ActionKind, legacyCanonical.ToArray());
        JournalOperationResolution resolution = await store.ResolveOperationAsync(legacyIdentity,
            cancellationToken).ConfigureAwait(false);
        if (resolution.Status == JournalOperationResolutionStatus.NotFound)
            return new(ContainerCraftReplayStatus.NotFound);
        if (resolution.Status == JournalOperationResolutionStatus.OperationConflict)
            return new(ContainerCraftReplayStatus.OperationConflict);

        JournalCommitReceipt receipt = resolution.Receipt!;
        JournalStreamVersionRange? range = null;
        foreach (JournalStreamVersionRange candidate in receipt.Streams)
            if (StringComparer.Ordinal.Equals(candidate.StreamKey, streamKey)) range = candidate;
        if (range is null || range.EventCount == 0) return Unavailable();

        long headEventVersion = checked(range.BeforeVersion + 1);
        JournalEventPage page = await store.ReadEventsAsync(new JournalEventRead(streamKey,
            range.BeforeVersion, headEventVersion, 1, Math.Min(limits.EventPayloadBytes, limits.AggregateEventReadBytes)),
            cancellationToken).ConfigureAwait(false);
        if (page.Status != JournalEventPageStatus.Success || page.Events.Count != 1) return Unavailable();
        JournalStoredEvent stored = page.Events[0];
        if (stored.OperationId != requestedIdentity.OperationId || stored.StreamVersion != headEventVersion
            || !stored.HasValidChecksum || !StringComparer.Ordinal.Equals(stored.EventType, ItemInstanceEvents.Crafted))
            return Unavailable();

        ReadOnlyMemory<byte> auditBody;
        if (stored.EventSchemaVersion == 1)
        {
            auditBody = stored.Payload;
        }
        else if (stored.EventSchemaVersion == 2)
        {
            if (!ContainerOperationEventCodec.TryRead(stored.EventType, stored.EventSchemaVersion,
                stored.Payload, out ContainerOperation historical, out _)
                || !historical.ToCanonicalArray().AsSpan().SequenceEqual(legacyCanonical.Span)) return Unavailable();
            auditBody = historical.EventPayload;
        }
        else
        {
            return Unavailable();
        }

        if (!ItemCraftedEvent.TryRead(auditBody, out ItemCraftedEvent audit, out _)
            || audit.InstanceId != requested.InstanceId) return Unavailable();
        return audit.CurrencyId == planId
            ? new(ContainerCraftReplayStatus.Replayed, receipt)
            : new(ContainerCraftReplayStatus.OperationConflict);
    }

    static ContainerCraftReplayResult Unavailable() => new(ContainerCraftReplayStatus.EvidenceUnavailable);
}
