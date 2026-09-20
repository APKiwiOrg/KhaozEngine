using System;
using System.Collections.Generic;

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
    /// The type's own validator, or null for none. <see cref="Validator"/> is the one this package ships
    /// for it, and a game passes that, one of its own, a wrapper over both, or null.
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

    /// <summary>
    /// The shelf's own rules: a draw position is unique within its store, an item appears at most once on a
    /// store, and no shelf names an item that has left play.
    /// </summary>
    /// <remarks>
    /// It takes NO options and it ACCUMULATES, so one run over a whole shop list reports every defect
    /// rather than the earliest.
    /// <para>
    /// The retired check ADDS to the engine's own rather than restating it. The engine's reference pass
    /// reports a reference to a row that is not live under <c>KEC0006</c>, which is a statement about a
    /// dangling pointer. This one is a statement about a SHOP: the row is present and decodable, the shelf
    /// resolves fine, and the shop would quietly keep selling something the game withdrew.
    /// </para>
    /// <para>
    /// A RETIRED shelf is skipped, the same way the engine's reference pass skips a retired row. A withdrawn
    /// shelf holds no draw position and sells nothing.
    /// </para>
    /// </remarks>
    public sealed class Validator : IContentValidator
    {
        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);
            var sorts = new Dictionary<(long Store, long Sort), int>();
            var items = new Dictionary<(long Store, long Item), int>();

            foreach (ContentRow row in candidate.Rows(type))
            {
                // A row whose value count does not match the schema is the engine's own finding, and reading
                // it positionally here would be reading someone else's fields.
                if (row.IsRetired || row.Fields.Count != FieldCount)
                {
                    continue;
                }

                ContentFieldValue store = row.Fields[StoreIndex];
                ContentFieldValue item = row.Fields[ItemIndex];
                ContentFieldValue sort = row.Fields[SortIndex];

                // A reference of 0 is NO CONTENT rather than a dangling id, and a required field left empty
                // is the engine's KEC0005. Either way there is no store to be unique within.
                if (store.IsAbsent || store.Number == 0)
                {
                    continue;
                }

                if (!sort.IsAbsent && !sorts.TryAdd((store.Number, sort.Number), row.Id))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.StoreShelfDuplicateSort,
                        FormattableString.Invariant(
                            $"Shelf {row.Id} of store {store.Number} claims draw position {sort.Number}, which shelf {sorts[(store.Number, sort.Number)]} already claims. The draw order is the sort field and never the row order, so two shelves at one position have no order between them.")));
                }

                if (item.IsAbsent || item.Number == 0)
                {
                    continue;
                }

                if (!items.TryAdd((store.Number, item.Number), row.Id))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.StoreShelfDuplicateItem,
                        FormattableString.Invariant(
                            $"Shelf {row.Id} puts item {item.Number} on store {store.Number}, which shelf {items[(store.Number, item.Number)]} already does. One item is one line of one shop, so a second shelf draws the same stock twice.")));
                }

                if (candidate.IsRetired(itemType, (int)item.Number))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.StoreShelfRetiredItem,
                        FormattableString.Invariant(
                            $"Shelf {row.Id} of store {store.Number} names item {item.Number}, which is retired. A retired row keeps its bytes so a stored stack still decodes, so the shelf still resolves and the shop would go on selling something the game withdrew.")));
                }
            }
        }
    }
}
