using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Tests.Catalog.Store;
using KhaozEngine.Tests.Catalog.Validation;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// One row as the pack writer takes it: a row through its type's codec, or the exact body bytes when a test
/// needs a chunk that VERIFIES and then will not decode, which is the one refusal that cannot be built out
/// of a well formed row.
/// </summary>
/// <param name="TypeKey">The type the row belongs to.</param>
/// <param name="Row">The row itself, which supplies the id and the retired bit either way.</param>
/// <param name="Body">The encoded body, or null to encode <paramref name="Row"/> through its codec.</param>
internal readonly record struct BootRow(string TypeKey, ContentRow Row, byte[]? Body = null);

/// <summary>
/// One healthy published version, written through the shipped encoders into a real
/// <see cref="FileSystemPackStore"/> on disk, plus the handles to break exactly ONE thing about it. Every
/// refusal test starts from a pack that boots and takes one property away, so a red test names the property
/// rather than the fixture.
/// <para>
/// <b>The manifest carries one type entry per REGISTERED type</b>, with an empty chunk list for a type this
/// version has no rows of, because that is what boot step 6's mirror check reads. The shipped publisher
/// builds its entries from the chunk list instead and so omits an empty type, which is
/// https://github.com/APKiwiOrg/KhaozEngine/issues/937 and is the publish half of the same rule.
/// </para>
/// </summary>
internal sealed class BootPack : IDisposable
{
    /// <summary>The version every fixture publishes, and the number the pointer is filed under.</summary>
    public const int VersionNumber = 7;

    /// <summary>The server build the boot is told it is running, comfortably over the pack's minimum.</summary>
    public const int ServerBuild = 20;

    /// <summary>The minimum server build the pack demands.</summary>
    public const uint MinimumServerBuild = 11;

    /// <summary>What the operator's manifest line calls this store.</summary>
    public const string StoreName = "the test store";

    readonly TemporaryRoot _root = new();
    readonly List<EncodedContentChunk> _chunks = [];

    BootPack(ContentTypeRegistry registry)
    {
        Registry = registry;
        PackStore = new FileSystemPackStore(_root.Path);
    }

    /// <summary>The registry the chunks were encoded against and the boot loads through.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The store on disk, which is also the version pointer source.</summary>
    public FileSystemPackStore PackStore { get; }

    /// <summary>The holder the boot publishes into, one per pack so no test shares one.</summary>
    public ContentRuntimeHolder Holder { get; } = new();

    /// <summary>The server manifest the pointer currently names.</summary>
    public ContentManifest Manifest { get; private set; } = null!;

    /// <summary>That manifest's content address.</summary>
    public string ManifestHash { get; private set; } = string.Empty;

    /// <summary>The client manifest's address, which the pointer carries and the boot never reads.</summary>
    public string ClientManifestHash { get; private set; } = string.Empty;

    /// <summary>Every chunk written, in the order they were encoded.</summary>
    public IReadOnlyList<EncodedContentChunk> Chunks => _chunks;

    /// <summary>A content address no object in the store is filed under.</summary>
    public static string AbsentHash => new('a', 64);

    /// <summary>A second one, for the test that needs two.</summary>
    public static string OtherHash => new('b', 64);

    /// <summary>
    /// The clean version: one tag, one item carrying it, one stat, one loot table and the entry that draws
    /// from it. Every required field is set and every reference resolves, so the validator finds nothing
    /// beyond the <c>KEC0000</c> that says <c>previous</c> was null.
    /// </summary>
    public static IReadOnlyList<BootRow> CleanRows() =>
    [
        new(EngineContentTypes.TagTypeKey, ContentValidationFixtures.Tag(1, "metal")),
        new(EngineContentTypes.ItemTypeKey, ContentValidationFixtures.Item(7, "sword", tagIds: [1])),
        new(EngineContentTypes.StatTypeKey, ContentValidationFixtures.Stat(3, "attack")),
        new(EngineContentTypes.LootTableTypeKey, ContentValidationFixtures.LootTable(100, "goblin")),
        new(
            EngineContentTypes.LootEntryTypeKey,
            ContentValidationFixtures.LootEntry(500, "goblin_sword", table: 100, item: 7)),
    ];

    /// <summary>Publishes a version into a fresh store and points at it.</summary>
    /// <param name="registry">The registry to encode against, or null for every engine type.</param>
    /// <param name="rows">The rows to publish, or null for <see cref="CleanRows"/>.</param>
    public static async Task<BootPack> CreateAsync(
        ContentTypeRegistry? registry = null,
        IReadOnlyList<BootRow>? rows = null)
    {
        var pack = new BootPack(registry ?? ContentValidationFixtures.EngineRegistry());
        await pack.PublishAsync(rows ?? CleanRows());
        return pack;
    }

