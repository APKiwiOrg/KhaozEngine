using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;

namespace KhaozEngine.Benchmarks.CatalogCrash;

/// <summary>
/// The one catalog both halves of the crash probe agree on: a real SQLite file, a real
/// <see cref="FileSystemPackStore"/> beside it, one game-band type with one int field, two rows at version 1,
/// and the reprice the interrupted publish carries.
/// <para>
/// <b>The crash draft is an UPDATE and never an add</b>, the same choice the in-process suite makes: step 3
/// commits the id marks on their own, so a kill after it burns the ids it issued and the retry allocates
/// fresh ones, which is reserve before issue working rather than a torn publish. A draft that allocates
/// nothing produces the same bytes on the retry, which is what makes the manifest hash comparison mean
/// something.
/// </para>
/// </summary>
internal static class CatalogCrashScene
{
    /// <summary>The fixture type's id, the first the game band is entitled to.</summary>
    internal const ushort TypeId = 1024;

    /// <summary>The actor every edit and publish carries.</summary>
    internal const string Actor = "catalog-crash-probe";

    /// <summary>The line the child writes when it reaches the step it was told to pause at.</summary>
    internal const string Checkpoint = "CATALOG_CHECKPOINT";

    /// <summary>The fixture type as a type id.</summary>
    internal static ContentTypeId Thing => new(TypeId);

    /// <summary>The database file under one probe root.</summary>
    /// <param name="root">The probe root.</param>
    internal static string DatabasePath(string root) => Path.Combine(root, "catalog.db");

    /// <summary>The pack root under one probe root.</summary>
    /// <param name="root">The probe root.</param>
    internal static string PackPath(string root) => Path.Combine(root, "pack");

    /// <summary>A registry carrying the one fixture type. Per call, never ambient.</summary>
    internal static ContentTypeRegistry Registry()
    {
        var schema = new ContentFieldSchema(new List<ContentFieldEntry>
        {
            new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
        });
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            TypeId,
            "thing",
            new CatalogCrashCodec(Thing, schema),
            null,
            schema,
            ContentVisibility.Client,
            256);
        return registry;
    }

    /// <summary>A store over one probe root, opened under the given schema mode.</summary>
    /// <param name="root">The probe root.</param>
    /// <param name="registry">The registry the store is opened over.</param>
    /// <param name="mode">Whether the schema may be created.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    internal static async Task<SqliteContentAuthoringStore> OpenAsync(
        string root,
        ContentTypeRegistry registry,
        ContentAuthoringSchemaMode mode,
        CancellationToken cancellationToken = default)
    {
        var store = new SqliteContentAuthoringStore(
            "Data Source=" + DatabasePath(root) + ";Pooling=False",
            registry,
            new FileSystemPackStore(PackPath(root)));
        await store.InitializeAsync(mode, cancellationToken).ConfigureAwait(false);
        return store;
    }

    /// <summary>The commit half over one store, with the step hook a kill pauses at.</summary>
    /// <param name="store">The store.</param>
    /// <param name="registry">The registry.</param>
    /// <param name="onStep">The hook, or null for an ordinary publish.</param>
    internal static ContentPublishCommit Commit(
        SqliteContentAuthoringStore store,
        ContentTypeRegistry registry,
        Action<ContentPublishStep>? onStep = null)
        => new(
            store,
            store.PackStore ?? throw new InvalidOperationException("The probe store was opened with no pack target."),
            new ContentPublisher(store, store, registry, onStep));

    /// <summary>The request a probe publish sends.</summary>
    /// <param name="expectedBaseVersion">The base version the caller believes it is publishing onto.</param>
    internal static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, "oid:probe", "catalog crash probe", expectedBaseVersion);

    /// <summary>
    /// A probe root with version 1 published AND the reprice staged in the open draft, so the child process
    /// has nothing left to do but publish. The store is DISPOSED before this returns, which is what releases
    /// the SQLite file for the child.
    /// </summary>
    /// <param name="root">The probe root to create.</param>
    /// <param name="cancellationToken">Cancels the seed.</param>
    internal static async Task SeedAsync(string root, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(root);
        ContentTypeRegistry registry = Registry();
        SqliteContentAuthoringStore store = await OpenAsync(
            root, registry, ContentAuthoringSchemaMode.AutoCreate, cancellationToken).ConfigureAwait(false);
        try
        {
            await store.ApplyEditsAsync(
                [
                    ContentEdit.Add(Thing, new ContentKey("one"), Fields(11)),
                    ContentEdit.Add(Thing, new ContentKey("two"), Fields(22)),
                ],
                Actor,
                "oid:probe",
                "seed",
                cancellationToken).ConfigureAwait(false);
            await Commit(store, registry).PublishAsync(Request(0), cancellationToken).ConfigureAwait(false);
            await store.ApplyEditsAsync(
                [ContentEdit.Update(Thing, 1, new ContentKey("one"), Fields(99))],
                Actor,
                "oid:probe",
                "reprice",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>One row's fields.</summary>
    /// <param name="value">The int field's value.</param>
    internal static ContentFieldEdit[] Fields(int value)
        => [new ContentFieldEdit("value", ContentFieldValue.OfNumber(ContentFieldKind.Int, value))];

    /// <summary>Deletes a probe root, sidecars and all, retrying a Windows share violation a few times.</summary>
    /// <param name="root">The probe root.</param>
    internal static void Delete(string root)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }
}

/// <summary>The generic positional walk with nothing added, which is what the probe's one type registers.</summary>
internal sealed class CatalogCrashCodec(ContentTypeId type, ContentFieldSchema schema)
    : ContentRowCodecBase(type, schema)
{
}
