using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Validation;

/// <summary>
/// The content the validator tests are swept against, built through <see cref="ContentSnapshotBuilder"/>
/// rather than through a hand written fake, so checks 6, 7, 8, 12 and 13 run against the REAL
/// <see cref="IContentSnapshot"/> seam and a change to that seam breaks these facts rather than passing
/// them.
/// <para>
/// The six Scope B types are stand-ins registered here: spec 8's own <c>mod</c>, <c>mod_tier</c> and
/// friends are a later milestone's, and what the validator needs of them is a type key, a type id and, for
/// <c>mod_tier</c>, the two fields spec 8.3 names. Registering them in the INSTANCES band is what a real
/// Scope B registration will do, so nothing here is a shape the engine could not meet.
/// </para>
/// </summary>
internal static class InstanceValidationFixtures
{
    /// <summary>The page geometry every fixture page runs, spec 5.2's hundred.</summary>
    public const int PageSlots = ItemContainerPageCodec.ContainerPageSlots;

    /// <summary>The content version the fixture snapshot publishes at, and the stamp a clean page takes.</summary>
    public const int ActiveVersion = 7;

    /// <summary>A live item base, stackable to <see cref="LiveItemStackCap"/>.</summary>
    public const int LiveItem = 100;

    /// <summary>An item base that has been RETIRED, which check 13 is the only check to notice.</summary>
    public const int RetiredItem = 101;

    /// <summary>A live item base a socket contains, which is the other half of checks 6 and 13.</summary>
    public const int GemItem = 102;

    /// <summary>The stack cap <see cref="LiveItem"/> declares, which check 12 compares a count against.</summary>
    public const int LiveItemStackCap = 20;

    /// <summary>A live mod row, which an affix entry names.</summary>
    public const int LiveMod = 500;

    /// <summary>A second live mod row, so an affix list can hold two.</summary>
    public const int SecondMod = 501;

    /// <summary>An authored tier ordinal <see cref="LiveMod"/> really has.</summary>
    public const byte LiveTier = 3;

    /// <summary>A tier ordinal whose <c>mod_tier</c> row is retired, so it is not LIVE.</summary>
    public const byte RetiredTier = 4;

    /// <summary>A live socket type row.</summary>
    public const int LiveSocketType = 700;

    /// <summary>A live rarity rule row.</summary>
    public const int LiveRarityRule = 800;

    /// <summary>A live unique template row.</summary>
    public const int LiveUniqueTemplate = 900;

    /// <summary>A live rare name word row.</summary>
    public const int LiveRareNameWord = 950;

    /// <summary>An id no row of any type carries, which is what an unresolved reference names.</summary>
    public const int MissingId = 9_999;

    const ushort ModTypeId = 256;
    const ushort ModTierTypeId = 257;
    const ushort SocketTypeTypeId = 258;
    const ushort RarityRuleTypeId = 259;
    const ushort UniqueTemplateTypeId = 260;
    const ushort RareNameWordTypeId = 261;

    /// <summary>The <c>item</c> type, which the entry's definition id and a socket's contained one belong to.</summary>
    public static ContentTypeId ItemType => new(EngineContentTypes.ItemTypeId);

    /// <summary>A fresh property registry, never shared, so no test can register into another's.</summary>
    public static InstancePropertyRegistry Properties() => InstancePropertyRegistry.CreateV1();

