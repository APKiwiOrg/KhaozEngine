using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>Which store a crash-safety case runs against. Both answer the same nine questions.</summary>
public enum CrashStore
{
    /// <summary>The in-memory reference store, whose commit is one gate.</summary>
    InMemory = 0,

    /// <summary>The SQLite provider, whose commit is one real transaction on a real file.</summary>
    Sqlite = 1,
}

/// <summary>
/// One publish that is about to be interrupted: a store seeded to version 1, its pack store, and a commit
/// half whose publisher carries the <c>OnStep</c> hook the kill is thrown from.
/// <para>
/// <b>The crash draft is an UPDATE and never an add, deliberately.</b> Step 3 commits the id marks on their
/// own, before anything else is durable, so a kill at or after
/// <see cref="ContentPublishStep.AfterIdAllocation"/> burns the ids it issued and the retry allocates fresh
/// ones, which is reserve-before-issue working as designed and NOT a torn publish. A draft that allocates
/// nothing therefore produces the same bytes on the retry as it would have without the kill, which is what
/// makes the idempotence assertion an assertion rather than a coin toss. The id-burn case has its own test,
/// where it is the subject rather than the noise.
/// </para>
/// </summary>
internal sealed class CrashSafetyHarness : IDisposable
{
    readonly SqliteContentAuthoringStore? _sqlite;

    CrashSafetyHarness(
        CrashStore kind,
        string root,
        ContentTypeRegistry registry,
        IContentAuthoringStore store,
        FileSystemPackStore pack,
        SqliteContentAuthoringStore? sqlite)
    {
        Kind = kind;
        Root = root;
        Registry = registry;
        Store = store;
        Pack = pack;
        _sqlite = sqlite;
    }

    /// <summary>Which store this harness is over.</summary>
    public CrashStore Kind { get; }

    /// <summary>The directory everything this harness writes sits under.</summary>
    public string Root { get; }

    /// <summary>The registry the store and every publish are driven through.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The store under test, already at version 1.</summary>
    public IContentAuthoringStore Store { get; }

    /// <summary>The pack store every publish writes its files to.</summary>
    public FileSystemPackStore Pack { get; }

    /// <summary>A harness with version 1 published: two rows, ids 1 and 2, values 11 and 22.</summary>
    /// <param name="kind">Which store to run against.</param>
    public static async Task<CrashSafetyHarness> StartAsync(CrashStore kind)
    {
        string root = Path.Combine(Path.GetTempPath(), "kec-crash-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var pack = new FileSystemPackStore(Path.Combine(root, "pack"));
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

        SqliteContentAuthoringStore? sqlite = kind == CrashStore.Sqlite
            ? new SqliteContentAuthoringStore(
                "Data Source=" + Path.Combine(root, "catalog.db"), registry, pack)
            : null;
        IContentAuthoringStore store = sqlite is null
            ? new InMemoryContentAuthoringStore(registry, pack)
            : sqlite;
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate).ConfigureAwait(false);

        var harness = new CrashSafetyHarness(kind, root, registry, store, pack, sqlite);
        await harness.SeedAsync().ConfigureAwait(false);
        return harness;
    }

    /// <summary>The commit half over this harness, with the step hook a kill is thrown from.</summary>
    /// <param name="onStep">The hook, or null for an ordinary publish.</param>
    public ContentPublishCommit Commit(Action<ContentPublishStep>? onStep = null)
        => new(
            Store,
            Pack,
            new ContentPublisher(Store, (IContentIdPersistence)Store, Registry, onStep));

    /// <summary>The request a crash publish sends, naming the base version it expects.</summary>
    /// <param name="expectedBaseVersion">The base version the caller believes it is publishing onto.</param>
    public static ContentPublishRequest Request(int expectedBaseVersion)
        => new(PublishFixtures.Actor, "oid:tests", "crash safety", expectedBaseVersion);

    /// <summary>The edit every nine-step case publishes: one row repriced, which allocates no id.</summary>
    public static ContentEdit Reprice()
        => ContentEdit.Update(
            new ContentTypeId(PublishFixtures.ThingTypeId), 1, new ContentKey("one"), PublishFixtures.Fields(99));

    /// <summary>Applies edits to the open draft, opening one when none is open.</summary>
    /// <param name="edits">The edits.</param>
    public Task<ContentDraft> ApplyAsync(params ContentEdit[] edits)
        => Store.ApplyEditsAsync(edits, PublishFixtures.Actor, "oid:tests", "crash safety");

    /// <summary>Every live row of the fixture type at the current version.</summary>
    public Task<ContentRowPage> RowsAsync()
        => Store.ListRowsAsync(new ContentTypeId(PublishFixtures.ThingTypeId), 0, null, true, 0, 100);

    /// <summary>
    /// The third assertion of every case, and the one that matters: every hash any version REFERENCES is
    /// there and readable. A pack that holds a file nothing references is inert and fine, and a version that
    /// names a file the store cannot serve is a boot that fails closed forever.
    /// </summary>
    public async Task AssertEveryReferencedFileServesAsync()
    {
        IReadOnlyList<ContentVersionRecord> versions = await Store.ListVersionsAsync().ConfigureAwait(false);
        Assert.NotEmpty(versions);

        foreach (ContentVersionRecord version in versions)
        {
            var named = new List<string>();
            await foreach (string hash in Pack.ListAsync(version.VersionNumber).ConfigureAwait(false))
            {
                named.Add(hash);
            }

            Assert.NotEmpty(named);
            foreach (string hash in named)
            {
                Assert.True(
                    await Pack.ExistsAsync(hash).ConfigureAwait(false),
                    FormattableString.Invariant($"Version {version.VersionNumber} names {hash} and the store does not hold it."));
                Assert.NotNull(await Pack.GetAsync(hash).ConfigureAwait(false));
            }

            // The pointer is how the next sweep finds the version again, so a version whose pointer went
            // missing is a version the sweep would delete the files of.
            Assert.NotNull(await Pack.GetVersionPointerAsync(version.VersionNumber).ConfigureAwait(false));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sqlite?.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }

    /// <summary>Version 1: two rows, so the crash publish has something to reprice and something to leave alone.</summary>
    async Task SeedAsync()
    {
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        await ApplyAsync(
            ContentEdit.Add(type, new ContentKey("one"), PublishFixtures.Fields(11)),
            ContentEdit.Add(type, new ContentKey("two"), PublishFixtures.Fields(22))).ConfigureAwait(false);
        ContentPublishResult seeded = await Commit().PublishAsync(Request(0)).ConfigureAwait(false);
        Assert.Equal(1, seeded.VersionNumber);
    }
}
