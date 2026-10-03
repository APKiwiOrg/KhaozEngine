using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Prepares a complete <see cref="ContentTextPublishPlan"/> from a snapshot the companion already froze. It
/// writes nothing durable: like steps 1 to 8 of a row publish, a plan dropped here leaves the store as found.
/// <para>
/// <b>Two phases, ids once.</b> Phase one is the ordinary publisher's steps 2 to 7 over the frozen row
/// snapshot, which allocates every new id exactly once and builds the row and rule chunks. The candidate and
/// chunk builders then run on those final ids, Fork copies included. Phase two encodes both manifests once,
/// naming the complete output language list, so the row plan's languages equal the text chunks by
/// construction rather than by a second build. Text-only work prepares with zero row edits.
/// </para>
/// </summary>
internal sealed class ContentTextPublisher
{
    readonly ContentPublisher _publisher;

    /// <summary>Builds a text publisher over the row pipeline it shares registry, ids and hooks with.</summary>
    /// <param name="publisher">The row pipeline.</param>
    public ContentTextPublisher(ContentPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    /// <summary>Prepares the complete plan for one frozen snapshot.</summary>
    /// <param name="request">The publish request.</param>
    /// <param name="snapshot">The snapshot the companion froze for the request's base version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The base moved, the candidate did not validate, or the text is ineligible or out of bounds.</exception>
    public async Task<ContentTextPublishPlan> PrepareAsync(
        ContentPublishRequest request,
        ContentTextPublishSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (request.ExpectedBaseVersion != snapshot.BaseVersion)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The publish expects base version {request.ExpectedBaseVersion} and the snapshot was frozen for {snapshot.BaseVersion}."),
                default,
                0,
                ContentAuthoringException.BaseVersionMovedReason);
        }

        // PHASE ONE. Ids, candidate, validation, row and rule chunks, once.
        ContentPublishRowPhase rows = await _publisher
            .PrepareRowsAsync(request, snapshot.Baseline, snapshot.Draft, cancellationToken).ConfigureAwait(false);
        if (!rows.IsValid)
        {
            throw ContentPublishCommit.Invalid(rows.Refusal!);
        }

        // The text, bound to phase one's final ids and preflighted before any chunk is encoded.
        ContentTypeRegistry registry = _publisher.Registry;
        ContentTextCandidate candidate = ContentTextCandidateBuilder.Build(registry, snapshot, rows);
        IReadOnlyList<ContentTextChunkRecord> chunks = ContentTextChunkBuilder.Build(
            registry, candidate, rows.LiveRows, snapshot.BaselineText);
        var languages = new ManifestLanguageEntry[chunks.Count];
        for (int i = 0; i < languages.Length; i++)
        {
            languages[i] = new ManifestLanguageEntry(chunks[i].WireTag, chunks[i].Hash);
        }

        // PHASE TWO. Both manifests, once, over the complete language list.
        ContentPublishPlan rowPlan = rows.Complete(languages);
        return new ContentTextPublishPlan(rowPlan, snapshot, candidate, chunks);
    }
}
