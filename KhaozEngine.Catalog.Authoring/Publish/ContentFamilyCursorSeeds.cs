using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>Seeds only the already reserved blocks that contain a new carried definition.</summary>
static class ContentFamilyCursorSeeds
{
    internal static async Task CommitAsync(
        IReadOnlyList<ContentCandidateRow> rows,
        IReadOnlyList<ContentFamily> families,
        IContentIdPersistence persistence,
        CancellationToken cancellationToken)
    {
        if (families.Count == 0)
        {
            return;
        }

        var byId = new Dictionary<long, ContentFamily>(families.Count);
        foreach (ContentFamily family in families)
        {
            byId.Add(family.FamilyId, family);
        }

        var seeds = new SortedDictionary<(long FamilyId, int Ordinal), (ContentTypeId Type, int Id)>();
        foreach (ContentCandidateRow row in rows)
        {
            if (!row.IsNewDefinition || row.NeedsId || row.FamilyId is not long familyId)
            {
                continue;
            }

            foreach (ContentFamilyBlock block in byId[familyId].Blocks)
            {
                int id = row.DefinitionId;
                if (!block.IsFull && id >= block.NextFreeId && block.Contains(id))
                {
                    var key = (familyId, block.BlockOrdinal);
                    if (!seeds.TryGetValue(key, out var seed) || id > seed.Id)
                    {
                        seeds[key] = (row.Registration.Type, id);
                    }
                }
            }
        }

        foreach (var seed in seeds)
        {
            await persistence.CommitFamilyCarriedThroughAsync(
                seed.Value.Type, seed.Key.FamilyId, seed.Key.Ordinal, seed.Value.Id, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
