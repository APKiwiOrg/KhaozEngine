using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Step 3 of spec 6.1, the ONE path with two id SOURCES, where which one runs is a property of the EDIT
/// rather than of the caller.
/// <para>
/// <b>It runs BEFORE validation, deliberately</b> (spec 6.3), because several checks need the ids the new
/// rows will carry: <c>KEC0006</c> resolves references and <c>KEC0010</c> asks about family membership, and
/// neither can be asked of a row whose id does not exist yet.
/// </para>
/// <para>
/// <b>An allocation that fails aborts the publish with nothing written</b>, because no durable row carries
/// the id yet. A reservation that COMMITTED and then aborted leaves a gap of reserved-but-unissued ids,
/// which is the safe direction and costs nothing.
/// </para>
/// </summary>
static class ContentIdAllocation
{
    /// <summary>
    /// Issues an id for every row that introduces a new definition, in edit ordinal order, then seeds the
    /// high-water marks from the ids that were CARRIED.
    /// </summary>
    /// <param name="rows">Every candidate row, in edit ordinal order for the ones that need an id.</param>
    /// <param name="allocator">The reserve-then-issue allocator.</param>
    /// <param name="persistence">The durable half, which the seeding writes through.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">An allocation crossed a type's declared id ceiling, or named a family the store does not hold.</exception>
    public static async Task<ContentIdAllocationRecord> RunAsync(
        IReadOnlyList<ContentCandidateRow> rows,
        ContentIdAllocator allocator,
        IContentIdPersistence persistence,
        CancellationToken cancellationToken)
    {
        var entries = new List<ContentIdAllocationEntry>();
        var carried = new Dictionary<ushort, int>();

        for (int i = 0; i < rows.Count; i++)
        {
            ContentCandidateRow row = rows[i];
            if (!row.IsNewDefinition)
            {
                continue;
            }

            ContentTypeId type = row.Registration.Type;
            if (!row.NeedsId)
            {
                // Branch 1: the edit CARRIED the id, which only a bulk import into an empty database
                // writes. Carrying it is what makes an adoption a no-op for stored player data, where
                // ordering alone would not preserve a single one.
                carried[type.Value] = carried.TryGetValue(type.Value, out int held)
                    ? Math.Max(held, row.DefinitionId)
                    : row.DefinitionId;
            }
            else if (row.FamilyId is long familyId && row.Source == ContentIdSource.Family)
            {
                // Branch 2: the edit names a family, so the id comes from the family's aligned blocks.
                row.DefinitionId = await allocator
                    .AllocateInFamilyAsync(familyId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Branch 3: the plain per-type counter. A FORK always lands here, because its copy takes
                // the next free id of the type and may not name a family of its own.
                row.DefinitionId = await allocator
                    .AllocateAsync(type, 1, cancellationToken).ConfigureAwait(false);
            }

            entries.Add(new ContentIdAllocationEntry(
                row.EditOrdinal, type, row.Key, row.DefinitionId, row.Source, row.FamilyId));
        }

        IReadOnlyList<ContentIdSeed> seeds = await SeedAsync(carried, persistence, cancellationToken)
            .ConfigureAwait(false);
        return new ContentIdAllocationRecord(entries, seeds);
    }

    /// <summary>
    /// Moves each type's two marks up to the largest CARRIED id of that type, after every add has an id
    /// (spec 6.3).
    /// <para>
    /// <b>Without it the first ordinary add after an import allocates id 1 straight onto an imported row.</b>
    /// The step is a no-op for an ordinary publish, because nothing carries an id there, and that is the
    /// property a test pins rather than one the step arrives at quietly.
    /// </para>
    /// </summary>
    static async Task<IReadOnlyList<ContentIdSeed>> SeedAsync(
        Dictionary<ushort, int> carried,
        IContentIdPersistence persistence,
        CancellationToken cancellationToken)
    {
        if (carried.Count == 0)
        {
            return [];
        }

        var seeds = new List<ContentIdSeed>(carried.Count);
        foreach (ushort typeId in Ascending(carried.Keys))
        {
            var type = new ContentTypeId(typeId);
            int highest = carried[typeId];
            ContentIdHighWater mark = await persistence
                .ReadHighWaterAsync(type, cancellationToken).ConfigureAwait(false);
            if (mark.ReservedThrough >= highest && mark.IssuedThrough >= highest)
            {
                continue;
            }

            // Reserve before issue, here as everywhere: the promise lands on its own commit first.
            if (mark.ReservedThrough < highest)
            {
                await persistence
                    .CommitReservedThroughAsync(type, highest, cancellationToken).ConfigureAwait(false);
            }

            if (mark.IssuedThrough < highest)
            {
                await persistence
                    .CommitIssuedThroughAsync(type, highest, cancellationToken).ConfigureAwait(false);
            }

            seeds.Add(new ContentIdSeed(type, highest));
        }

        return seeds;
    }

    /// <summary>The type ids in ascending order, so two runs of one import seed in the same order.</summary>
    static ushort[] Ascending(IEnumerable<ushort> source)
    {
        var ordered = new List<ushort>(source);
        ordered.Sort();
        return ordered.ToArray();
    }
}
