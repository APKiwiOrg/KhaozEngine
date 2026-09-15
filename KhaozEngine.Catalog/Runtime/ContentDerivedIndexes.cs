using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// The four indexes the ENGINE derives at load, spec 9.4, in the order it derives them:
/// <list type="number">
/// <item><description>Key to id, per type, which is <c>ContentTypeTable.KeyIds</c> rather than a structure
/// held here, because it is keyed on a slice of that table's own blob. Read through
/// <see cref="ContentRuntime.TryGetId(ContentTypeId, ContentKey, out int)"/>.</description></item>
/// <item><description><see cref="Tags"/>, tag id to sorted row ids, per type.</description></item>
/// <item><description><see cref="Families"/>, the block list per family.</description></item>
/// <item><description><see cref="Loot"/>, per loot table the resolved entries with the weights prefix
/// summed.</description></item>
/// </list>
/// <para>
/// <b>All four are built EAGERLY and none is built lazily</b>, because each is walked inside gameplay and a
/// lazy build inside a tick is the latency spike this whole section exists to refuse. They are built once,
/// immutable after, and rebuilt wholesale beside the old ones when a version changes, which is the same
/// volatile swap as spec 9.7.
/// </para>
/// <para>
/// The order is load bearing in one direction only: <see cref="Loot"/> resolves a tag-filtered entry through
/// <see cref="Tags"/>, so the tag index is built first. Nothing reads in the other direction.
/// </para>
/// </summary>
public sealed class ContentDerivedIndexes
{
    /// <summary>The indexes of a version carrying nothing to index.</summary>
    public static readonly ContentDerivedIndexes Empty =
        new(ContentTagIndex.Empty, ContentFamilyIndex.Empty, ContentLootIndex.Empty);

    ContentDerivedIndexes(ContentTagIndex tags, ContentFamilyIndex families, ContentLootIndex loot)
    {
        Tags = tags;
        Families = families;
        Loot = loot;
    }

    /// <summary>Tag id to the sorted ids of the rows carrying it, per content type.</summary>
    public ContentTagIndex Tags { get; }

    /// <summary>
    /// The block list per family. EMPTY for a version loaded from a pack, because a pack carries no family
    /// declaration to build one from. See <see cref="ContentFamilyIndex"/>.
    /// </summary>
    public ContentFamilyIndex Families { get; }

    /// <summary>Per loot table, the resolved entries with the weights prefix summed.</summary>
    public ContentLootIndex Loot { get; }

    /// <summary>The managed bytes the three hold. The key index is counted with the table that owns it.</summary>
    public long ApproximateBytes()
        => Tags.ApproximateBytes() + Families.ApproximateBytes() + Loot.ApproximateBytes();

    /// <summary>
    /// Boot step 7's second half, run from the runtime's own constructor so a runtime never exists with its
    /// engine indexes missing.
    /// </summary>
    internal static ContentDerivedIndexes Build(ContentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        // The key index lives on the type table and is built with it, so there is nothing to do for it here.
        // It is still one of the four, and it is still built before the other three.
        ContentTagIndex tags = ContentTagIndex.Build(runtime);
        ContentFamilyIndex families = ContentFamilyIndex.Build(runtime);
        ContentLootIndex loot = ContentLootIndexBuilder.Build(runtime, tags);
        return new ContentDerivedIndexes(tags, families, loot);
    }
}
