using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>store</c> content type: one shopkeeper kind and the two rates it trades at.
/// </summary>
/// <remarks>
/// A store row is what a shopkeeper IS to the economy. The npc kind binds it to the world, and the two rates
/// say what the shop pays and what it charges, both in basis points so nothing in the price path is ever a
/// float. The shelves are <c>store_shelf</c> rows rather than a repeated field here, by the rule that a
/// repeating child structure is its own content type with a key reference to its parent.
/// <para>
/// <c>Client</c> at the type level, because a shop panel prints a price beside every line before the player
/// commits to anything. A server-only rate would mean a round trip per drawn row.
/// </para>
/// <para>
/// <see cref="NpcKindField"/> is a raw number this package gives no meaning to. Which shopkeeper it names is
/// the game's, and the only rule here is that one kind resolves to one row.
/// </para>
/// </remarks>
public static class StoreContentType
{
    /// <summary>Id slots per chunk, the engine's floor. A world has a handful of shops.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The npc kind that opens this store, unique across live rows.</summary>
    public const string NpcKindField = "npc_kind";

    /// <summary>What the shop CHARGES, in basis points of the item's value.</summary>
    public const string SellRateBasisPointsField = "sell_rate_bp";

    /// <summary>What the shop PAYS, in basis points of the item's value.</summary>
    public const string BuyRateBasisPointsField = "buy_rate_bp";

    internal const int NpcKindIndex = 0;
    internal const int SellRateBasisPointsIndex = 1;
    internal const int BuyRateBasisPointsIndex = 2;
    internal const int FieldCount = 3;

    /// <summary>
    /// What a rate is out of. A rate of <see cref="BasisPointDenominator"/> is the item's value unchanged,
    /// half of it is half price, and there is no float anywhere on the path.
    /// </summary>
    /// <remarks>
    /// The number is the definition of a basis point rather than a game's choice, which is why it is a
    /// constant here and not a seam. A game that priced in some other unit would not be authoring
    /// <c>sell_rate_bp</c>.
    /// </remarks>
    public const int BasisPointDenominator = 10_000;

