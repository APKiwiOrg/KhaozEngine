using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// One content type as the publish suites declare it: its id, its key, its default visibility, whether it
/// carries a per-field <c>ServerOnly</c> override, and its chunk slot count.
/// </summary>
/// <param name="TypeId">The stable numeric type id, always in the game band.</param>
/// <param name="TypeKey">The stable string type key.</param>
/// <param name="Visibility">The type's default visibility.</param>
/// <param name="HasSecret">Whether the schema carries the optional <c>ServerOnly</c> field.</param>
/// <param name="ChunkSlots">Id slots per chunk.</param>
internal sealed record PublishTypeSpec(
    ushort TypeId,
    string TypeKey,
    ContentVisibility Visibility = ContentVisibility.Client,
    bool HasSecret = false,
    int ChunkSlots = PublishFixtures.ChunkSlots);

/// <summary>
/// The registries, stores, edits and publishers the three publish suites share. Everything is built in
/// memory against <see cref="InMemoryContentAuthoringStore"/>, so no test here touches a file or a database.
/// <para>
/// The types are GAME band, because the publish rules under test are about visibility and chunk identity
/// rather than about any engine type's own schema, and a two-field type makes a chunk's bytes readable in a
/// failure message. The engine registry is used where a suite needs a <c>ServerOnly</c> TYPE, which the
/// engine already declares in <c>loot_table</c> and <c>loot_entry</c>.
/// </para>
/// </summary>
internal static class PublishFixtures
{
    /// <summary>The first id the game band is entitled to, which the plain type takes.</summary>
    public const ushort ThingTypeId = 1024;

    /// <summary>The plain type's key.</summary>
    public const string ThingTypeKey = "thing";

    /// <summary>A second game-band type, for the manifest's multi-type ordering.</summary>
    public const ushort OtherTypeId = 1025;

    /// <summary>The second type's key.</summary>
    public const string OtherTypeKey = "other_thing";

    /// <summary>The <c>Client</c> field every fixture type carries.</summary>
    public const string ValueField = "value";

    /// <summary>The optional <c>Bool</c> field a fork sets on its copy. The field is the caller's to name.</summary>
    public const string LegacyField = "legacy";

    /// <summary>The optional <c>ServerOnly</c> field the visibility suite turns on.</summary>
    public const string SecretField = "secret";

    /// <summary>
    /// The smallest legal slot count, so two chunks are reachable with ids a test can read: ids 1 and 2 sit
    /// in chunk 0 and id 300 sits in chunk 1.
    /// </summary>
    public const int ChunkSlots = 256;

    /// <summary>An id in chunk 1 at <see cref="ChunkSlots"/>, which every reuse test edits.</summary>
    public const int SecondChunkId = 300;

    /// <summary>The actor every fixture edit and publish carries.</summary>
    public const string Actor = "publish-tests";

    /// <summary>
    /// The schema of a fixture type: one required Client int, the optional Bool a fork flags its copy with,
    /// and the optional ServerOnly int the visibility suite turns on. The ServerOnly field is LAST, so a
    /// reader counting positions in a row body does not have to hold two layouts in their head.
    /// </summary>
    public static ContentFieldSchema Schema(bool hasSecret)
    {
        var fields = new List<ContentFieldEntry>
        {
            new(ValueField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new(LegacyField, ContentFieldKind.Bool, null, ContentVisibility.Client, false),
        };

        if (hasSecret)
        {
            fields.Add(new ContentFieldEntry(SecretField, ContentFieldKind.Int, null, ContentVisibility.ServerOnly, false));
        }

        return new ContentFieldSchema(fields);
    }

    /// <summary>A registry carrying the given types IN THE ORDER they are handed in, and nothing else.</summary>
    public static ContentTypeRegistry Registry(params PublishTypeSpec[] specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        var registry = new ContentTypeRegistry();
        foreach (PublishTypeSpec spec in specs)
        {
            Register(registry, spec);
        }

        return registry;
    }

    /// <summary>Adds one more type to a registry that is already carrying some, which is the add-a-type case.</summary>
    public static void Register(ContentTypeRegistry registry, PublishTypeSpec spec, IContentRowCodec? codec = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(spec);

        ContentFieldSchema schema = Schema(spec.HasSecret);
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            spec.TypeId,
            spec.TypeKey,
            codec ?? new PublishCodec(new ContentTypeId(spec.TypeId), schema),
            null,
            schema,
            spec.Visibility,
            spec.ChunkSlots);
    }

    /// <summary>The one plain type every ordinary publish test uses.</summary>
    public static PublishTypeSpec Thing => new(ThingTypeId, ThingTypeKey);

    /// <summary>The plain type with the per-field <c>ServerOnly</c> override turned on.</summary>
    public static PublishTypeSpec SecretThing => new(ThingTypeId, ThingTypeKey, HasSecret: true);

    /// <summary>A second plain type, for a publish that spans two types.</summary>
    public static PublishTypeSpec Other => new(OtherTypeId, OtherTypeKey);

    /// <summary>The field edits one fixture row carries.</summary>
    public static ContentFieldEdit[] Fields(int value, int? secret = null)
    {
        var edits = new List<ContentFieldEdit>
        {
            new(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, value)),
        };

        if (secret is int held)
        {
            edits.Add(new ContentFieldEdit(SecretField, ContentFieldValue.OfNumber(ContentFieldKind.Int, held)));
        }

