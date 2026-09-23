using System;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The validators of the three child types whose rules are about a row's place under its PARENT:
/// <c>store_shelf</c>, <c>recipe_input</c> and <c>recipe_output</c>.
/// </summary>
/// <remarks>
/// The two recipe sides run the same three rules through one control flow under two sets of codes, so the
/// facts here check both that a defect is reported and that it comes back under the side it happened on. A
/// report that said only "a line is wrong" would send an author to the wrong half of the recipe.
/// </remarks>
public class ChildValidatorTests
{
    [Fact]
    public void StoreShelfRefusesATiedDrawPositionASecondShelfForOneItemAndARetiredItem()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration shelf = Registration(registry, GameContentTypeIds.StoreShelf);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "bread"),
            Item(item, 12, "coins"),
            Item(item, 13, "withdrawn_blade", isRetired: true),

            // Store 1's two shelves, both claiming draw position 0.
            Shelf(shelf, 10, store: 1, item: 11, sort: 0),
            Shelf(shelf, 11, store: 1, item: 12, sort: 0),

            // The same position under a DIFFERENT store, which is ordinary and must not be reported.
            Shelf(shelf, 12, store: 2, item: 11, sort: 0),

            // The same item twice on one store, and an item that has left play.
            Shelf(shelf, 13, store: 2, item: 11, sort: 1),
            Shelf(shelf, 14, store: 2, item: 13, sort: 2));

        Assert.True(candidate.IsRetired(item.Type, 13));

        Assert.Equal(
            new[]
            {
                (11, GameContentFindings.StoreShelfDuplicateSort),
                (13, GameContentFindings.StoreShelfDuplicateItem),
                (14, GameContentFindings.StoreShelfRetiredItem),
            },
            Codes(new StoreShelfContentType.Validator(), shelf.Type, candidate));
    }

    [Fact]
    public void StoreShelfSkipsARetiredShelfAndOneBelongingToNoStore()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration shelf = Registration(registry, GameContentTypeIds.StoreShelf);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "bread"),
            Shelf(shelf, 10, store: 1, item: 11, sort: 0),
            RetiredRowOf(
                shelf,
                11,
                "shelf_11",
                true,
                (StoreShelfContentType.StoreField, Ref(1)),
                (StoreShelfContentType.ItemField, Ref(11)),
                (StoreShelfContentType.SortField, Int(0))),

            // No store is no content, so two shelves belonging to nothing are not two shelves of one store.
            Shelf(shelf, 12, store: 0, item: 11, sort: 0),
            Shelf(shelf, 13, store: 0, item: 11, sort: 0));

        Assert.Empty(Codes(new StoreShelfContentType.Validator(), shelf.Type, candidate));
    }

    [Fact]
    public void BothRecipeSidesRefuseATiedListPositionACountOfNothingAndARetiredItem()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration input = Registration(registry, GameContentTypeIds.RecipeInput);
        ContentTypeRegistration output = Registration(registry, GameContentTypeIds.RecipeOutput);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "logs"),
            Item(item, 12, "timber"),
            Item(item, 13, "ashen_logs", isRetired: true),

            // Two inputs of ONE recipe claiming position zero.
            Line(input, 20, recipe: 1, item: 11, count: 1, sort: 0),
            Line(input, 21, recipe: 1, item: 12, count: 1, sort: 0),

            // The same position under a DIFFERENT recipe, which is ordinary and must not be reported.
            Line(input, 22, recipe: 2, item: 11, count: 1, sort: 0),
            Line(input, 23, recipe: 2, item: 11, count: 0, sort: 1),
            Line(input, 24, recipe: 2, item: 13, count: 1, sort: 2),

            Line(output, 30, recipe: 1, item: 12, count: 2, sort: 0),
            Line(output, 31, recipe: 1, item: 11, count: 1, sort: 0),
            Line(output, 32, recipe: 2, item: 11, count: 0, sort: 1),
            Line(output, 33, recipe: 2, item: 13, count: 1, sort: 2));

        Assert.Equal(
            new[]
            {
                (21, GameContentFindings.RecipeInputDuplicateSort),
                (23, GameContentFindings.RecipeInputCountNotPositive),
                (24, GameContentFindings.RecipeInputRetiredItem),
            },
            Codes(new RecipeInputContentType.Validator(), input.Type, candidate));

        Assert.Equal(
            new[]
            {
                (31, GameContentFindings.RecipeOutputDuplicateSort),
                (32, GameContentFindings.RecipeOutputCountNotPositive),
                (33, GameContentFindings.RecipeOutputRetiredItem),
            },
            Codes(new RecipeOutputContentType.Validator(), output.Type, candidate));

        // The two sides carry their own tokens, so a report names which half of the recipe is wrong.
        Assert.NotEqual(
            GameContentFindings.RecipeInputDuplicateSort,
            GameContentFindings.RecipeOutputDuplicateSort);
        Assert.NotEqual(
            GameContentFindings.RecipeInputCountNotPositive,
            GameContentFindings.RecipeOutputCountNotPositive);
        Assert.NotEqual(
            GameContentFindings.RecipeInputRetiredItem,
            GameContentFindings.RecipeOutputRetiredItem);
    }

    [Fact]
    public void ARecipeLineNamingNoRecipeCannotTieAListPositionWithAnother()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration input = Registration(registry, GameContentTypeIds.RecipeInput);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "logs"),

            // A reference of 0 is no content, which is the engine's own required-field finding. There is no
            // recipe for either line to be unique within.
            Line(input, 20, recipe: 0, item: 11, count: 1, sort: 0),
            Line(input, 21, recipe: 0, item: 11, count: 1, sort: 0));

        Assert.Empty(Codes(new RecipeInputContentType.Validator(), input.Type, candidate));
    }

    /// <summary>
    /// The messages name the side they came from, so an author reading one knows which half of the recipe
    /// to open.
    /// </summary>
    [Fact]
    public void TheTwoRecipeSidesSpeakTheirOwnNouns()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration input = Registration(registry, GameContentTypeIds.RecipeInput);
        ContentTypeRegistration output = Registration(registry, GameContentTypeIds.RecipeOutput);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "logs"),
            Line(input, 20, recipe: 1, item: 11, count: 0, sort: 0),
            Line(output, 30, recipe: 1, item: 11, count: 0, sort: 0));

        ContentFinding consumed = Assert.Single(
            Findings(new RecipeInputContentType.Validator(), input.Type, candidate));
        Assert.StartsWith("Input 20 consumes 0", consumed.Message, StringComparison.Ordinal);

        ContentFinding produced = Assert.Single(
            Findings(new RecipeOutputContentType.Validator(), output.Type, candidate));
        Assert.StartsWith("Output 30 produces 0", produced.Message, StringComparison.Ordinal);
    }

    static ContentRow Shelf(ContentTypeRegistration shelf, int id, int store, int item, int sort)
        => RowOf(
            shelf,
            id,
            FormattableString.Invariant($"shelf_{id}"),
            (StoreShelfContentType.StoreField, Ref(store)),
            (StoreShelfContentType.ItemField, Ref(item)),
            (StoreShelfContentType.SortField, Int(sort)));

    /// <summary>
    /// One line of either side, filled by field NAME. The input and the output schemas declare the same four
    /// names, so one builder serves both and the fact reads as the one rule set it is.
    /// </summary>
    static ContentRow Line(
        ContentTypeRegistration side,
        int id,
        int recipe,
        int item,
        int count,
        int sort)
        => RowOf(
            side,
            id,
            FormattableString.Invariant($"line_{id}"),
            (RecipeInputContentType.RecipeField, Ref(recipe)),
            (RecipeInputContentType.ItemField, Ref(item)),
            (RecipeInputContentType.CountField, Int(count)),
            (RecipeInputContentType.SortField, Int(sort)));
}
