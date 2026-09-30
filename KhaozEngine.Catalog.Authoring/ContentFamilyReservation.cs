using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>The aligned block and ceiling rule shared by creation and ordinary reservations.</summary>
static class ContentFamilyReservation
{
    internal static ContentFamilyBlock Plan(
        ContentFamily family,
        ContentIdHighWater mark,
        long ceiling,
        int reservedInVersion)
    {
        long baseId = ContentIdAllocator.NextBlockBase(mark, family.BlockSize);
        long topId = baseId + family.BlockSize - 1;
        if (topId > ceiling)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {family.Type.Value} declares an id ceiling of {ceiling}, so family '{family.FamilyKey}' cannot reserve the block [{baseId}, {topId + 1}) whose highest id is {topId}. The type has reserved through {mark.ReservedThrough} and issued through {mark.IssuedThrough}."),
                family.Type,
                0,
                ContentAuthoringException.IdCeilingReason);
        }

        return new ContentFamilyBlock(
            family.FamilyId, family.Blocks.Count, (int)baseId, family.BlockSize, (int)baseId, reservedInVersion);
    }
}
