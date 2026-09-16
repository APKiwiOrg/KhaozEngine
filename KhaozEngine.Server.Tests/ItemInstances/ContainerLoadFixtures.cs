using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Catalog;
using KhaozEngine.Diagnostics;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// The content, the registries and the stored sections the container load facts run over.
/// <para>
/// The snapshot is built through <see cref="ContentSnapshotBuilder"/> and the sections through the real
/// <see cref="JournalProjectionSection"/>, so the load path meets the seams it will meet in a server rather
/// than a pair of fakes shaped to agree with it. The pages are encoded by
/// <see cref="ItemContainerPageCodec"/> for the same reason: a fact about what the load path does with a
/// stored page is only a fact if the page is stored the way the codec stores one.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no test using it needs a collection attribute.
/// </para>
/// </summary>
internal static class ContainerLoadFixtures
{
    /// <summary>The stream every fixture section is filed under.</summary>
    public const string StreamKey = "player:42";

    /// <summary>The container every fixture section names, spec 5.2's own example.</summary>
    public const string Container = "bank";

    /// <summary>The projection schema a container page is filed under.</summary>
    public const string Schema = "item-container";

    /// <summary>The page geometry, spec 5.2's hundred.</summary>
    public const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>The content version the fixture snapshot publishes at.</summary>
    public const int ActiveVersion = 7;

    /// <summary>A live item base.</summary>
    public const int Sword = 100;

    /// <summary>A live item base a socket contains.</summary>
    public const int Gem = 102;

    /// <summary>A live mod row an affix entry names.</summary>
    public const int Mod = 500;

    /// <summary>A second live mod row, so an affix list can hold two.</summary>
    public const int SecondMod = 501;

    /// <summary>The authored tier ordinal <see cref="Mod"/> really has.</summary>
    public const byte Tier = 3;

    /// <summary>A live socket type row.</summary>
    public const int SocketType = 700;

    /// <summary>An id no row of any type carries, which is what an unresolved reference names.</summary>
    public const int MissingId = 9_999;

    /// <summary>The instance id a payload-carrying entry takes unless a fact needs its own.</summary>
    public const long Instance = 7_001;

    /// <summary>The <c>mod</c> content type key a remap rule names.</summary>
    public const string ModKey = "mod";

    /// <summary>The <c>item</c> content type key a remap rule names.</summary>
    public const string ItemKey = EngineContentTypes.ItemTypeKey;

    const ushort ModTypeId = 256;
    const ushort ModTierTypeId = 257;
    const ushort SocketTypeTypeId = 258;

    /// <summary>A fresh property registry, never shared, so no test registers into another's.</summary>
    public static InstancePropertyRegistry Properties() => InstancePropertyRegistry.CreateV1();