    /// <summary>The six engine types plus the six Scope B stand-ins, fresh per call.</summary>
    public static ContentTypeRegistry Types()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        Instances(registry, ModTypeId, "mod", Schema(("kind", ContentFieldKind.Int), ("legacy", ContentFieldKind.Bool)));
        Instances(
            registry,
            ModTierTypeId,
            "mod_tier",
            Schema(
                ("mod_id", ContentFieldKind.Int),
                ("ordinal", ContentFieldKind.Int),
                ("item_level_min", ContentFieldKind.Int),
                ("item_level_max", ContentFieldKind.Int)));
        Instances(registry, SocketTypeTypeId, "socket_type", Schema(("width", ContentFieldKind.Int)));
        Instances(registry, RarityRuleTypeId, "rarity_rule", Schema(("ordinal", ContentFieldKind.Int)));
        Instances(registry, UniqueTemplateTypeId, "unique_template", Schema(("base_id", ContentFieldKind.Int)));
        Instances(registry, RareNameWordTypeId, "rare_name_word", Schema(("position", ContentFieldKind.Int)));
        return registry;
    }

    /// <summary>
    /// The fixture content: three item bases one of which is retired, two mods, four tiers one of which is
    /// retired, and one row of every other referenced type.
    /// </summary>
    public static ContentSnapshot Snapshot(ContentTypeRegistry types, IReadOnlyList<RemapRule>? rules = null)
    {
        var builder = new ContentSnapshotBuilder(types);
        builder.WithIdentity(ActiveVersion, "fixture");
        builder.AddRow(Item(types, LiveItem, "sword", retired: false, LiveItemStackCap));
        builder.AddRow(Item(types, RetiredItem, "old_sword", retired: true, 1));
        builder.AddRow(Item(types, GemItem, "ruby", retired: false, 1));
        builder.AddRow(Row(types, ModTypeId, LiveMod, "fine", retired: false, ("kind", 1)));
        builder.AddRow(Row(types, ModTypeId, SecondMod, "sharp", retired: false, ("kind", 2)));
        builder.AddRow(Tier(types, 1, LiveMod, 1));
        builder.AddRow(Tier(types, 2, LiveMod, LiveTier));
        builder.AddRow(Tier(types, 3, SecondMod, 1));
        builder.AddRow(Row(
            types, ModTierTypeId, 4, "fine_t4", retired: true, ("mod_id", LiveMod), ("ordinal", RetiredTier)));
        builder.AddRow(Row(types, SocketTypeTypeId, LiveSocketType, "gem", retired: false));
        builder.AddRow(Row(types, RarityRuleTypeId, LiveRarityRule, "rare", retired: false));
        builder.AddRow(Row(types, UniqueTemplateTypeId, LiveUniqueTemplate, "widowmaker", retired: false));
        builder.AddRow(Row(types, RareNameWordTypeId, LiveRareNameWord, "doom", retired: false));
        if (rules is not null)
        {
            builder.WithRules(rules);
        }

        return builder.Build();
    }

    /// <summary>One page entry on the way in, with the defaults a clean stack takes.</summary>
    public static PageSlotInput Slot(
        int slot,
        int definitionId,
        int count = 1,
        long instanceId = 0,
        byte[]? payload = null,
        uint flags = 0)
        => new(slot, flags, definitionId, count, instanceId, payload ?? Array.Empty<byte>());

    /// <summary>The bytes a builder writes, which is the only way a payload is made here.</summary>
    public static byte[] Encode(ItemInstancePayloadBuilder builder)
    {
        byte[] bytes = new byte[builder.Length];
        ItemInstancePayload.Encode(builder, bytes);
        return bytes;
    }

    /// <summary>An affixed payload: one affix naming a live mod at a live tier.</summary>
    public static byte[] AffixPayload(int modId = LiveMod, byte tier = LiveTier)
        => Encode(new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 42)
            .AddAffixes(InstancePropertyKind.Affixes, [new InstanceAffix(modId, tier, 17)]));

    /// <summary>A socketed payload: one socket holding a contained definition and its own nested payload.</summary>
    public static byte[] SocketPayload(int containedDefinitionId = GemItem)
    {
        byte[] nested = Encode(new ItemInstancePayloadBuilder().AddScalar(InstancePropertyKind.Quality, 5));
        return Encode(new ItemInstancePayloadBuilder().AddSockets(
            [new InstanceSocket(LiveSocketType, containedDefinitionId, 4242, nested)]));
    }

    /// <summary>
    /// Encodes a page, decodes it back the way a load path does, and sweeps it. The page goes through the
    /// REAL codec rather than a hand built <see cref="PageEntry"/> array, so an entry's payload window is
    /// the one task 8 produces.
    /// </summary>
    public static InstanceValidationReport Sweep(
        PageSlotInput[] slots,
        ContentTypeRegistry types,
        IContentSnapshot snapshot,
        int stamp = ActiveVersion,
        InstancePropertyRegistry? properties = null)
    {
        byte[] page = ItemContainerPageCodec.Encode(0, 0, PageSlots, stamp, slots);
        var entries = new PageEntry[PageSlots];
        Assert.True(
            ItemContainerPageCodec.TryDecode(page, PageSlots, entries, out PageHeader header, out int count, out string? reason),
            reason);
        return InstanceValidator.Validate(
            page, header, entries.AsSpan(0, count), properties ?? Properties(), types, snapshot);
    }

    /// <summary>The one finding at a slot, which is what a single-defect fixture asserts on.</summary>
    public static InstanceValidationFinding At(this InstanceValidationReport report, int slot)
    {
        foreach (InstanceValidationFinding finding in report.Findings)
        {
            if (finding.Slot == slot)
            {
                return finding;
            }
        }

        Assert.Fail(FormattableString.Invariant($"No finding at slot {slot}. Findings: {Describe(report)}"));
        return default;
    }

    /// <summary>Every finding, rendered for an assertion message.</summary>
    public static string Describe(InstanceValidationReport report)
    {
        var parts = new List<string>();
        foreach (InstanceValidationFinding finding in report.Findings)
        {
            parts.Add(FormattableString.Invariant(
                $"slot {finding.Slot} check {finding.Check} {finding.Reason} {finding.Outcome}"));
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    static ContentRow Item(ContentTypeRegistry types, int id, string key, bool retired, int maxStack)
        => Row(
            types,
            EngineContentTypes.ItemTypeId,
            id,
            key,
            retired,
            (ItemContentType.StackableField, maxStack > 1 ? 1 : 0),
            (ItemContentType.MaxStackField, maxStack),
            (ItemContentType.TradableField, 1),
            (ItemContentType.ValueField, 10));

    static ContentRow Tier(ContentTypeRegistry types, int id, int modId, int ordinal)
        => Row(
            types,
            ModTierTypeId,
            id,
            FormattableString.Invariant($"tier_{id}"),
            retired: false,
            ("mod_id", modId),
            ("ordinal", ordinal));

    static ContentRow Row(
        ContentTypeRegistry types,
        ushort typeId,
        int id,
        string key,
        bool retired,
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

        return new ContentRow(new ContentTypeId(typeId), id, new ContentKey(key), 0, retired, fields);
    }

    static ContentFieldSchema Schema(params (string Name, ContentFieldKind Kind)[] fields)
    {
        var entries = new ContentFieldEntry[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            entries[i] = new ContentFieldEntry(
                fields[i].Name, fields[i].Kind, null, ContentVisibility.Client, false);
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
                if (!field.IsDerivedMarker)
                {
                    written.Add(field.Name);
                }
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
