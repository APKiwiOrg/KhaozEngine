using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The GUARDED freeze and its recorded-base release on the in-memory store. Each member completes under the one
/// gate, so the base check, the draft check, representability, the marker write and the returned copy are a single
/// step no other caller can interleave with.
/// <para>
/// <b>No stale sweep runs here.</b> The text freeze and the baseline read still clear a marker naming an older base,
/// but a refused guarded freeze must write nothing, a stale marker included. A successful one replaces it directly.
/// </para>
/// </summary>
public sealed partial class InMemoryContentAuthoringStore : IContentConditionalDraftFreeze
{
    /// <inheritdoc />
    public Task<ContentDraft> FreezeDraftForBaseAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBaseVersion);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (expectedBaseVersion != _activeVersion)
            {
                throw Moved(FormattableString.Invariant(
                    $"The publish expects base version {expectedBaseVersion} and the store stands at {_activeVersion}. Another publish landed in between, so this draft is against a version that is no longer the base."));
            }

            if (_draft is not ContentDraft open || open.EditCount == 0)
            {
                throw new ContentAuthoringException(
                    "There is no open draft with pending row edits, so there is nothing to publish.",
                    default,
                    0,
                    ContentAuthoringException.NoOpenDraftReason);
            }

            RequireRowOnlyRepresentable(open, nameof(FreezeDraftForBaseAsync));
            cancellationToken.ThrowIfCancellationRequested();

            _draft = Reframe(open, open.BaseVersion, open.Changes, expectedBaseVersion);
            return Task.FromResult(Protected(_draft));
        }
    }

    /// <inheritdoc />
    public Task<bool> ReleaseDraftFreezeForBaseAsync(int frozenForBaseVersion, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frozenForBaseVersion);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_draft is not { FrozenForBaseVersion: int frozen } held || frozen != frozenForBaseVersion)
            {
                return Task.FromResult(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _draft = Reframe(held, held.BaseVersion, held.Changes, null);
            return Task.FromResult(true);
        }
    }
}