    /// <summary>The engine types plus the Scope B stand-ins this fixture references, fresh per call.</summary>
    public static ContentTypeRegistry Types()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        Instances(registry, ModTypeId, ModKey, Fields(("kind", ContentFieldKind.Int)));
        Instances(
            registry,
            ModTierTypeId,
            "mod_tier",
            Fields(("mod_id", ContentFieldKind.Int), ("ordinal", ContentFieldKind.Int)));
        Instances(registry, SocketTypeTypeId, "socket_type", Fields(("width", ContentFieldKind.Int)));
        return registry;
    }

    /// <summary>One content type's id, looked up by KEY the way the pass looks it up.</summary>
    public static ContentTypeId Type(ContentTypeRegistry types, string typeKey)
    {
        Assert.True(types.TryGetByKey(typeKey, out ContentTypeRegistration? registration), typeKey);
        return registration!.Type;
    }

    /// <summary>
    /// The fixture content: two item bases, two mods with a tier each, and one socket type.
    /// <paramref name="extraMods"/> publishes the ids a remap rule moves an item ONTO, which is what a
    /// rescued entry needs to resolve against.
    /// </summary>
    public static ContentSnapshot Snapshot(ContentTypeRegistry types, params int[] extraMods)
    {
        var builder = new ContentSnapshotBuilder(types);
        builder.WithIdentity(ActiveVersion, "fixture");
        builder.AddRow(Item(types, Sword, "sword", 20));
        builder.AddRow(Item(types, Gem, "ruby", 1));
        builder.AddRow(Row(types, ModTypeId, Mod, "fine", ("kind", 1)));
        builder.AddRow(Row(types, ModTypeId, SecondMod, "sharp", ("kind", 2)));
        builder.AddRow(TierRow(types, 1, Mod, Tier));
        builder.AddRow(TierRow(types, 2, SecondMod, Tier));
        int rowId = 3;
        foreach (int mod in extraMods)
        {
            builder.AddRow(Row(types, ModTypeId, mod, FormattableString.Invariant($"mod_{mod}"), ("kind", 3)));
            builder.AddRow(TierRow(types, rowId++, mod, Tier));
        }

        builder.AddRow(Row(types, SocketTypeTypeId, SocketType, "gem"));
        return builder.Build();
    }

    /// <summary>The load context every fact builds, with the two sinks left off unless it reads them.</summary>
    public static ContainerLoadContext Context(
        ContentTypeRegistry types,
        InstancePropertyRegistry properties,
        RemapRuleSet? rules = null,
        ILogger? logger = null,
        Action<int, string>? counter = null,
        string container = Container)
        => new(
            StreamKey,
            container,
            properties,
            types,
            rules ?? new RemapRuleSet(Array.Empty<RemapRule>()),
            static definitionId => definitionId != 0,
            logger,
            counter);

    /// <summary>One stored section, the way a projection read hands one back.</summary>
    public static JournalProjectionSection Section(int pageIndex, byte[] page, string? name = null)
        => new(
            StreamKey,
            name ?? ContainerSectionNames.Format(Container, pageIndex),
            sourceVersion: 1,
            Schema,
            ItemContainerPageCodec.Version,
            page,
            DateTimeOffset.UnixEpoch);

    /// <summary>One section holding a page of the given entries, encoded by the real codec.</summary>
    public static JournalProjectionSection Page(int pageIndex, int stamp, params PageSlotInput[] entries)
        => Section(
            pageIndex,
            ItemContainerPageCodec.Encode(
                pageIndex, ItemContainerPage.FirstSlotOf(pageIndex), PageSlots, stamp, entries));

    /// <summary>One page entry on the way in, with the defaults a clean stack takes.</summary>
    public static PageSlotInput Slot(
        int slot,
        int definitionId,
        int count = 1,
        long instanceId = 0,
        byte[]? payload = null,
        uint flags = 0)
        => new(slot, flags, definitionId, count, instanceId, payload ?? Array.Empty<byte>());

    /// <summary>One entry carrying a quarantine wrapper, which is what a page that has already been through a
    /// load holds.</summary>
    public static PageSlotInput Wrapped(
        int slot, int definitionId, string reason, int stampedVersion, byte[] original, long instanceId = Instance)
        => new(
            slot,
            ItemContainerPageCodec.EntryFlagQuarantined,
            definitionId,
            1,
            instanceId,
            QuarantineWrapper.Wrap(reason, stampedVersion, original));

    /// <summary>The bytes a builder writes, which is the only way a payload is made here.</summary>
    public static byte[] Encode(ItemInstancePayloadBuilder builder)
    {
        byte[] bytes = new byte[builder.Length];
        ItemInstancePayload.Encode(builder, bytes);
        return bytes;
    }

    /// <summary>An affixed payload: one affix naming a mod at a tier.</summary>
    public static byte[] AffixPayload(int modId = Mod, byte tier = Tier)
        => Encode(new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 42)
            .AddAffixes(InstancePropertyKind.Affixes, [new InstanceAffix(modId, tier, 17)]));

    /// <summary>A socketed payload: one socket holding a contained definition and its own nested payload.</summary>
    public static byte[] SocketPayload(int containedDefinitionId = Gem, int socketTypeId = SocketType)
    {
        byte[] nested = Encode(new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.Quality, 5));
        return Encode(new ItemInstancePayloadBuilder().AddSockets(
            [new InstanceSocket(socketTypeId, containedDefinitionId, 4242, nested)]));
    }

    /// <summary>Contracts 8.2 kind 1: every reference to <paramref name="fromId"/> becomes
    /// <paramref name="toId"/>.</summary>
    public static RemapRule Replaced(int sequence, int introducedIn, ContentTypeId type, int fromId, int toId)
        => new(sequence, introducedIn, type, RemapRuleKind.ReplacedBy, fromId, toId, ReadOnlySpan<byte>.Empty);

    /// <summary>The rule set, in sequence order, which is the order the pass applies it in.</summary>
    public static RemapRuleSet Rules(params RemapRule[] rules) => new(rules);

    /// <summary>The findings of one kind, which is what a fact asserts over.</summary>
    public static List<ContainerLoadFinding> OfKind(this ContainerLoadResult result, ContainerLoadFindingKind kind)
    {
        var found = new List<ContainerLoadFinding>();
        foreach (ContainerLoadFinding finding in result.Findings)
        {
            if (finding.Kind == kind) found.Add(finding);
        }

        return found;
    }

    /// <summary>One page of a result, which every multi page fact reads by index rather than by position.</summary>
    public static ItemContainerPage PageAt(this ContainerLoadResult result, int pageIndex)
    {
        Assert.True(result.TryGetPage(pageIndex, out ItemContainerPage? page), Describe(result));
        return page!;
    }

    /// <summary>Every finding, rendered for an assertion message.</summary>
    public static string Describe(ContainerLoadResult result)
    {
        var parts = new List<string>();
        foreach (ContainerLoadFinding finding in result.Findings)
        {
            parts.Add(FormattableString.Invariant(
                $"{finding.Kind} page {finding.PageIndex} slot {finding.Slot} {finding.Reason}"));
        }

        foreach (InstanceValidationReport report in result.Reports)
        {
            foreach (InstanceValidationFinding finding in report.Findings)
            {
                parts.Add(FormattableString.Invariant(
                    $"sweep page {report.PageIndex} slot {finding.Slot} check {finding.Check} {finding.Reason}"));
            }
        }

        return parts.Count == 0 ? "no findings" : string.Join(", ", parts);
    }

    static ContentRow Item(ContentTypeRegistry types, int id, string key, int maxStack)
        => Row(
            types,
            EngineContentTypes.ItemTypeId,
            id,
            key,
            (ItemContentType.StackableField, maxStack > 1 ? 1 : 0),
            (ItemContentType.MaxStackField, maxStack),
            (ItemContentType.TradableField, 1),
            (ItemContentType.ValueField, 10));

    static ContentRow TierRow(ContentTypeRegistry types, int id, int modId, int ordinal)
        => Row(
            types,
            ModTierTypeId,
            id,
            FormattableString.Invariant($"tier_{id}"),
            ("mod_id", modId),
            ("ordinal", ordinal));

    static ContentRow Row(
        ContentTypeRegistry types,
        ushort typeId,
        int id,
        string key,
        params (string Name, long Value)[] numbers)
    {
        Assert.True(types.TryGet(new ContentTypeId(typeId), out ContentTypeRegistration? registration));
        IReadOnlyList<ContentFieldEntry> schema = registration!.Schema.Fields;
        var fields = new ContentFieldValue[schema.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            ContentFieldEntry field = schema[i];
            fields[i] = ContentFieldValue.Absent(field.Kind);
            foreach ((string name, long value) in numbers)
            {
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                {
                    fields[i] = ContentFieldValue.OfNumber(field.Kind, value);
                }
            }
        }

        return new ContentRow(new ContentTypeId(typeId), id, new ContentKey(key), 0, false, fields);
    }

    static ContentFieldSchema Fields(params (string Name, ContentFieldKind Kind)[] fields)
    {
        var entries = new ContentFieldEntry[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            entries[i] = new ContentFieldEntry(fields[i].Name, fields[i].Kind, null, ContentVisibility.Client, false);
        }

        return new ContentFieldSchema(entries);
    }

    static void Instances(ContentTypeRegistry registry, ushort typeId, string typeKey, ContentFieldSchema schema)
        => registry.RegisterContentType(
            ContentRegistrationBand.Instances,
            typeId,
            typeKey,
            new FixtureCodec(schema),
            null,
            schema,
            ContentVisibility.Client,
            ContentTypeRegistry.MinChunkSlots);

    /// <summary>
    /// A codec that declares the schema's written fields and nothing else. Registration compares the two
    /// sets and a stand-in type never reaches a chunk, so neither direction of the codec is ever called.
    /// </summary>
    sealed class FixtureCodec : IContentRowCodec
    {
        public FixtureCodec(ContentFieldSchema schema)
        {
            var written = new List<string>();
            foreach (ContentFieldEntry field in schema.Fields)
            {
                if (!field.IsDerivedMarker) written.Add(field.Name);
            }

            WrittenFields = written;
        }

        public IReadOnlyList<string> WrittenFields { get; }

        public void Encode(ContentRow row, IBufferWriter<byte> destination)
            => throw new NotSupportedException("A fixture stand-in type is never packed.");

        public bool TryDecode(ReadOnlySpan<byte> body, [MaybeNullWhen(false)] out ContentRow row, out string? reason)
        {
            row = null;
            reason = "fixture";
            return false;
        }
    }
}