    /// <summary>The options a boot is handed, with every knob a refusal test moves.</summary>
    public ContentBootOptions Options(
        int serverBuild = ServerBuild,
        int? configuredVersion = VersionNumber,
        IContentVersionDirectory? directory = null,
        IPackStore? store = null,
        IReadOnlyList<ContentWorldKeyReference>? worldKeys = null)
        => new()
        {
            Registry = Registry,
            Store = store ?? PackStore,
            Holder = Holder,
            ServerBuild = serverBuild,
            ConfiguredVersion = configuredVersion,
            Directory = directory,

            // Always the store on disk, so a decorated store still resolves the pointer through the one
            // provider that holds it.
            Pointers = PackStore,
            StoreName = StoreName,
            WorldKeys = worldKeys ?? [],
        };

    /// <summary>Stores a manifest and points the version at it, which is how one property is broken.</summary>
    public async Task<string> RepointAsync(ContentManifest manifest, int versionNumber = VersionNumber)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        byte[] file = ContentManifestCodec.Encode(manifest);
        string hash = ContentManifestText.Hash(manifest);
        if (manifest.FormatGeneration > ContentPackFormat.Generation)
        {
            // The store VERIFIES what it is handed by decoding it, and the decoder refuses a generation
            // above this build's, so a pack from a newer engine can only be filed the way it arrived: as
            // bytes, under the address they digest to.
            WriteUnverified(hash, file);
        }
        else
        {
            await PackStore.PutAsync(hash, file);
        }

        await PackStore.PutVersionPointerAsync(versionNumber, hash, ClientManifestHash);
        Manifest = manifest;
        ManifestHash = hash;
        return hash;
    }

    /// <summary>Points the version at an address, whatever is or is not filed under it.</summary>
    public Task PointAtAsync(string serverManifestHash, int versionNumber = VersionNumber)
        => PackStore.PutVersionPointerAsync(versionNumber, serverManifestHash, ClientManifestHash);

    /// <summary>
    /// Files bytes under a name they do NOT digest to, which the store itself refuses, so the verify-on-read
    /// half has something to catch.
    /// </summary>
    public void WriteUnverified(string hash, ReadOnlySpan<byte> bytes)
    {
        string path = PackStore.PathFor(hash);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.ToArray());
    }

    /// <summary>Deletes one object, which is how a chunk goes absent under a manifest that still names it.</summary>
    public Task<bool> DeleteAsync(string hash) => PackStore.DeleteAsync(hash);

    /// <inheritdoc />
    public void Dispose() => _root.Dispose();

    async Task PublishAsync(IReadOnlyList<BootRow> rows)
    {
        var byChunk = new SortedDictionary<(ushort Type, int Index), List<ContentChunkRow>>();
        var registrations = new Dictionary<ushort, ContentTypeRegistration>();
        for (int i = 0; i < rows.Count; i++)
        {
            BootRow row = rows[i];
            ContentTypeRegistration registration = Registration(row.TypeKey);
            registrations[registration.Type.Value] = registration;

            var key = (registration.Type.Value, row.Row.Id / registration.ChunkSlots);
            if (!byChunk.TryGetValue(key, out List<ContentChunkRow>? held))
            {
                held = [];
                byChunk[key] = held;
            }

            held.Add(new ContentChunkRow(
                row.Row.Id,
                row.Row.IsRetired,
                row.Body ?? CatalogSnapshotFixtures.Body(Registry, row.TypeKey, row.Row)));
        }

        var chunksByType = new Dictionary<ushort, List<ManifestChunkEntry>>();
        foreach (KeyValuePair<(ushort Type, int Index), List<ContentChunkRow>> pair in byChunk)
        {
            ContentTypeRegistration registration = registrations[pair.Key.Type];
            pair.Value.Sort(static (left, right) => left.DefinitionId.CompareTo(right.DefinitionId));
            EncodedContentChunk chunk = ContentChunkCodec.Encode(
                registration,
                pair.Key.Index,
                registration.DefaultVisibility,
                pair.Value);
            _chunks.Add(chunk);
            await PackStore.PutAsync(chunk.Hash, chunk.StoredFile);

            if (!chunksByType.TryGetValue(pair.Key.Type, out List<ManifestChunkEntry>? entries))
            {
                entries = [];
                chunksByType[pair.Key.Type] = entries;
            }

            entries.Add(new ManifestChunkEntry((uint)chunk.ChunkIndex, (uint)chunk.UncompressedBytes, chunk.Hash));
        }

        RemapRule[] rules = [];
        string ruleHash = ContentRuleChunkCodec.Hash(rules);
        await PackStore.PutAsync(ruleHash, ContentRuleChunkCodec.Encode(rules));

        var types = new List<ManifestTypeEntry>();
        IReadOnlyList<ContentTypeRegistration> registered = Registry.ByTypeId;
        for (int i = 0; i < registered.Count; i++)
        {
            ContentTypeRegistration registration = registered[i];
            types.Add(new ManifestTypeEntry(
                registration.Type.Value,
                registration.TypeKey,
                registration.ChunkSlots,
                registration.DefaultVisibility,
                chunksByType.TryGetValue(registration.Type.Value, out List<ManifestChunkEntry>? entries)
                    ? entries
                    : []));
        }

        ContentManifest server = BuildManifest(ContentManifestSide.Server, ruleHash, types);
        var clientTypes = new List<ManifestTypeEntry>();
        for (int i = 0; i < types.Count; i++)
        {
            if (types[i].Visibility != ContentVisibility.ServerOnly)
            {
                clientTypes.Add(types[i]);
            }
        }

        ContentManifest client = BuildManifest(ContentManifestSide.Client, ruleHash, clientTypes);
        ClientManifestHash = ContentManifestText.Hash(client);
        await PackStore.PutAsync(ClientManifestHash, ContentManifestCodec.Encode(client));
        await RepointAsync(server);
    }

    static ContentManifest BuildManifest(
        ContentManifestSide side,
        string ruleHash,
        IReadOnlyList<ManifestTypeEntry> types)
        => new()
        {
            Side = side,
            VersionNumber = VersionNumber,
            FormatGeneration = ContentPackFormat.Generation,
            MinimumServerBuild = MinimumServerBuild,
            MinimumClientBuild = 12,
            RemapRuleChunkHash = ruleHash,
            Types = types,
            Languages = [],
        };

    ContentTypeRegistration Registration(string typeKey)
    {
        Assert.True(Registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }
}

