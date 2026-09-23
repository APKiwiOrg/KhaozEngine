using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>recipe_output</c> content type: one item one recipe PRODUCES, and how many of it.
/// </summary>
/// <remarks>
/// An output is its own content type rather than a repeated field on <c>recipe</c>, by the rule that a
/// repeating child structure is its own type with a key reference to its parent and never a blob field. A
/// blob would cost the generic editor, the field-level audit and the field-level diff.
/// <para>
/// <b>The order is the <see cref="SortField"/> and never a list position</b>, the same as on the input side.
/// A row arrives ordered by definition id and may be authored in any order, so the field is what fixes which
/// product a panel lists first. Two outputs of one recipe may not share one.
/// </para>
/// <para>
/// The count is also a PAYOUT input rather than only a bag line, wherever a game pays its recipe's
/// experience per product item: a row producing two of something then pays twice.
/// </para>
/// </remarks>
public static class RecipeOutputContentType
{
    /// <summary>
    /// Id slots per chunk, sized for a child that outnumbers its parent: one recipe is one row and one or
    /// more outputs, so the output takes a bigger chunk than the recipe does.
    /// </summary>
    public const int DefaultChunkSlots = 1024;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The recipe this output belongs to.</summary>
    public const string RecipeField = "recipe";

    /// <summary>The item the step produces.</summary>
    public const string ItemField = "item";

    /// <summary>How many of the item one completion produces.</summary>
    public const string CountField = "count";

    /// <summary>The list position within the recipe, unique within it.</summary>
    public const string SortField = "sort";

    internal const int RecipeIndex = 0;
    internal const int ItemIndex = 1;
    internal const int CountIndex = 2;
    internal const int SortIndex = 3;
    internal const int FieldCount = 4;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            RecipeField,
            ContentFieldKind.KeyReference,
            GameContentTypeIds.RecipeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(
            ItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(CountField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
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
            GameContentTypeIds.RecipeOutput,
            GameContentTypeIds.RecipeOutputKey,
            new Codec(new ContentTypeId(GameContentTypeIds.RecipeOutput), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The output row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the recipe output schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }

    /// <summary>
    /// The output's own rules: a list position is unique within its recipe, a count is above zero, and no
    /// output names an item that has left play.
    /// </summary>
    /// <remarks>
    /// It takes NO options and it ACCUMULATES, so one run over a whole recipe book reports every defect
    /// rather than the earliest. The rules are the input side's, read through the same control flow under
    /// this side's own codes, so a report names which side of the recipe is wrong.
    /// <para>
    /// A RETIRED output is skipped, the same way the engine's reference pass skips a retired row. A
    /// withdrawn output holds no list position and produces nothing.
    /// </para>
    /// </remarks>
    public sealed class Validator : IContentValidator
    {
        /// <summary>The shared line rules, carrying the output side's codes, positions and nouns.</summary>
        static readonly RecipeLineValidator Line = new()
        {
            Noun = "Output",
            LowerNoun = "output",
            Verb = "produces",
            CountReason =
                "Experience is paid per product item, so a line that makes nothing pays nothing and lands nothing.",
            RetiredAction = "making",
            RecipeIndex = RecipeIndex,
            ItemIndex = ItemIndex,
            CountIndex = CountIndex,
            SortIndex = SortIndex,
            FieldCount = FieldCount,
            DuplicateSortCode = GameContentFindings.RecipeOutputDuplicateSort,
            CountNotPositiveCode = GameContentFindings.RecipeOutputCountNotPositive,
            RetiredItemCode = GameContentFindings.RecipeOutputRetiredItem,
        };

        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
            => Line.Validate(type, candidate, findings);
    }
}
