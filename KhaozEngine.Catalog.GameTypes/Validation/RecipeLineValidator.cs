using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The three rules a recipe LINE obeys, whichever side of the recipe it sits on: a list position is unique
/// within its recipe, a count is above zero, and no line names an item that has left play.
/// </summary>
/// <remarks>
/// <c>recipe_input</c> and <c>recipe_output</c> are separate types on purpose, and they carry separate
/// finding codes so a report names which side of the recipe is wrong, but the rules themselves are one rule
/// set read twice. Holding two copies of it means a correctness fix lands on whichever side the next person
/// happens to be editing, and the two then disagree about the same rule for the same reason on two sides of
/// one recipe. So the CONTROL FLOW lives here once and each type supplies its own codes, its own field
/// positions and its own nouns.
/// <para>
/// It ACCUMULATES, so one run over a whole recipe book reports every defect rather than the earliest.
/// </para>
/// <para>
/// A RETIRED line is skipped, the same way the engine's reference pass skips a retired row. A withdrawn line
/// holds no list position and moves nothing.
/// </para>
/// <para>
/// A recipe with NO input at all is not reported here, and neither is one with no output. Neither is a
/// statement about a row either type carries, so both belong to the cross-type pass that can see the recipe
/// and its children at once.
/// </para>
/// </remarks>
sealed class RecipeLineValidator : IContentValidator
{
    /// <summary>The capitalised noun a message opens with, for example <c>Input</c>.</summary>
    internal required string Noun { get; init; }

    /// <summary>The same noun mid sentence, for example <c>input</c>. The plural is this plus an s.</summary>
    internal required string LowerNoun { get; init; }

    /// <summary>What the line does to its item, for example <c>consumes</c>.</summary>
    internal required string Verb { get; init; }

    /// <summary>The sentence explaining why a count of zero is refused on this side.</summary>
    internal required string CountReason { get; init; }

    /// <summary>What the step would go on doing with a retired item, for example <c>asking for</c>.</summary>
    internal required string RetiredAction { get; init; }

    /// <summary>The position of the parent recipe reference in a row's value list.</summary>
    internal required int RecipeIndex { get; init; }

    /// <summary>The position of the item reference.</summary>
    internal required int ItemIndex { get; init; }

    /// <summary>The position of the count.</summary>
    internal required int CountIndex { get; init; }

    /// <summary>The position of the list order.</summary>
    internal required int SortIndex { get; init; }

    /// <summary>How many values a row of this type carries, which is its schema's field count.</summary>
    internal required int FieldCount { get; init; }

    /// <summary>The code for two live lines of one recipe claiming one list position.</summary>
    internal required string DuplicateSortCode { get; init; }

    /// <summary>The code for a line that moves nothing.</summary>
    internal required string CountNotPositiveCode { get; init; }

    /// <summary>The code for a line naming an item that has left play.</summary>
    internal required string RetiredItemCode { get; init; }

    /// <inheritdoc />
    public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(findings);

        var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);
        var sorts = new Dictionary<(long Recipe, long Sort), int>();

        foreach (ContentRow row in candidate.Rows(type))
        {
            // A row whose value count does not match the schema is the engine's own finding, and reading it
            // positionally here would be reading someone else's fields.
            if (row.IsRetired || row.Fields.Count != FieldCount)
            {
                continue;
            }

            ContentFieldValue recipe = row.Fields[RecipeIndex];
            ContentFieldValue item = row.Fields[ItemIndex];
            ContentFieldValue count = row.Fields[CountIndex];
            ContentFieldValue sort = row.Fields[SortIndex];

            if (!count.IsAbsent && count.Number <= 0)
            {
                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    CountNotPositiveCode,
                    FormattableString.Invariant(
                        $"{Noun} {row.Id} {Verb} {count.Number} of item {item.Number}. {CountReason}")));
            }

            // A reference of 0 is NO CONTENT rather than a dangling id, and a required field left empty is
            // the engine's KEC0005. Either way there is no recipe to be unique within.
            if (!recipe.IsAbsent && recipe.Number != 0 && !sort.IsAbsent
                && !sorts.TryAdd((recipe.Number, sort.Number), row.Id))
            {
                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    DuplicateSortCode,
                    FormattableString.Invariant(
                        $"{Noun} {row.Id} of recipe {recipe.Number} claims list position {sort.Number}, which {LowerNoun} {sorts[(recipe.Number, sort.Number)]} already claims. The order is the sort field and never the row order, so two {LowerNoun}s at one position have no order between them.")));
            }

            if (item.IsAbsent || item.Number == 0 || !candidate.IsRetired(itemType, (int)item.Number))
            {
                continue;
            }

            findings.Add(new ContentFinding(
                type,
                row.Id,
                RetiredItemCode,
                FormattableString.Invariant(
                    $"{Noun} {row.Id} of recipe {recipe.Number} {Verb} item {item.Number}, which is retired. A retired row keeps its bytes so a stored stack still decodes, so the {LowerNoun} still resolves and the step would go on {RetiredAction} something the game withdrew.")));
        }
    }
}