/// <summary>
/// The host half of the boot, which is where the exit lives. It writes the refusal to its own error stream
/// and RECORDS the exit code instead of calling <c>Environment.Exit</c>, which is what lets all twelve rows
/// of spec 9.6's table run in one assembly rather than one child process per row.
/// </summary>
internal sealed class BootHost
{
    readonly StringWriter _standardError = new();

    /// <summary>The code the host would have exited with, or null when it has not run a boot yet.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Every line written to the error stream, in order.</summary>
    public IReadOnlyList<string> Lines => _standardError.ToString()
        .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The one line a single-line refusal wrote.</summary>
    public string Line => Assert.Single(Lines);

    /// <summary>Runs the boot and takes the two things a host does with the result.</summary>
    public async Task<ContentBootResult> RunAsync(ContentBootOptions options)
    {
        ContentBootResult result = await ContentBoot.RunAsync(options);
        result.WriteStandardError(_standardError);
        ExitCode = result.ExitCode;
        return result;
    }
}

/// <summary>
/// An authoring database's two version numbers, which is boot step 2's second and third arm. A server that
/// reads none passes null instead of one of these.
/// </summary>
internal sealed class FakeVersionDirectory(int? pinned, int active) : IContentVersionDirectory
{
    /// <summary>How many times the pinned version was read, which pins the precedence short circuit.</summary>
    public int PinnedReads { get; private set; }

    /// <summary>How many times the active version was read.</summary>
    public int ActiveReads { get; private set; }

    /// <inheritdoc />
    public Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default)
    {
        PinnedReads++;
        return Task.FromResult(pinned);
    }

    /// <inheritdoc />
    public Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        ActiveReads++;
        return Task.FromResult(active);
    }
}

/// <summary>
/// A version record's two manifest hashes, answered for one version and null for every other, which is the
/// fact boot step 3 compares the pack pointer with.
/// </summary>
internal sealed class FakeHashSource(int version, ContentVersionHashes hashes) : IContentVersionHashSource
{
    /// <summary>How many times the record was read.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public Task<ContentVersionHashes?> GetVersionHashesAsync(
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult<ContentVersionHashes?>(versionNumber == version ? hashes : null);
    }
}

/// <summary>An index that cannot build what it was asked for, which fails the boot closed at step 7b.</summary>
internal sealed class BootThrowingIndex(ContentTypeId type) : IContentLoadIndex
{
    /// <inheritdoc />
    public ContentTypeId Type { get; } = type;

    /// <inheritdoc />
    public void Build(IContentSnapshot snapshot)
    {
        _ = snapshot;
        throw new InvalidOperationException("the store rows disagree with the item rows");
    }
}

/// <summary>An index that records that boot step 7b ran it, which is how the happy path pins the step.</summary>
internal sealed class BootRecordingIndex(ContentTypeId type) : IContentLoadIndex
{
    /// <inheritdoc />
    public ContentTypeId Type { get; } = type;

    /// <summary>How many times the boot built it.</summary>
    public int BuildCount { get; private set; }

    /// <summary>The item rows it could see when it ran, which is the runtime the boot handed it.</summary>
    public int ItemsSeen { get; private set; }

    /// <inheritdoc />
    public void Build(IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        BuildCount++;
        ItemsSeen = snapshot.Rows(new ContentTypeId(EngineContentTypes.ItemTypeId)).Count;
    }
}