    /// <summary>
    /// The largest rate, in basis points, that can be applied to an item worth
    /// <paramref name="largestItemValue"/> without pricing it outside the 32 bit range a price is carried in.
    /// </summary>
    /// <param name="largestItemValue">The most valuable item the catalog carries, in whole currency units.</param>
    /// <remarks>
    /// Derived from the two things that decide it rather than picked. A price is
    /// <c>value * rateBp / <see cref="BasisPointDenominator"/></c>, and the engine <c>item</c> type writes
    /// its <c>value</c> as a 32 bit number, so an authored item may be worth anything up to
    /// <see cref="int.MaxValue"/>. A rate at or below this answer therefore prices EVERY item in the catalog
    /// inside the range, and one above it prices the dearest one outside it.
    /// <para>
    /// A catalog whose items are all worth nothing has nothing to overflow, so the answer is the widest rate
    /// the field can hold. That is the same arithmetic with the divisor gone rather than a special case.
    /// </para>
    /// </remarks>
    public static int LargestSafeRateBasisPoints(long largestItemValue) =>
        largestItemValue <= 0
            ? int.MaxValue
            : (int)Math.Min(int.MaxValue, (long)int.MaxValue * BasisPointDenominator / largestItemValue);

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(NpcKindField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(SellRateBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(BuyRateBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
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
    /// for it, built over this same registry, and a game passes that, one of its own, a wrapper over both,
    /// or null.
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
            GameContentTypeIds.Store,
            GameContentTypeIds.StoreKey,
            new Codec(new ContentTypeId(GameContentTypeIds.Store), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The store row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the store schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }

    /// <summary>
    /// The store's own rules, which the engine's five passes cannot express: one npc kind opens one store,
    /// and neither rate is below zero or above what the price path can multiply.
    /// </summary>
    /// <remarks>
    /// It takes the REGISTRY rather than a game seam, because the one thing it needs from outside its own
    /// type is where the engine's <c>item</c> type keeps its <c>value</c>, and that is a fact about the
    /// live registration rather than about any game. It ACCUMULATES: one run reports every defect rather
    /// than the earliest, so a bulk import of a whole shop list comes back with the full picture.
    /// <para>
    /// A RETIRED row is skipped, the same way the engine's own reference pass skips one. A retired store has
    /// left play and keeps its bytes forever so a stored reference still decodes, so letting it hold an npc
    /// kind against a live replacement would report a defect nobody can fix.
    /// </para>
    /// <para>
    /// <b>The ceiling reads the ITEM rows, because a rate alone cannot overflow.</b> What throws at the
    /// counter is a value and a rate together, so the bound is <see cref="LargestSafeRateBasisPoints"/> of
    /// the dearest item the candidate carries. Both halves publish in one version and every validator sees
    /// the whole candidate, so the pair is checked every time either half moves. It stays in the store's own
    /// validator rather than the cross-type sweep because it is a rule about this type's own field, and it
    /// is the reason a store finding may name a number from an item row.
    /// </para>
    /// </remarks>
    /// <param name="registry">
    /// The registry the candidate's types are declared in, which is where the <c>item</c> value field's
    /// position is read from AT VALIDATION TIME. Never a schema this class built itself: that index is this
    /// build's idea of the item type rather than the one the candidate was registered against, and an engine
    /// release that moved an item field would leave the rule silently reading its neighbour.
    /// </param>
    public sealed class Validator(ContentTypeRegistry registry) : IContentValidator
    {
        readonly ContentTypeRegistry _registry = registry
            ?? throw new ArgumentNullException(nameof(registry));

        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            int valueIndex = EngineSchemaFields.IndexIn(
                _registry,
                EngineContentTypes.ItemTypeKey,
                ItemContentType.ValueField);

            long dearest = LargestItemValue(candidate, valueIndex);
            int ceiling = LargestSafeRateBasisPoints(dearest);
            var byNpcKind = new Dictionary<long, int>();
            foreach (ContentRow row in candidate.Rows(type))
            {
                // A row whose value count does not match the schema is the engine's own finding, and reading
                // it positionally here would be reading someone else's fields.
                if (row.IsRetired || row.Fields.Count != FieldCount)
                {
                    continue;
                }

                CheckRate(type, row, SellRateBasisPointsIndex, SellRateBasisPointsField, dearest, ceiling, findings);
                CheckRate(type, row, BuyRateBasisPointsIndex, BuyRateBasisPointsField, dearest, ceiling, findings);

                ContentFieldValue npcKind = row.Fields[NpcKindIndex];
                if (npcKind.IsAbsent)
                {
                    // A required field carrying nothing is the engine's KEC0005, and a missing kind cannot
                    // collide with anything.
                    continue;
                }

                if (byNpcKind.TryGetValue(npcKind.Number, out int first))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.StoreDuplicateNpcKind,
                        FormattableString.Invariant(
                            $"Store {row.Id} claims npc kind {npcKind.Number}, which store {first} already claims. One shopkeeper kind opens one store, so a player talking to it has to resolve to a single row.")));
                    continue;
                }

                byNpcKind.Add(npcKind.Number, row.Id);
            }
        }

        static void CheckRate(
            ContentTypeId type,
            ContentRow row,
            int index,
            string field,
            long dearestItem,
            int ceiling,
            ICollection<ContentFinding> findings)
        {
            ContentFieldValue rate = row.Fields[index];
            if (rate.IsAbsent)
            {
                return;
            }

            if (rate.Number < 0)
            {
                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    GameContentFindings.StoreNegativeRate,
                    FormattableString.Invariant(
                        $"Store {row.Id} carries {field} of {rate.Number}. A rate is basis points out of {BasisPointDenominator} and a negative one inverts the trade, paying a player to take an item.")));
                return;
            }

            if (rate.Number <= ceiling)
            {
                return;
            }

            findings.Add(new ContentFinding(
                type,
                row.Id,
                GameContentFindings.StoreRateOverCurrencyCeiling,
                FormattableString.Invariant(
                    $"Store {row.Id} carries {field} of {rate.Number}, above the {ceiling} this catalog allows. The dearest item in it is worth {dearestItem}, and that pair prices a single unit outside the range the trade path carries, which it throws on rather than wrapping a purse.")));
        }

        /// <summary>
        /// The most valuable item the candidate carries, RETIRED rows included: nothing mints a retired item
        /// any more, but a stored stack of one is still in somebody's bag and the counter still buys it, so
        /// it is still a value a rate gets applied to.
        /// </summary>
        static long LargestItemValue(IContentSnapshot candidate, int valueIndex)
        {
            if (valueIndex < 0)
            {
                return 0;
            }

            var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);
            long largest = 0;
            foreach (ContentRow row in candidate.Rows(itemType))
            {
                // A row carrying fewer values than the schema declares is the engine's own finding, and
                // reading it positionally here would be reading someone else's field.
                if (row.Fields.Count <= valueIndex)
                {
                    continue;
                }

                ContentFieldValue value = row.Fields[valueIndex];
                if (!value.IsAbsent && value.Number > largest)
                {
                    largest = value.Number;
                }
            }

            return largest;
        }
    }
}
