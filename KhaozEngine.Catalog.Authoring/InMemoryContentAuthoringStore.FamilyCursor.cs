using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

public sealed partial class InMemoryContentAuthoringStore
{
    /// <inheritdoc />
    public Task CommitFamilyCarriedThroughAsync(
        ContentTypeId type,
        long familyId,
        int blockOrdinal,
        int carriedId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_families.TryGetValue(familyId, out FamilyRecord? family)
                && family.Type == type
                && (uint)blockOrdinal < family.Blocks.Count)
            {
                ContentFamilyBlock block = family.Blocks[blockOrdinal];
                if (!block.IsFull && block.Contains(carriedId) && block.NextFreeId <= carriedId)
                {
                    family.Blocks[blockOrdinal] = block with { NextFreeId = checked(carriedId + 1) };
                }
            }
        }

        return Task.CompletedTask;
    }
}
