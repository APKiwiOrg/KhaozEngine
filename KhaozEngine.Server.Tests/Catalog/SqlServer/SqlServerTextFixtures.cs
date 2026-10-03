using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// What the SQL Server text suites share: one CLIENT item type carrying two CLIENT text markers and one
/// SERVER-only marker, the open, and helpers that build expectations from LITERAL derived keys through the
/// shipped <see cref="ContentTextChunkCodec"/>, never through the producer under test. It is a sibling of the
/// Catalog.Tests text fixtures rather than a reference to them, because a test project's types are internal
/// to it.
/// </summary>
internal static class SqlServerTextFixtures
{
    public const string Actor = "sqlserver-text-tests";
    public const string Operator = "oid:text";
    public const string NameField = "name";
    public const string DescriptionField = "description";
    public const string SecretField = "secret_name";

    /// <summary>The literal derived key of the sword's name.</summary>
    public const string SwordName = "item.sword.name";

    /// <summary>The literal derived key of the shield's name.</summary>
    public const string ShieldName = "item.shield.name";

    public static readonly ContentTypeId Item = new(1024);
    public static readonly ContentKey Sword = new("sword");

    /// <summary>A fresh registry carrying the text item type, per store, never ambient.</summary>
    public static ContentTypeRegistry TextRegistry() => MarkerRegistry(NameField, DescriptionField);

