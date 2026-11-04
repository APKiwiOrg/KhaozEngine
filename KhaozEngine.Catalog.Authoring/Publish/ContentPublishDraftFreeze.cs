using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>The row-only draft freeze and the policy's read under that freeze.</summary>
internal static class ContentPublishDraftFreeze
{
    /// <summary>
    /// Step 1. The draft is marked FROZEN for the whole publish, so a draft edit or a discard arriving while
    /// one is in flight is refused with
    /// <see cref="ContentAuthoringException.PublishInProgressReason"/> rather than half applied.
    /// <para>
    /// <b>The marker is durable and it is not a lock.</b> Steps 1 to 10 span step 9's pack writes, and no
    /// provider here holds a row lock across those: SQLite leases its one connection per call and SQL
    /// Server's Serializable transaction covers step 10 alone. So the store writes
    /// <c>catalog_draft.frozen_for_base_version</c> under its own transaction and every draft write reads it.
    /// A successful commit consumes it, and <see cref="ContentPublishCommit"/> scopes a failed attempt's
    /// release to its base when the store supplies <see cref="IContentDraftFreezeStore"/>. A stale marker
    /// naming a base version the store no longer stands at is cleared by the next baseline read.
    /// </para>
    /// <para>
    /// <see cref="ContentPublishRequest.ExpectedBaseVersion"/> is optimistic concurrency and it is checked
    /// against the BASELINE, which the commit read. Two consoles cannot both publish the same draft: the
    /// second one's expectation is stale and it is refused with both numbers named. That check runs BEFORE
    /// the marker is written, so a publish refused for a stale expectation freezes nothing.
    /// </para>
    /// </summary>
    internal static async Task<ContentDraft> FreezeAsync(
        IContentAuthoringStore store,
        ContentPublishRequest request,
        ContentPublishBaseline baseline,
        CancellationToken cancellationToken)
    {
        if (request.ExpectedBaseVersion != baseline.VersionNumber)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The publish expects base version {request.ExpectedBaseVersion} and the store stands at {baseline.VersionNumber}. Another publish landed in between, so this draft is against a version that is no longer the base."),
                default,
                0,
                ContentAuthoringException.BaseVersionMovedReason);
        }

        ContentDraft? draft = await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        if (draft is null || draft.EditCount == 0)
        {
            throw new ContentAuthoringException(
                "There is no open draft with pending edits, so there is nothing to publish. A version is a set of changes and an empty one would be a number with no content behind it.",
                default,
                0,
                ContentAuthoringException.NoOpenDraftReason);
        }

        await store.FreezeDraftAsync(baseline.VersionNumber, cancellationToken).ConfigureAwait(false);
        if (request.PublishPolicy is null)
        {
            return draft;
        }

        // Legacy row-only providers freeze separately from their initial draft read. A policy must inspect
        // the changes held by the marker, including an edit that arrived between that read and the freeze.
        ContentDraft? frozen = await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        if (frozen is null || frozen.FrozenForBaseVersion != baseline.VersionNumber)
        {
            throw new ContentAuthoringException("The policy publish no longer holds its frozen draft.",
                default, 0, ContentAuthoringException.BaseVersionMovedReason);
        }

        return frozen;
    }

}
