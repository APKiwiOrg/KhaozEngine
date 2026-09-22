using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// An <c>Add</c> that CARRIES its own definition id, against a POPULATED catalog, proved the same way on the
/// in-memory reference, SQLite and SQL Server.
/// <para>
/// <b>Why a populated one.</b> The carried id was documented as a bulk-import-into-an-empty-database device,
/// and nothing enforced that. A content upgrade needs it against a live catalog, because the allocator's
/// durable mark sits above the highest row id after any publish that was refused once it had reserved, so a
/// plan that predicted the counter would file the new rows under numbers the committed bundle names other
/// rows by. These facts pin what a carried add does there and what it refuses.
/// </para>
/// <para>
/// <b>Every refusal asserts that NOTHING moved</b>, the two id marks included. The marks matter more than
/// they look: step 3 seeds them from the carried ids on their own commits before the sweep runs, so a check
/// that lived in the sweep would leave a type's counter advanced for a version nobody published.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>
    /// A carried add publishes under EXACTLY that id, the type's marks rise to cover it, and the next
    /// ordinary add is issued an id above it rather than one that reuses it.
    /// </summary>
    [Fact]
    public virtual async Task ACarriedAddIntoAPopulatedCatalogPublishesUnderExactlyThatId()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        await PublishAsync(
            store, ContentEdit.Import(Thing, 7, new ContentKey("seven"), CatalogFixtures.Fields(77)));

        ContentRowPage published = await RowsAsync(store);
        Assert.Equal([1, 7], Ids(published));
        Assert.Equal(["one", "seven"], Keys(published));

        // Both marks cover the carried id, reserve before issue, so nothing can be handed it again.
        ContentIdHighWater mark = await Ids(store).ReadHighWaterAsync(Thing);
        Assert.True(mark.ReservedThrough >= 7, Describe(mark));
        Assert.True(mark.IssuedThrough >= 7, Describe(mark));

        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("next"), CatalogFixtures.Fields(88)));

        ContentRowPage page = await RowsAsync(store);
        Assert.Equal(3, page.Total);
        Assert.True(page.Rows[2].Id > 7, "The add after a carried id is issued an id above it.");
    }

    /// <summary>
    /// A carried id BELOW the current mark that no row ever held is accepted and published under that id.
    /// That is the whole point: a publish refused after step 3 burns the ids it reserved, which is reserve
    /// before issue working as designed, and a burnt id cannot be unburnt. Refusing here would dead-end a
    /// hosted catalog forever.
    /// </summary>
    [Fact]
    public virtual async Task ACarriedAddUnderABurntIdIsAcceptedAndPublishesUnderIt()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        // Burn ids 2 through 11 without writing a row, which is what an allocation whose publish was then
        // refused leaves behind.
        await store.AllocateAsync(Thing, 10);
        ContentIdHighWater burnt = await Ids(store).ReadHighWaterAsync(Thing);
        Assert.True(burnt.IssuedThrough >= 11, Describe(burnt));

        await PublishAsync(
            store, ContentEdit.Import(Thing, 3, new ContentKey("three"), CatalogFixtures.Fields(33)));

        ContentRowPage published = await RowsAsync(store);
        Assert.Equal([1, 3], Ids(published));

        // The mark is never LOWERED to the carried id, so the next ordinary add still clears the burnt range.
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("next"), CatalogFixtures.Fields(44)));
        ContentRowPage page = await RowsAsync(store);
        Assert.True(page.Rows[2].Id > 11, "The next ordinary add clears the burnt range rather than reusing it.");
    }

    /// <summary>A draft holding a carried add survives being written and read back with the id intact.</summary>
    [Fact]
    public virtual async Task ADraftHoldingACarriedAddReadsBackWithItsIdIntact()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        await ApplyAsync(
            store, ContentEdit.Import(Thing, 7, new ContentKey("seven"), CatalogFixtures.Fields(77)));

        ContentEdit held = Assert.Single((await DraftAsync(store)).Changes.Edits);
        Assert.Equal(ContentEditOperation.Add, held.Operation);
        Assert.Equal(7, held.DefinitionId);
        Assert.Equal("seven", held.Key.ToString());
    }

    /// <summary>A carried id a LIVE row already holds is refused, and nothing moves.</summary>
    [Fact]
    public virtual async Task ACarriedAddOnALiveRowsIdIsRefusedAndChangesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        await AssertRefusedAsync(
            store,
            ContentCarriedIdCodes.Taken,
            ContentEdit.Import(Thing, 1, new ContentKey("impostor"), CatalogFixtures.Fields(99)));
    }

    /// <summary>
    /// A carried id a RETIRED row holds is refused too. A retired definition keeps its id forever so a stored
    /// stack still decodes, which is exactly why reissuing it is the defect rather than a tidy-up.
    /// </summary>
    [Fact]
    public virtual async Task ACarriedAddOnARetiredRowsIdIsRefusedAndChangesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        await PublishAsync(
            store,
            ContentEdit.Retire(Thing, 1, new ContentKey("one"), ContentRetirePolicy.Placeholder, 0));

        await AssertRefusedAsync(
            store,
            ContentCarriedIdCodes.Taken,
            ContentEdit.Import(Thing, 1, new ContentKey("impostor"), CatalogFixtures.Fields(99)));
    }

    /// <summary>Two carried adds of one id in ONE draft are refused, and nothing moves.</summary>
    [Fact]
    public virtual async Task TwoCarriedAddsOfOneIdInOneDraftAreRefusedAndChangeNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        await AssertRefusedAsync(
            store,
            ContentCarriedIdCodes.Taken,
            ContentEdit.Import(Thing, 7, new ContentKey("seven"), CatalogFixtures.Fields(77)),
            ContentEdit.Import(Thing, 7, new ContentKey("also_seven"), CatalogFixtures.Fields(78)));
    }

    /// <summary>A carried id over the type's declared CEILING is refused, and nothing moves.</summary>
    [Fact]
    public virtual async Task ACarriedAddOverTheTypesCeilingIsRefusedAndChangesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));

        await AssertRefusedAsync(
            store,
            ContentCarriedIdCodes.Ceiling,
            ContentEdit.Import(
                Capped,
                CatalogFixtures.Ceiling + 1,
                new ContentKey("over_the_top"),
                CatalogFixtures.Fields(99)));
    }

    /// <summary>
    /// A carried id inside a family's reserved block, on an edit that names no family, is refused. A block is
    /// reserved for its family alone and the membership test is the id, so a row that lands in one is in that
    /// family whatever column it was written with.
    /// </summary>
    [Fact]
    public virtual async Task ACarriedAddInsideAFamilysBlockNamingNoFamilyIsRefusedAndChangesNothing()
    {
        IContentAuthoringStore store = await OpenAsync();
        await PublishAsync(store, ContentEdit.Add(Thing, new ContentKey("one"), CatalogFixtures.Fields(11)));
        ContentFamily family = await store.CreateFamilyAsync(
            Thing, "swords", ContentFamily.MinBlockSize, CatalogFixtures.Actor, CatalogFixtures.Operator);
        int inside = family.Blocks[0].BaseId + 1;

        await AssertRefusedAsync(
            store,
            ContentCarriedIdCodes.Family,
            ContentEdit.Import(Thing, inside, new ContentKey("intruder"), CatalogFixtures.Fields(99)));
    }

    /// <summary>
    /// Applies the edits, publishes, and asserts the publish was REFUSED under the code with the catalog
    /// exactly where it was: the active version, the version list, the live rows and both id marks.
    /// </summary>
    /// <param name="store">The store under test.</param>
    /// <param name="code">The finding code the refusal has to carry.</param>
    /// <param name="edits">The edits the refused publish would have written.</param>
    async Task AssertRefusedAsync(IContentAuthoringStore store, string code, params ContentEdit[] edits)
    {
        int activeBefore = await store.GetActiveVersionAsync();
        int versionsBefore = (await store.ListVersionsAsync()).Count;
        int[] idsBefore = Ids(await RowsAsync(store, includeRetired: true));
        ContentIdHighWater thingBefore = await Ids(store).ReadHighWaterAsync(Thing);
        ContentIdHighWater cappedBefore = await Ids(store).ReadHighWaterAsync(Capped);

        await ApplyAsync(store, edits);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.PublishAsync(Request(activeBefore)));

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        Assert.Contains(code, Codes(refused), StringComparer.Ordinal);
        Assert.Equal(activeBefore, await store.GetActiveVersionAsync());
        Assert.Equal(versionsBefore, (await store.ListVersionsAsync()).Count);
        Assert.Equal(idsBefore, Ids(await RowsAsync(store, includeRetired: true)));
        Assert.Equal(thingBefore, await Ids(store).ReadHighWaterAsync(Thing));
        Assert.Equal(cappedBefore, await Ids(store).ReadHighWaterAsync(Capped));

        // The draft is the caller's to deal with, exactly as it is after any other refused publish.
        await store.DiscardDraftAsync(CatalogFixtures.Actor, CatalogFixtures.Operator);
    }

    static string Describe(ContentIdHighWater mark) => FormattableString.Invariant(
        $"reserved through {mark.ReservedThrough}, issued through {mark.IssuedThrough}.");

    static string[] Codes(ContentAuthoringException refused)
    {
        IReadOnlyList<ContentFinding> findings = refused.Findings;
        var codes = new string[findings.Count];
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = findings[i].Code;
        }

        return codes;
    }
}

/// <summary>
/// The three finding codes a refused carried id carries. They are the codes already issued for those
/// invariants rather than new ones, and they are named here so a fact reads as the rule it is asserting.
/// </summary>
internal static class ContentCarriedIdCodes
{
    /// <summary>An id a row or another edit already holds, which is never reused.</summary>
    public const string Taken = "KEC0036";

    /// <summary>An id over the ceiling the type declared at registration.</summary>
    public const string Ceiling = "KEC0042";

    /// <summary>An id that disagrees with the type's family blocks.</summary>
    public const string Family = "KEC0037";
}
