using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// One look at the store taken from OUTSIDE the publish, which is what fact 22 is asserted with.
/// </summary>
/// <param name="ActiveVersion">The active pointer as the look saw it.</param>
/// <param name="VersionRowPresent">Whether the version row was there in the same look.</param>
public readonly record struct CatalogPointerLook(int ActiveVersion, bool VersionRowPresent);

/// <summary>
/// The twenty-three provider conformance facts of spec 15.5, as an abstract class with one concrete subclass
/// per backend: the in-memory reference store, SQLite and SQL Server. It is the shape
/// <c>WalletStoreContract</c> established and the journal's <c>MutationJournalStoreConformance</c> runs at a
/// larger scale.
/// <para>
/// <b>Every fact asserts OBSERVABLE behaviour and never a mechanism.</b> A fact that reached into a table or
/// a private field would pass on one backend and be unwritable on another, and the point of the suite is
/// that three implementations of one seam answer the same way. Where a fact names a column (fact 9's
/// <c>valid_from_version</c>, fact 13's <c>reserved_through</c>), it is asserted through the seam member that
/// reports it.
/// </para>
/// <para>
/// <b>The in-memory store is the REFERENCE.</b> It is the implementation every other suite in the engine
/// tests the publish pipeline against, so a fact it fails is a defect in the reference rather than a fact
/// that does not apply, and the only facts it overrides are the three that assert a SCHEMA (1 to 3), because
/// it has no database that can be empty.
/// </para>
/// <para>
/// <b>Numbered in the name.</b> The spec table is the index into this file and the number is how a failure
/// message points back at it.
/// </para>
/// </summary>
public abstract partial class ContentAuthoringStoreConformance
{
    /// <summary>The registry every store in one test instance is opened over. Per instance, never ambient.</summary>
    protected ContentTypeRegistry Registry { get; } = CatalogFixtures.Registry(CatalogFixtures.ThingSpec);

    /// <summary>The fixture type as a type id, which is what every seam member takes.</summary>
    protected static ContentTypeId Thing => CatalogFixtures.Thing;

    /// <summary>The migration a schema refusal names, which is per provider and identical in both so far.</summary>
    protected virtual string RequiredMigration => "catalog-v1-initial";

    /// <summary>
    /// A store over an EMPTY, UNINITIALIZED database, which is what facts 1 and 2 need: the schema is the
    /// caller's decision and <see cref="IContentAuthoringStore.InitializeAsync"/> is where it is made.
    /// </summary>
    protected abstract IContentAuthoringStore NewStore();

    /// <summary>
    /// Empties the backing database and hands back an INITIALIZED store over it, which is how the bundle
    /// facts reach an empty database without needing two at once. SQL Server's isolation unit is the schema
    /// of one test database rather than a file, so two live stores would be two instances.
    /// </summary>
    protected abstract Task<IContentAuthoringStore> ResetToEmptyAsync();

    /// <summary>
    /// Makes the next audit write FAIL, and undoes it on dispose. Fact 17 is the only caller: an audit insert
    /// that fails has to take the edit down with it, and no backend offers a way to ask for that, so each
    /// subclass arms the fault the way its own store can be made to fail.
    /// </summary>
    /// <param name="store">The store whose audit write is to fail.</param>
    protected abstract IDisposable ArmAnAuditWriteFault(IContentAuthoringStore store);

    /// <summary>
    /// One look at the active pointer and the version row TOGETHER, taken through a second reader while a
    /// publish is in flight, or null when no consistent look could be taken (a lock, a timeout, a straddled
    /// read). Fact 22 is the only caller.
    /// </summary>
    /// <param name="store">The store being published to.</param>
    /// <param name="versionNumber">The version whose row the look asks about.</param>
    protected abstract Task<CatalogPointerLook?> LookAsync(IContentAuthoringStore store, int versionNumber);

    /// <summary>A store with its schema created, which is where every fact but 1 and 2 starts.</summary>
    protected async Task<IContentAuthoringStore> OpenAsync()
    {
        IContentAuthoringStore store = NewStore();
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    /// <summary>The durable id half of a store. Every backend implements both halves on the one type.</summary>
    /// <param name="store">The store.</param>
    protected static IContentIdPersistence Ids(IContentAuthoringStore store)
        => store as IContentIdPersistence
            ?? throw new InvalidOperationException(
                "A conformance store implements IContentIdPersistence: the allocator facts read the two marks through it.");

    /// <summary>The request every fixture publish sends, naming the base version it expects.</summary>
    /// <param name="expectedBaseVersion">The base version the caller believes it is publishing onto.</param>
    protected static ContentPublishRequest Request(int expectedBaseVersion)
        => new(CatalogFixtures.Actor, CatalogFixtures.Operator, "conformance", expectedBaseVersion);

    /// <summary>Applies edits to the open draft, opening one when none is open.</summary>
    /// <param name="store">The store.</param>
    /// <param name="edits">The edits.</param>
    protected static Task<ContentDraft> ApplyAsync(IContentAuthoringStore store, params ContentEdit[] edits)
        => store.ApplyEditsAsync(edits, CatalogFixtures.Actor, CatalogFixtures.Operator, "conformance");

    /// <summary>Applies edits and publishes them onto the version the store currently stands at.</summary>
    /// <param name="store">The store.</param>
    /// <param name="edits">The edits.</param>
    protected static async Task<ContentPublishResult> PublishAsync(
        IContentAuthoringStore store,
        params ContentEdit[] edits)
    {
        int baseVersion = await store.GetActiveVersionAsync();
        await ApplyAsync(store, edits);
        return await store.PublishAsync(Request(baseVersion));
    }

    /// <summary>One page of a type's live rows, big enough that no fact has to page.</summary>
    /// <param name="store">The store.</param>
    /// <param name="versionNumber">The version, or 0 for the current live set.</param>
    /// <param name="includeRetired">Whether retired rows are in the page.</param>
    protected static Task<ContentRowPage> RowsAsync(
        IContentAuthoringStore store,
        int versionNumber = 0,
        bool includeRetired = false)
        => store.ListRowsAsync(Thing, versionNumber, null, includeRetired, 0, 100);

    /// <summary>Every row id in a page, in page order, which is what an id assertion reads.</summary>
    /// <param name="page">The page.</param>
    protected static int[] Ids(ContentRowPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var ids = new int[page.Rows.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = page.Rows[i].Id;
        }

        return ids;
    }

    /// <summary>Every key in a page, in page order.</summary>
    /// <param name="page">The page.</param>
    protected static string[] Keys(ContentRowPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var keys = new string[page.Rows.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = page.Rows[i].Key.ToString();
        }

        return keys;
    }

    /// <summary>The full rule list as it stands, read the way the publish pipeline reads it.</summary>
    /// <param name="store">The store.</param>
    protected static async Task<IReadOnlyList<RemapRule>> RulesAsync(IContentAuthoringStore store)
    {
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        return baseline.Rules;
    }

    /// <summary>The open draft, failing with the fact's own message when there is none.</summary>
    /// <param name="store">The store.</param>
    protected static async Task<ContentDraft> DraftAsync(IContentAuthoringStore store)
    {
        ContentDraft? draft = await store.GetOpenDraftAsync();
        Assert.NotNull(draft);
        return draft;
    }
}
