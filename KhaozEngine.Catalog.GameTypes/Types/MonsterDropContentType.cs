using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>monster_drop</c> content type: one creature kind and the <c>loot_table</c> it rolls on death.
/// </summary>
/// <remarks>
/// <b><c>ServerOnly</c> at the TYPE level</b>, which is what keeps the whole family out of every client
/// manifest rather than merely out of a client's view of a row. It is the same declaration the engine's
/// <c>loot_table</c> and <c>loot_entry</c> carry, for the same reason: a client that can read a drop table
/// knows every roll before it happens, and the server is authoritative over what falls.
/// <para>
/// The type holds the BINDING and nothing else. What actually drops is the engine's table, rolled by the
/// engine's <c>LootRoller</c>, which is the one implementation of the composition rule. A roll written here
/// would be a second answer to the first table that used both <c>guaranteed</c> and <c>roll_count</c>.
/// </para>
/// </remarks>
public static class MonsterDropContentType
{
    /// <summary>Id slots per chunk, the engine's floor. One row per creature kind is a small type.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.ServerOnly;

    /// <summary>The creature kind this rule belongs to, unique across live rows.</summary>
    public const string MonsterKindField = "monster_kind";

    /// <summary>The engine loot table the kind rolls.</summary>
    public const string LootTableField = "loot_table";

    internal const int MonsterKindIndex = 0;
    internal const int LootTableIndex = 1;
    internal const int FieldCount = 2;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(MonsterKindField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
        new ContentFieldEntry(
            LootTableField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.LootTableTypeKey,
            ContentVisibility.ServerOnly,
            true),
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
            GameContentTypeIds.MonsterDrop,
            GameContentTypeIds.MonsterDropKey,
            new Codec(new ContentTypeId(GameContentTypeIds.MonsterDrop), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The drop row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the monster drop schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }

    /// <summary>
    /// The type's one rule: a creature kind names one table. A death has to resolve to a single roll, and
    /// two rows claiming one kind would make that a choice nobody authored.
    /// </summary>
    /// <remarks>
    /// It takes NO options. Which creature kinds exist is the game's, and the rule is about two rows
    /// agreeing rather than about any one number meaning anything. It ACCUMULATES, so one run reports every
    /// duplicate rather than the earliest.
    /// <para>
    /// A RETIRED row is skipped, the same way the engine's own reference pass skips one. A withdrawn rule
    /// rolls nothing, so holding a kind against its live replacement would report a defect nobody can fix.
    /// </para>
    /// </remarks>
    public sealed class Validator : IContentValidator
    {
        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            var byKind = new Dictionary<long, int>();
            foreach (ContentRow row in candidate.Rows(type))
            {
                // A row whose value count does not match the schema is the engine's own finding, and reading
                // it positionally here would be reading someone else's fields.
                if (row.IsRetired || row.Fields.Count != FieldCount)
                {
                    continue;
                }

                ContentFieldValue kind = row.Fields[MonsterKindIndex];
                if (kind.IsAbsent || byKind.TryAdd(kind.Number, row.Id))
                {
                    continue;
                }

                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    GameContentFindings.MonsterDropDuplicateMonsterKind,
                    FormattableString.Invariant(
                        $"Drop rule {row.Id} claims creature kind {kind.Number}, which rule {byKind[kind.Number]} already claims. A death rolls one table, so two rules for one kind is a choice nobody authored.")));
            }
        }
    }
}
