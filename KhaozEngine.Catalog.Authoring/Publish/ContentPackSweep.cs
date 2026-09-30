using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What step 11 did, or why it did nothing. A sweep that SKIPPED is not a failure and is not silent either:
/// the reason is on the result, because deleting nothing and deleting everything are one keystroke apart and
/// an operator reading a publish response deserves to know which happened.
/// </summary>
/// <param name="Ran">True when the store was actually pruned.</param>
/// <param name="Kept">How many distinct hashes the keep set held, 0 on a skip.</param>
/// <param name="Deleted">How many orphan objects were deleted.</param>
/// <param name="SkipReason">Why the sweep did not run, or null when it did.</param>
public sealed record ContentPackSweepResult(bool Ran, int Kept, int Deleted, string? SkipReason);

/// <summary>
/// Step 11 of spec 6.1, the orphan sweep, which runs after a SUCCESSFUL commit and never before one.
/// <para>
/// <b>The keep set is the union, over EVERY version the store knows, of that version's pointer, the two
/// manifest hashes it holds, and every hash named INSIDE either manifest.</b> It is defined against the
/// MANIFESTS and not against the chunk table, because the manifest is the complete enumeration and the chunk
/// table is not: the remap rule chunk sits at a reserved address outside any type's id space and the text
/// chunks are per language, so neither has a type row to hang a chunk row on, while both are named by both
/// manifests. A keep set read from the chunk table would delete the rule chunk and every text chunk at the
/// first publish, and every later boot would fail closed on an absent chunk, for every version, forever.
/// </para>
/// <para>
/// <b>It is SKIPPED when the store listing fails for any reason</b>. The validated entry point also
/// requires every pointer's two hashes to match its durable record before any listing starts. An absent,
/// unreadable or mismatched pointer is a listing failure. Deleting files on the authority of a listing that
/// failed is how a bad publish turns into a lost pack.
/// </para>
/// <para>
/// It never deletes a file referenced by ANY version, not just the active one, because a pinned server and a
/// rollback both need older versions to stay fetchable. A version POINTER is never a candidate either: a
/// pointer is how the next sweep finds its version again, and it lives outside the content-addressed tree
/// the orphan half enumerates.
/// </para>
/// </summary>
public static class ContentPackSweep
{
    /// <summary>The store could not enumerate one version, so the keep set is incomplete and nothing is deleted.</summary>
    public const string SkippedListingFailed = "listing-failed";

    /// <summary>The store implements no pruning half, so there is nothing to sweep with.</summary>
    public const string SkippedNoPruning = "store-cannot-prune";

    /// <summary>
    /// Checks every pointer against its durable version record before listing or pruning any objects.
    /// Missing pointer evidence or either mismatched manifest hash skips the sweep as a listing failure.
    /// The publish and operator sweep use this entry point.
    /// </summary>
    /// <param name="store">The pack store to sweep, with readable version pointers.</param>
    /// <param name="versions">Every durable version record the authoring store knows, the new one included.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<ContentPackSweepResult> RunValidatedAsync(
        IPackStore store,
        IReadOnlyList<ContentVersionRecord> versions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(versions);

        if (store is not IPackStorePruning)
        {
            return new ContentPackSweepResult(false, 0, 0, SkippedNoPruning);
        }

        IPackVersionPointerStore? pointers = PackVersionPointers.Resolve(store);
        IContentVersionPointerSource? source = store as IContentVersionPointerSource;
        if (pointers is null && source is null)
        {
            return new ContentPackSweepResult(false, 0, 0, SkippedListingFailed);
        }

        var numbers = new List<int>(versions.Count);
        for (int i = 0; i < versions.Count; i++)
        {
            ContentVersionRecord version = versions[i];
            PackVersionPointer? pointer = pointers is not null
                ? await pointers.GetVersionPointerAsync(version.VersionNumber, cancellationToken).ConfigureAwait(false)
                : await source!.GetVersionPointerAsync(version.VersionNumber, cancellationToken).ConfigureAwait(false);
            if (pointer is null
                || !string.Equals(pointer.ServerManifestHash, version.ServerManifestHash, StringComparison.Ordinal)
                || !string.Equals(pointer.ClientManifestHash, version.ClientManifestHash, StringComparison.Ordinal))
            {
                return new ContentPackSweepResult(false, 0, 0, SkippedListingFailed);
            }

            numbers.Add(version.VersionNumber);
        }

        return await RunAsync(store, numbers, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes every object outside the keep set, or skips and says why.
    /// This compatibility entry point trusts the pointer-backed listings without durable version records.
    /// Its caller must establish that the pointers name the authoritative manifests. Use
    /// <see cref="RunValidatedAsync"/> when those records are available, as the publish and operator sweep do.
    /// </summary>
    /// <param name="store">The pack store to sweep.</param>
    /// <param name="versions">Every version number the store knows, the new one included.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<ContentPackSweepResult> RunAsync(
        IPackStore store,
        IReadOnlyList<int> versions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(versions);

        if (store is not IPackStorePruning pruning)
        {
            return new ContentPackSweepResult(false, 0, 0, SkippedNoPruning);
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < versions.Count; i++)
        {
            int named = 0;
            await foreach (string hash in store.ListAsync(versions[i], cancellationToken).ConfigureAwait(false))
            {
                keep.Add(hash);
                named++;
            }

            if (named == 0)
            {
                // An empty listing is how every provider reports a failure: an absent pointer, an unreadable
                // one, and a manifest the pointer names that could not be read all answer this way. A
                // published version always names at least its own two manifests.
                return new ContentPackSweepResult(false, keep.Count, 0, SkippedListingFailed);
            }
        }

        var orphans = new List<string>();
        await foreach (string hash in pruning.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!keep.Contains(hash))
            {
                orphans.Add(hash);
            }
        }

        int deleted = 0;
        for (int i = 0; i < orphans.Count; i++)
        {
            if (await pruning.DeleteAsync(orphans[i], cancellationToken).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return new ContentPackSweepResult(true, keep.Count, deleted, null);
    }
}
