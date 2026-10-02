using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// One game-band item type carrying two CLIENT text markers and one SERVER-only marker, plus the helpers the
/// text authoring suites share. The type is deliberately small so a failed assertion reads in one line.
/// <para>
/// The row plan a text commit needs is prepared through the ordinary publisher over a ROW-ONLY twin draft,
/// with the store under test as the id persistence. That keeps the allocation marks on the store that commits
/// the version, and it is how these suites build a supplied plan before the text publisher exists.
/// </para>
/// </summary>
internal static class TextAuthoringFixtures
{
    public const string Actor = "text-tests";
    public const string Operator = "oid:text";
    public const string NameField = "name";
    public const string DescriptionField = "description";
    public const string SecretField = "secret_name";
    public const string LegacyField = "legacy";

    public static readonly ContentTypeId Item = new(1024);
    public static readonly ContentKey Sword = new("sword");
    public static readonly ContentTextTarget Name = new(Item, Sword, NameField, "EN-us");

    public static ContentFieldSchema Schema() => new(new ContentFieldEntry[]
    {
        new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new(LegacyField, ContentFieldKind.Bool, null, ContentVisibility.Client, false),
        new(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
        new(DescriptionField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false),
        new(SecretField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.ServerOnly, false),
    });

    public static ContentTypeRegistry TextRegistry(ContentVisibility visibility = ContentVisibility.Client)
    {
        ContentFieldSchema schema = Schema();
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(
            ContentRegistrationBand.Game, Item.Value, "item", new Codec(Item, schema), null, schema, visibility, 256);
        return registry;
    }

    public static InMemoryContentAuthoringStore TextStore(Func<DateTimeOffset>? clock = null)
        => new(TextRegistry(), clock: clock);

    public static InMemoryContentAuthoringStore TextStore(IPackStore pack, Func<DateTimeOffset>? clock = null)
        => new(TextRegistry(), pack, clock);

    public static ContentEdit Add(string key = "sword", int value = 1)
        => ContentEdit.Add(Item, new ContentKey(key), new[] { Value(value) });

    public static ContentFieldEdit Value(int value)
        => new("value", ContentFieldValue.OfNumber(ContentFieldKind.Int, value));

    public static ContentTextTarget Target(string field, string language, string key = "sword")
        => new(Item, new ContentKey(key), field, language);

    public static ContentAuthoringChanges Changes(IEnumerable<ContentEdit>? rows, params ContentTextEdit[] text)
        => new((rows ?? Array.Empty<ContentEdit>()).ToArray(), text);

    public static Task<ContentDraft> ApplyAsync(
        IContentTextAuthoringStore store, IEnumerable<ContentEdit>? rows, params ContentTextEdit[] text)
        => store.ApplyChangesAsync(Changes(rows, text), Actor, Operator, "text tests");

    public static async Task<int> AuditCountAsync(IContentAuthoringStore store)
        => (await store.ListAuditAsync(default, 0, 0, 500)).Count;

    public static ContentPublishRequest Request(int expectedBaseVersion)
        => new(Actor, Operator, "text publish", expectedBaseVersion);

    /// <summary>A UTF-8 value of exactly <paramref name="bytes"/> bytes carrying one astral character.</summary>
    public static string Utf8Value(int bytes)
    {
        string astral = "\U0001F600";
        string value = new string('a', 4090) + astral;
        int remaining = bytes - Encoding.UTF8.GetByteCount(value);
        return value + new string('b', remaining);
    }

    /// <summary>
    /// Freezes the store's complete draft and builds the ordinary row plan for it over a row-only twin, whose
    /// baseline names <paramref name="languages"/> so both manifests carry the text chunk list.
    /// </summary>
    public static async Task<(ContentTextPublishSnapshot Snapshot, ContentPublishPlan RowPlan)> FreezeAndPlanAsync(
        InMemoryContentAuthoringStore store, IReadOnlyList<ManifestLanguageEntry> languages)
    {
        IContentTextAuthoringStore text = store;
        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(
            await store.GetActiveVersionAsync());

        var twin = new InMemoryContentAuthoringStore(TextRegistry());
        await twin.ApplyEditsAsync(snapshot.Draft.Changes.Edits, Actor, Operator, "twin");
        var publisher = new ContentPublisher(twin, store, TextRegistry());
        ContentPublishBaseline held = snapshot.Baseline;
        var baseline = new ContentPublishBaseline(
            held.VersionNumber, held.Rows, held.Rules, held.Chunks, languages,
            held.MinimumServerBuild, held.MinimumClientBuild);
        ContentPublishPlan plan = await publisher.PrepareAsync(Request(held.VersionNumber), baseline);
        if (!plan.IsValid)
        {
            throw new InvalidOperationException("The fixture row plan did not validate.");
        }

        return (snapshot, plan);
    }

    /// <summary>One language's placeholder chunk, hashed through the real text subdomain.</summary>
    public static ContentTextChunkRecord Chunk(string wireTag, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        return new ContentTextChunkRecord(wireTag, ContentHash.OfTextChunk(bytes), bytes, false);
    }

    /// <summary>The definition id the row plan allocated to <paramref name="key"/>.</summary>
    public static int IdOf(ContentPublishPlan plan, string key)
        => plan.LiveRows.Single(row => row.Row.Key.Equals(new ContentKey(key))).Row.Id;

    /// <summary>
    /// Commits one mixed draft holding an add of <paramref name="key"/> and a Set of its name in English, through
    /// the companion, and returns the committed version.
    /// </summary>
    public static async Task<ContentVersionRecord> PublishNamedRowAsync(
        InMemoryContentAuthoringStore store, string key, string name)
    {
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, new[] { Add(key) }, ContentTextEdit.Set(Target(NameField, "en", key), name));
        ContentTextChunkRecord chunk = Chunk("en", name);
        (ContentTextPublishSnapshot snapshot, ContentPublishPlan rowPlan) = await FreezeAndPlanAsync(
            store, new[] { new ManifestLanguageEntry("en", chunk.Hash) });
        int version = rowPlan.VersionNumber;
        var revision = new ContentTextRevision(Item, IdOf(rowPlan, key), NameField, "en", name, version, null);
        var candidate = new ContentTextCandidate(
            Merge(snapshot.BaselineText.Revisions, revision),
            new[] { new ContentTextLanguageDeclaration("en", "en") },
            Array.Empty<ContentTextRevision>(),
            new[] { revision });
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, candidate, new[] { chunk });
        return await text.CommitTextPublishAsync(plan, Request(snapshot.BaseVersion), null);
    }

    static ContentTextRevision[] Merge(IReadOnlyList<ContentTextRevision> held, ContentTextRevision added)
        => held.Append(added).ToArray();

    sealed class Codec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema);
}
