using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One reserved, ALIGNED id block of one family (contracts 5.2). A block holding
/// <c>[BaseId, BaseId + BlockSize)</c> has <c>BaseId % BlockSize == 0</c>, and the alignment is what makes
/// membership <c>(id &amp; ~(size - 1)) == base</c> rather than a set lookup.
/// <para>
/// Block boundaries do NOT have to align to chunk boundaries, and nothing assumes they do. A chunk is a
/// transport and hashing unit sized for download economics. A family is an authoring and gameplay unit
/// sized for how many swords there will be. Coupling them would force one of the two to be the wrong size.
/// </para>
/// </summary>
/// <param name="FamilyId">The family this block belongs to.</param>
/// <param name="BlockOrdinal">The block's position in the family's ordered list, from 0.</param>
/// <param name="BaseId">The first id in the block, a multiple of <paramref name="BlockSize"/>.</param>
/// <param name="BlockSize">The block's size, a power of two between 16 and 65,536.</param>
/// <param name="NextFreeId">The next id to issue, which reaches <see cref="TopExclusive"/> when the block fills.</param>
/// <param name="ReservedInVersion">The version the block was reserved in.</param>
public readonly record struct ContentFamilyBlock(
    long FamilyId,
    int BlockOrdinal,
    int BaseId,
    int BlockSize,
    int NextFreeId,
    int ReservedInVersion)
{
    /// <summary>One past the last id in the block.</summary>
    public int TopExclusive => BaseId + BlockSize;

    /// <summary>True when every id in the block has been issued, so the family needs a new one.</summary>
    public bool IsFull => NextFreeId >= TopExclusive;

    /// <summary>The two-comparison membership test of contracts 5.2, over the block's alignment.</summary>
    /// <param name="id">The definition id to test.</param>
    public bool Contains(int id) => (id & ~(BlockSize - 1)) == BaseId;
}

/// <summary>
/// An author-declared grouping within ONE content type whose members are allocated ids from contiguous
/// aligned blocks (contracts 5.2), so a runtime check "is this id in the sword family" is two comparisons
/// per block rather than a set lookup.
/// <para>
/// A family reserves a block at creation and its block size is declared then, is a power of two between 16
/// and 65,536, and cannot be changed later, because changing it would move every id in the family. When a
/// block fills a SECOND block is reserved for the same family and the family carries an ORDERED block list,
/// so membership is a short loop. Growing a block in place is impossible once the next one is allocated,
/// and reserving a huge block up front wastes the dense-array runtime the server depends on.
/// </para>
/// <para>
/// A family is never deleted. It is RETIRED like a definition.
/// </para>
/// </summary>
public sealed class ContentFamily
{
    /// <summary>The smallest legal declared block size, contracts 5.2.</summary>
    public const int MinBlockSize = 16;

    /// <summary>The largest legal declared block size, contracts 5.2.</summary>
    public const int MaxBlockSize = 65536;

    readonly ContentFamilyBlock[] _blocks;

    /// <summary>Builds a family and its ordered block list.</summary>
    /// <param name="familyId">The store's own family id.</param>
    /// <param name="type">The content type the family groups rows of.</param>
    /// <param name="familyKey">The family's key, unique within its type, at most 64 characters.</param>
    /// <param name="blockSize">The declared block size, fixed at creation.</param>
    /// <param name="isRetired">Whether the family is retired.</param>
    /// <param name="createdInVersion">The version it was created in.</param>
    /// <param name="blocks">The reserved blocks, in ordinal order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="familyKey"/> or <paramref name="blocks"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="familyKey"/> is blank, or a block's size or alignment disagrees with the family.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockSize"/> is not a power of two in range.</exception>
    public ContentFamily(
        long familyId,
        ContentTypeId type,
        string familyKey,
        int blockSize,
        bool isRetired,
        int createdInVersion,
        IReadOnlyList<ContentFamilyBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(familyKey);
        ArgumentNullException.ThrowIfNull(blocks);

        if (string.IsNullOrWhiteSpace(familyKey))
        {
            throw new ArgumentException("A family names a key.", nameof(familyKey));
        }

        if (!IsLegalBlockSize(blockSize))
        {
            throw new ArgumentOutOfRangeException(
                nameof(blockSize),
                blockSize,
                FormattableString.Invariant(
                    $"A family's block size is a power of two between {MinBlockSize} and {MaxBlockSize}."));
        }

        var copy = new ContentFamilyBlock[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            ContentFamilyBlock block = blocks[i];
            if (block.BlockSize != blockSize)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Block {block.BlockOrdinal} is sized {block.BlockSize}, and a family's block size is fixed at {blockSize}."),
                    nameof(blocks));
            }

            if (block.BaseId % blockSize != 0)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Block {block.BlockOrdinal} is based at {block.BaseId}, which is not aligned to {blockSize}, so the membership test would be wrong."),
                    nameof(blocks));
            }

            copy[i] = block;
        }

        FamilyId = familyId;
        Type = type;
        FamilyKey = familyKey;
        BlockSize = blockSize;
        IsRetired = isRetired;
        CreatedInVersion = createdInVersion;
        _blocks = copy;
    }

    /// <summary>The store's own family id.</summary>
    public long FamilyId { get; }

    /// <summary>The content type the family groups rows of. A family never spans two types.</summary>
    public ContentTypeId Type { get; }

    /// <summary>The family's key, unique within its type.</summary>
    public string FamilyKey { get; }

    /// <summary>The declared block size, fixed at creation and never changed.</summary>
    public int BlockSize { get; }

    /// <summary>Whether the family is retired. A family is never deleted.</summary>
    public bool IsRetired { get; }

    /// <summary>The version the family was created in.</summary>
    public int CreatedInVersion { get; }

    /// <summary>The reserved blocks in ORDINAL order, which is the order the allocator walks them in.</summary>
    public IReadOnlyList<ContentFamilyBlock> Blocks => _blocks;

    /// <summary>
    /// The membership test of contracts 5.2: a short loop over the block list, two comparisons per block,
    /// rather than a set lookup. The runtime caches the list, so the cost is the loop and nothing else.
    /// </summary>
    /// <param name="id">The definition id to test.</param>
    public bool Contains(int id)
    {
        for (int i = 0; i < _blocks.Length; i++)
        {
            if (_blocks[i].Contains(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A power of two between <see cref="MinBlockSize"/> and <see cref="MaxBlockSize"/>.</summary>
    /// <param name="blockSize">The candidate size.</param>
    public static bool IsLegalBlockSize(int blockSize)
        => blockSize >= MinBlockSize
            && blockSize <= MaxBlockSize
            && (blockSize & (blockSize - 1)) == 0;
}