        return edits.ToArray();
    }

    /// <summary>A store over one registry, which is also the id persistence the publisher allocates through.</summary>
    public static InMemoryContentAuthoringStore Store(ContentTypeRegistry registry)
        => new(registry);

    /// <summary>A store that can PUBLISH, which needs the pack store step 9 writes its files to.</summary>
    public static InMemoryContentAuthoringStore Store(ContentTypeRegistry registry, IPackStore packStore)
        => new(registry, packStore);

    /// <summary>The commit half over one store, one pack target and a publisher carrying the step hook.</summary>
    public static ContentPublishCommit Commit(
        InMemoryContentAuthoringStore store,
        IPackStore packStore,
        ContentTypeRegistry registry,
        Action<ContentPublishStep>? onStep = null)
        => new(store, packStore, Publisher(store, registry, onStep));

    /// <summary>A publisher over one store, with the step hook and the row encoder both optional.</summary>
    public static ContentPublisher Publisher(
        InMemoryContentAuthoringStore store,
        ContentTypeRegistry registry,
        Action<ContentPublishStep>? onStep = null,
        IContentRowSideEncoder? rowEncoder = null)
        => new(store, store, registry, onStep, rowEncoder);

    /// <summary>The request every fixture publish sends, naming the base version it expects.</summary>
    public static ContentPublishRequest Request(
        int expectedBaseVersion,
        int? minimumServerBuild = null,
        int? minimumClientBuild = null)
        => new(Actor, "oid:tests", "publish tests", expectedBaseVersion, minimumServerBuild, minimumClientBuild);

    /// <summary>Applies edits to the open draft, which is what a console does before it publishes.</summary>
    public static Task ApplyAsync(InMemoryContentAuthoringStore store, params ContentEdit[] edits)
        => store.ApplyEditsAsync(edits, Actor, "oid:tests", "publish tests");

    /// <summary>
    /// Applies edits, prepares steps 1 to 8, and DISCARDS the draft afterwards, which is what the commit of
    /// task 16 will do. Chaining publishes needs it, because nothing here writes anything durable.
    /// </summary>
    public static async Task<ContentPublishPlan> PublishAsync(
        InMemoryContentAuthoringStore store,
        ContentPublisher publisher,
        ContentPublishBaseline baseline,
        params ContentEdit[] edits)
    {
        await ApplyAsync(store, edits).ConfigureAwait(false);
        ContentPublishPlan plan = await publisher
            .PrepareAsync(Request(baseline.VersionNumber), baseline)
            .ConfigureAwait(false);
        await store.DiscardDraftAsync(Actor, "oid:tests").ConfigureAwait(false);
        return plan;
    }

    /// <summary>The one chunk at an address, failing loudly when the plan carries none.</summary>
    public static ContentChunkRecord Chunk(
        ContentPublishPlan plan,
        ushort typeId,
        int chunkIndex,
        ContentVisibility side)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Assert.True(
            plan.TryGetChunk(new ContentTypeId(typeId), chunkIndex, side, out ContentChunkRecord? record),
            FormattableString.Invariant(
                $"Expected a {side} chunk {chunkIndex} of type {typeId}. The plan carries {Describe(plan)}."));
        return record;
    }

    /// <summary>Every chunk address the plan carries, for an assertion message.</summary>
    public static string Describe(ContentPublishPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return string.Join(
            ", ",
            plan.Chunks.Select(chunk => FormattableString.Invariant(
                $"type {chunk.Type.Value} chunk {chunk.ChunkIndex} {chunk.Side} {(chunk.IsReused ? "reused" : "written")}")));
    }

    /// <summary>Every finding, one per line, which is what a failed publish assertion carries.</summary>
    public static string Findings(ContentPublishPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return "Findings: " + string.Join(
            " | ",
            plan.Validation.Findings.Select(finding => FormattableString.Invariant(
                $"{finding.Code} type {finding.Type.Value} id {finding.Id}: {finding.Message}")));
    }

    /// <summary>Fails with every finding named when the plan did not validate.</summary>
    public static ContentPublishPlan AssertValid(ContentPublishPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Assert.True(plan.IsValid, Findings(plan));
        return plan;
    }

    /// <summary>True when the plan carries a finding under the code.</summary>
    public static bool Has(ContentPublishPlan plan, string code)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Validation.Findings.Any(finding => string.Equals(finding.Code, code, StringComparison.Ordinal));
    }

    /// <summary>One decoded row out of a chunk's stored bytes, which is how a side's content is read back.</summary>
    public static ContentRow DecodeRow(
        ContentTypeRegistry registry,
        ContentChunkRecord record,
        int definitionId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(record);
        Assert.True(registry.TryGet(record.Type, out ContentTypeRegistration? registration));
        Assert.True(
            ContentChunkCodec.TryDecode(record.StoredFile.Span, registry, out ContentChunk? chunk, out string? reason),
            reason ?? "no reason");
        Assert.True(chunk.TryDecodeRow(definitionId, registration.Codec, out ContentRow? row, out reason), reason ?? "no reason");
        return row;
    }
}

/// <summary>The generic positional walk with nothing added, which is what a fixture type registers.</summary>
internal sealed class PublishCodec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema)
{
}

/// <summary>
/// A row encoder that writes the SAME bytes on both sides, which is the encoder defect <c>KEC0014</c>
/// exists to catch: a client chunk still carrying a field the schema marks <c>ServerOnly</c>. It is the
/// stub the visibility suite injects, because the publish-time refusal is the codec failing to honour the
/// schema and never the schema itself.
/// </summary>
internal sealed class LeakyRowEncoder : IContentRowSideEncoder
{
    public void Encode(
        ContentTypeRegistration type,
        ContentRow row,
        ContentVisibility side,
        IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(type);
        type.Codec.Encode(row, destination);
    }
}
