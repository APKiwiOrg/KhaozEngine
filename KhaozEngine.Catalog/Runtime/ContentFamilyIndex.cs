using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// One family's contiguous, self-aligned id block (spec 3.8, contracts 5.2), so a membership test is two
/// comparisons rather than a set lookup.
/// </summary>
/// <param name="Type">The content type whose id space the block is carved from.</param>
/// <param name="Base">The block's first id, aligned to <paramref name="Size"/>.</param>
/// <param name="Size">The block's width, a power of two between 16 and 65,536.</param>
public readonly record struct ContentIdBlock(ContentTypeId Type, int Base, int Size)
{
    /// <summary>The smallest block a family may declare.</summary>
    public const int MinSize = 16;

    /// <summary>The largest block a family may declare.</summary>
    public const int MaxSize = 65_536;

    /// <summary>The whole membership test: one mask and one comparison, contracts 5.2.</summary>
    public bool Contains(int id) => (id & ~(Size - 1)) == Base;

    /// <summary>The id one past the block's last, which is <c>Base + Size</c>.</summary>
    public int Top => Base + Size;

    /// <summary>True when the block obeys the power-of-two size and self-alignment rules of spec 3.8.</summary>
    public bool IsWellFormed => Size >= MinSize
        && Size <= MaxSize
        && (Size & (Size - 1)) == 0
        && Base >= 0
        && (Base & (Size - 1)) == 0;
}

/// <summary>
/// The family membership index of spec 9.4, third of the engine's four: the block list per family, cached, so
/// the two-comparison test of contracts 5.2 runs against a short loop rather than a store read.
/// <para>
/// <b>A pack carries no family declarations, so a version loaded from one indexes an EMPTY block list.</b> A
/// family and its blocks are AUTHORING rows (<c>catalog_family</c> and <c>catalog_family_block</c>, spec 4.3
/// and 4.7) and a row's claim on one is authoring data too. Neither travels in a chunk or a manifest (spec 7),
/// so the read side has nothing to derive a block from: an id is just an id here, and the clustering a family
/// leaves in the id space is indistinguishable from an ordinary sparse run. That is the same gap that leaves
/// the validator's <c>KEC0010</c> to <c>KEC0012</c> and <c>KEC0037</c> quiet in phase 1, and it is filed as
/// https://github.com/APKiwiOrg/KhaozEngine/issues/934.
/// </para>
/// <para>
/// So the index is built from DECLARED blocks through <see cref="FromBlocks"/> and nothing is inferred. The
/// membership test, the block list and the lookup are all here and tested, waiting on a source: when the
/// declarations reach the read side, the pack path hands them to the same constructor an authoring-side
/// caller does today, and nothing else about this type changes.
/// </para>
/// </summary>
public sealed class ContentFamilyIndex
{
    /// <summary>The index of a version with no declared family, which is every published pack in phase 1.</summary>
    public static readonly ContentFamilyIndex Empty = new([]);

    readonly ContentIdBlock[] _blocks;

    ContentFamilyIndex(ContentIdBlock[] blocks) => _blocks = blocks;

    /// <summary>Every declared block, ordered by type then by base id.</summary>
    public IReadOnlyList<ContentIdBlock> Blocks => _blocks;

    /// <summary>
    /// The index over a declared block list. The blocks are COPIED and ordered, so the caller's list is its
    /// own afterwards and two callers handing in the same blocks in different orders get the same index.
    /// </summary>
    /// <param name="blocks">The declared blocks, which an authoring store or a later pack field supplies.</param>
    /// <exception cref="ArgumentNullException"><paramref name="blocks"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A block is not a power of two between 16 and 65,536, or is not aligned to its own size, which would
    /// make the two-comparison membership test wrong rather than merely unhelpful.
    /// </exception>
    public static ContentFamilyIndex FromBlocks(IReadOnlyList<ContentIdBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
        {
            return Empty;
        }

        var copy = new ContentIdBlock[blocks.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            ContentIdBlock block = blocks[i];
            if (!block.IsWellFormed)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Family block [{block.Base}, {block.Top}) of type {block.Type.Value} is not a power-of-two block between {ContentIdBlock.MinSize} and {ContentIdBlock.MaxSize} aligned to its own size, so (id & ~(size - 1)) == base would not be a membership test for it."),
                    nameof(blocks));
            }

            copy[i] = block;
        }

        Array.Sort(copy, static (left, right) => left.Type.Value == right.Type.Value
            ? left.Base.CompareTo(right.Base)
            : left.Type.Value.CompareTo(right.Type.Value));

        return new ContentFamilyIndex(copy);
    }

    /// <summary>The block one id of one type falls in, walked as the short loop spec 3.8 describes.</summary>
    public bool TryGetBlock(ContentTypeId type, int id, out ContentIdBlock block)
    {
        for (int i = 0; i < _blocks.Length; i++)
        {
            ContentIdBlock candidate = _blocks[i];
            if (candidate.Type == type && candidate.Contains(id))
            {
                block = candidate;
                return true;
            }
        }

        block = default;
        return false;
    }

    /// <summary>True when an id of a type falls inside any declared block of that type.</summary>
    public bool IsInAnyBlock(ContentTypeId type, int id) => TryGetBlock(type, id, out _);

    /// <summary>The managed bytes this index holds, for the memory line of spec 9.2.</summary>
    public long ApproximateBytes() => (long)_blocks.Length * 12;

    /// <summary>
    /// The load-time build, which is EMPTY for a pack: the read side has no source for a family declaration,
    /// and inferring one from the shape of the id space would invent families nobody authored.
    /// </summary>
    internal static ContentFamilyIndex Build(ContentRuntime runtime)
    {
        _ = runtime;
        return Empty;
    }
}
