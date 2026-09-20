using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>store_shelf</c> content type: one line of one store, in draw order.
/// </summary>
/// <remarks>
/// <b>The draw order is the <see cref="SortField"/> and never a list position.</b> A shelf is a row like any
/// other, so it arrives ordered by definition id, it may be authored in any order, and a publish may hand
/// the rows back in an order nobody chose. A panel drawing rows in arrival order would reshuffle itself the
/// first time a shelf was added in the middle. The field is what fixes the order, which is why two shelves
/// of one store may not share a sort.
/// <para>
/// A shelf is its own content type rather than a repeated field on <c>store</c>, by the rule that a
/// repeating child structure is its own type with a key reference to its parent and never a blob field. A
/// blob would cost the generic editor, the field-level audit and the field-level diff.
/// </para>
/// </remarks>
public static class StoreShelfContentType
{
    /// <summary>
    /// Id slots per chunk, sized for a child that outnumbers its parent: a shop is one store row and tens of
    /// shelves, so the shelf takes a bigger chunk than the store does.
    /// </summary>
    public const int DefaultChunkSlots = 512;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The store this shelf belongs to.</summary>
    public const string StoreField = "store";

    /// <summary>The item on the shelf, at most once per store.</summary>
    public const string ItemField = "item";

    /// <summary>The draw position within the store, unique within it.</summary>
    public const string SortField = "sort";

    internal const int StoreIndex = 0;
    internal const int ItemIndex = 1;
    internal const int SortIndex = 2;
    internal const int FieldCount = 3;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            StoreField,
            ContentFieldKind.KeyReference,
            GameContentTypeIds.StoreKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(
            ItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// Registers the type on <paramref name="registry"/>, in the game band, under the id and key
    /// <see cref="GameContentTypeIds"/> declares for it and this type's own visibility and chunk slots.
    /// </summary>
    /// <remarks>
    /// The visibility and the chunk slot count are the TYPE's rather than a caller's: a wrong visibility
    /// moves rows between the two manifests and a wrong slot count moves every content address, and neither
    /// fails loudly.
    /// </remarks>
    /// <param name="registry">A registry that is not frozen and carries neither this id nor this key.</param>
    /// <param name="validator">
    /// The type's own validator, or null for none. This package ships NO validators yet, so a game either
    /// passes one of its own or passes null. The package's own arrive separately.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">
    /// The registry is frozen, or already carries this id or this key.
    /// </exception>
    public static void Register(ContentTypeRegistry registry, IContentValidator? validator)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ContentFieldSchema schema = CreateSchema();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            GameContentTypeIds.StoreShelf,
            GameContentTypeIds.StoreShelfKey,
            new Codec(new ContentTypeId(GameContentTypeIds.StoreShelf), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The shelf row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the shelf schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