    /// <summary>The item type with a required value, a legacy flag, the given CLIENT markers and one SERVER-only marker.</summary>
    public static ContentTypeRegistry MarkerRegistry(params string[] markers)
    {
        var fields = new List<ContentFieldEntry>
        {
            new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new("legacy", ContentFieldKind.Bool, null, ContentVisibility.Client, false),
        };
        foreach (string marker in markers)
        {
            fields.Add(new ContentFieldEntry(marker, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false));
        }

        fields.Add(new ContentFieldEntry(SecretField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.ServerOnly, false));
        var schema = new ContentFieldSchema(fields);
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game, Item.Value, "item", new Codec(schema), null, schema, ContentVisibility.Client, 256);
        return registry;
    }

    /// <summary>A store over the fixture's database and pack, creating or migrating the schema.</summary>
    public static async Task<SqlServerContentAuthoringStore> OpenAsync(
        SqlServerCatalogDatabase database,
        Func<DateTimeOffset>? clock = null,
        ContentTypeRegistry? registry = null)
    {
        var store = new SqlServerContentAuthoringStore(
            database.ConnectionString, registry ?? TextRegistry(), database.Pack(), clock);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    public static ContentEdit Add(string key = "sword", int value = 1)
        => ContentEdit.Add(Item, new ContentKey(key), new[] { Value(value) });

    public static ContentFieldEdit Value(int value)
        => new("value", ContentFieldValue.OfNumber(ContentFieldKind.Int, value));

    public static ContentTextTarget Target(string field, string language, string key = "sword")
        => new(Item, new ContentKey(key), field, language);

    public static Task<ContentDraft> ApplyAsync(
        IContentTextAuthoringStore store, IEnumerable<ContentEdit>? rows, params ContentTextEdit[] text)
        => store.ApplyChangesAsync(
            new ContentAuthoringChanges((rows ?? Array.Empty<ContentEdit>()).ToArray(), text), Actor, Operator, "text tests");

    public static async Task<int> AuditCountAsync(IContentAuthoringStore store)
        => (await store.ListAuditAsync(default, 0, 0, 500)).Count;

    public static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "text publish", expectedBaseVersion);

    /// <summary>A row-only update of the sword's value, published onto the active version.</summary>
    public static async Task RepriceAndPublishAsync(IContentAuthoringStore store, int value)
    {
        int active = await store.GetActiveVersionAsync();
        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single(row => row.Key.Equals(Sword)).Id;
        await store.ApplyEditsAsync(new[] { ContentEdit.Update(Item, sword, Sword, new[] { Value(value) }) }, Actor, Operator, "reprice");
        await store.PublishAsync(Request(active));
    }

    /// <summary>A UTF-8 value of exactly <paramref name="bytes"/> bytes carrying an astral character at the audit cut.</summary>
    public static string Utf8Value(int bytes)
    {
        string value = new string('a', ContentAuditEntry.MaxValueLength - 6) + "\U0001F600";
        return value + new string('b', bytes - Encoding.UTF8.GetByteCount(value));
    }

    /// <summary>The literal KECT hash the shipped codec computes for one language's entries, in the order given.</summary>
    public static string Hash(string tag, params (string Key, string Value)[] entries)
        => ContentTextChunkCodec.Hash(tag, Pairs(entries));

    /// <summary>The literal entries as the codec takes them.</summary>
    public static KeyValuePair<string, string>[] Pairs(params (string Key, string Value)[] entries)
        => entries.Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value)).ToArray();

    /// <summary>One language's REAL chunk over the name values of the given rows, from literal derived keys.</summary>
    public static ContentTextChunkRecord Chunk(string wireTag, params (string Key, string Value)[] names)
    {
        KeyValuePair<string, string>[] entries = names
            .Select(name => new KeyValuePair<string, string>(ContentTextKey.Derive("item", name.Key, NameField), name.Value))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        return new ContentTextChunkRecord(
            wireTag, ContentTextChunkCodec.Hash(wireTag, entries), ContentTextChunkCodec.Encode(wireTag, entries), false);
    }

    /// <summary>Both manifests' language lists of one committed version, read back off the pack.</summary>
    public static async Task<IReadOnlyList<ManifestLanguageEntry>> LanguagesAsync(IPackStore pack, ContentVersionRecord record)
    {
        ContentManifestRead server = await ContentPackReader.ReadManifestAsync(pack, record.ServerManifestHash, ContentManifestSide.Server);
        ContentManifestRead client = await ContentPackReader.ReadManifestAsync(pack, record.ClientManifestHash, ContentManifestSide.Client);
        Assert.True(server.Success, server.Reason);
        Assert.True(client.Success, client.Reason);
        Assert.Equal(server.Manifest!.Languages, client.Manifest!.Languages);
        return server.Manifest.Languages;
    }

    /// <summary>The value one literal key resolves to in a stored chunk, or null on a miss.</summary>
    public static async Task<string?> ValueAsync(IPackStore pack, string hash, string key)
    {
        ReadOnlyMemory<byte>? file = await pack.GetAsync(hash);
        Assert.NotNull(file);
        Assert.True(ContentTextIndex.TryDecode(file.Value.Span, out ContentTextIndex? index, out string? reason), reason);
        return index!.TryGetUtf8(Encoding.UTF8.GetBytes(key), out ReadOnlySpan<byte> value) ? Encoding.UTF8.GetString(value) : null;
    }

    /// <summary>
    /// The store's frozen draft as a complete text plan whose English record is <paramref name="chunk"/>: the
    /// row plan through the ordinary publisher over a row-only in-memory twin, with the store as the id
    /// persistence, and one revision for the sword's English name.
    /// </summary>
    public static async Task<ContentTextPublishPlan> PlanAsync(
        SqlServerContentAuthoringStore store, ContentTextChunkRecord chunk, string value = "Sword")
    {
        IContentTextAuthoringStore text = store;
        await store.ClearDraftFreezeAsync();
        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(0);
        var twin = new InMemoryContentAuthoringStore(TextRegistry());
        await twin.ApplyEditsAsync(snapshot.Draft.Changes.Edits, Actor, Operator, "twin");
        ContentPublishBaseline held = snapshot.Baseline;
        var baseline = new ContentPublishBaseline(
            held.VersionNumber, held.Rows, held.Rules, held.Chunks, new[] { new ManifestLanguageEntry("en-us", chunk.Hash) },
            held.MinimumServerBuild, held.MinimumClientBuild);
        ContentPublishPlan rowPlan = await new ContentPublisher(twin, store, TextRegistry()).PrepareAsync(Request(0), baseline);
        Assert.True(rowPlan.IsValid);
        int id = rowPlan.LiveRows.Single(row => row.Row.Key.Equals(Sword)).Row.Id;
        var revision = new ContentTextRevision(Item, id, NameField, "en-us", value, 1, null);
        return new ContentTextPublishPlan(rowPlan, snapshot, new ContentTextCandidate(
            new[] { revision }, new[] { new ContentTextLanguageDeclaration("en-us", "en-us") },
            Array.Empty<ContentTextRevision>(), new[] { revision }), new[] { chunk });
    }

    sealed class Codec(ContentFieldSchema schema) : ContentRowCodecBase(new ContentTypeId(1024), schema);
}