/// <summary>
/// A snapshot that wraps another and COUNTS every row read, which is how the one-pass fact is pinned: a load
/// that swept a page twice reads every row of it twice.
/// </summary>
internal sealed class CountingSnapshot : IContentSnapshot
{
    readonly IContentSnapshot _inner;

    /// <summary>Wraps the real fixture snapshot, so what is counted is the reads the checks actually make.</summary>
    /// <param name="inner">The snapshot every call is forwarded to.</param>
    public CountingSnapshot(IContentSnapshot inner) => _inner = inner;

    /// <summary>How many rows have been read since this wrapper was built.</summary>
    public int Reads { get; private set; }

    /// <summary>The active version, which nothing counts.</summary>
    public int VersionNumber => _inner.VersionNumber;

    /// <summary>The identity pair.</summary>
    public ContentVersionIdentity Identity => _inner.Identity;

    /// <summary>The rule set the snapshot carries, which the load path never reads: rules arrive by argument.</summary>
    public IReadOnlyList<RemapRule> Rules => _inner.Rules;

    /// <summary>Counts the read and forwards it.</summary>
    public bool TryGetRow(ContentTypeId type, int id, [MaybeNullWhen(false)] out ContentRow row)
    {
        Reads++;
        return _inner.TryGetRow(type, id, out row);
    }

    /// <summary>Forwards, counting nothing: no check makes this lookup.</summary>
    public bool TryGetId(ContentTypeId type, ContentKey key, out int id) => _inner.TryGetId(type, key, out id);

    /// <summary>Forwards. The tier index is built once per sweep, so it is counted as one read of the type.</summary>
    public IReadOnlyList<ContentRow> Rows(ContentTypeId type)
    {
        Reads++;
        return _inner.Rows(type);
    }

    /// <summary>Forwards, counting the read, because check 13 is a read like any other.</summary>
    public bool IsRetired(ContentTypeId type, int id)
    {
        Reads++;
        return _inner.IsRetired(type, id);
    }
}

/// <summary>Records what the one log line says, so a fact can assert on the line an operator would read.</summary>
internal sealed class RecordingLogger : ILogger
{
    /// <summary>Every entry, in order.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    /// <summary>The category contracts 10.2 names.</summary>
    public string Category => InstanceValidationTelemetry.LogCategory;

    /// <summary>Everything is enabled, so nothing is lost to a level filter.</summary>
    public bool IsEnabled(LogLevel level) => true;

    /// <summary>Records one entry.</summary>
    public void Log(LogLevel level, string message, Exception? exception = null) => Entries.Add((level, message));

    /// <summary>Records at trace.</summary>
    public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);

    /// <summary>Records at debug.</summary>
    public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);

    /// <summary>Records at info.</summary>
    public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);

    /// <summary>Records at warn, which is the level the one line is emitted at.</summary>
    public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);

    /// <summary>Records at error.</summary>
    public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);

    /// <summary>Records at fatal.</summary>
    public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
}
