using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using KhaozEngine.Tests.ItemInstances.Validation;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Remap;

/// <summary>
/// The pages, payloads and rule sets the remap facts run over. The content types and the property registry
/// are the VALIDATOR's fixtures, deliberately: the pass and the validator are supposed to walk the same
/// registrations, so a second registry here would be the first place the two could quietly disagree.
/// <para>
/// The rules are built BY HAND. Nothing on the authoring side can emit a kind 1 rule yet
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/911">#911</see>), so a fixture that went
/// through a publish would pin the gap rather than the pass.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no test using it needs a collection attribute.
/// </para>
/// </summary>
internal static class RemapFixtures
{
    /// <summary>The item base an entry carries, live in the validator's fixture content.</summary>
    public const int Sword = InstanceValidationFixtures.LiveItem;

    /// <summary>The item base a socket contains, live in the same content.</summary>
    public const int Gem = InstanceValidationFixtures.GemItem;

    /// <summary>A live mod row an affix entry names.</summary>
    public const int Mod = InstanceValidationFixtures.LiveMod;

    /// <summary>The second live mod row, so an affix list can hold two.</summary>
    public const int SecondMod = InstanceValidationFixtures.SecondMod;

    /// <summary>The authored tier ordinal the fixture content gives <see cref="Mod"/>.</summary>
    public const byte Tier = InstanceValidationFixtures.LiveTier;

    /// <summary>A live socket type row.</summary>
    public const int SocketType = InstanceValidationFixtures.LiveSocketType;

    /// <summary>A live rarity rule row, which kind 134 names as a VARINT and can therefore exceed 255.</summary>
    public const int RarityRule = InstanceValidationFixtures.LiveRarityRule;

    /// <summary>A live rare name word row.</summary>
    public const int RareNameWord = InstanceValidationFixtures.LiveRareNameWord;

    /// <summary>A live unique template row.</summary>
    public const int UniqueTemplate = InstanceValidationFixtures.LiveUniqueTemplate;

    /// <summary>The instance id every payload-carrying entry here takes. A payload with instance id 0 is
    /// refused at the container door (spec 4.7 invariant 3), so it is never 0 in a fixture.</summary>
    public const long Instance = 7_001;

    /// <summary>The instance id a socketed gem carries, which must survive every rewrite.</summary>
    public const ulong SocketedInstance = 4_242;

    /// <summary>A GAME kind, at or above 1024, that a game registers with a shape and a reference target.</summary>
    public const ushort GameKind = 2_048;

    /// <summary>A kind no registry here knows, whose bytes are kept verbatim (contracts 9.4).</summary>
    public const ushort UnknownKind = 3_000;

    /// <summary>The content type key a rule for an item definition names.</summary>
    public const string ItemKey = EngineContentTypes.ItemTypeKey;

    /// <summary>The content type key a mod rule names.</summary>
    public const string ModKey = "mod";

    /// <summary>The content type key a socket type rule names.</summary>
    public const string SocketTypeKey = EngineContentTypes.SocketTypeTypeKey;

    /// <summary>The content type key a rarity rule names.</summary>
    public const string RarityKey = "rarity_rule";

    /// <summary>The content type key a unique template rule names.</summary>
    public const string UniqueTemplateKey = "unique_template";

    /// <summary>The content type key a rare name word rule names.</summary>
    public const string RareNameWordKey = "rare_name_word";

    /// <summary>The <c>mod_tier</c> type key, which the validator builds its tier index from.</summary>
    public const string ModTierKey = "mod_tier";

    /// <summary>The six engine types plus the six Scope B stand-ins, fresh per call.</summary>
    public static ContentTypeRegistry Types() => InstanceValidationFixtures.Types();

    /// <summary>The v1 property registry, fresh per call, so no test registers into another's.</summary>
    public static InstancePropertyRegistry Properties() => InstanceValidationFixtures.Properties();

    /// <summary>
    /// The v1 registry plus one GAME kind carrying a <c>mod</c> reference in a repeating entry. A game kind
    /// declares a shape and its targets and gets remap, drift detection and quarantine for nothing else.
    /// </summary>
    public static InstancePropertyRegistry WithGameKind()
    {
        InstancePropertyRegistry registry = Properties();
        registry.Register(
            InstanceKindBand.Game,
            GameKind,
            InstancePropertyCodec.ShapeOnly,
            PropertyVisibility.Everyone,
            -1,
            new InstanceFieldShape(default, InstanceCountWidth.Byte, GameEntry),
            new[] { new InstanceReferenceTarget(ModKey, InstanceReferenceSite.Entry, 0) });
        return registry;
    }

    /// <summary>One content type's id, looked up the way the pass looks it up: by KEY, through the registry
    /// the active pack registered.</summary>
    public static ContentTypeId Type(ContentTypeRegistry types, string typeKey)
    {
        Assert.True(types.TryGetByKey(typeKey, out ContentTypeRegistration? registration), typeKey);
        return registration!.Type;
    }

    /// <summary>An empty page at one stamp, with the two door predicates a real container runs.</summary>
    public static ItemContainerPage Page(int stamp = 0, int pageIndex = 0)
    {
        var page = new ItemContainerPage(
            pageIndex, static definitionId => definitionId != 0, ItemInstancePayload.IsCanonical, QuarantineWrapper.Verify);
        page.SeatStamp(stamp);
        return page;
    }

    /// <summary>Seats one entry the way the load path does, which never dirties the page.</summary>
    public static void Seat(
        ItemContainerPage page,
        int slot,
        int definitionId,
        byte[]? payload = null,
        int count = 1,
        long instanceId = Instance,
        bool quarantined = false)
    {
        byte[] bytes = payload ?? Array.Empty<byte>();
        page.Seat(
            slot,
            new ItemSlot(new ItemStack(definitionId, count, bytes.Length == 0 ? 0 : instanceId), bytes, quarantined));
    }

    /// <summary>The rule set, in sequence order, which is the order the pass applies it in.</summary>
    public static RemapRuleSet Rules(params RemapRule[] rules) => new(rules);

    /// <summary>Contracts 8.2 kind 1: every reference to <paramref name="fromId"/> becomes
    /// <paramref name="toId"/>.</summary>
    public static RemapRule Replaced(int sequence, int introducedIn, ContentTypeId type, int fromId, int toId)
        => new(sequence, introducedIn, type, RemapRuleKind.ReplacedBy, fromId, toId, ReadOnlySpan<byte>.Empty);

    /// <summary>Contracts 8.2 kind 2 under the placeholder policy, which moves no id at all.</summary>
    public static RemapRule RetiredPlaceholder(int sequence, int introducedIn, ContentTypeId type, int fromId)
        => new(
            sequence,
            introducedIn,
            type,
            RemapRuleKind.Retired,
            fromId,
            0,
            new[] { RemapRule.RetirePolicyPlaceholder });

    /// <summary>Contracts 8.2 kind 2 under the replacement policy, whose destination is IN the payload.</summary>
    public static RemapRule RetiredTo(int sequence, int introducedIn, ContentTypeId type, int fromId, int toId)
    {
        byte[] payload = new byte[1 + sizeof(int)];
        payload[0] = RemapRule.RetirePolicyReplacement;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), toId);
        return new RemapRule(sequence, introducedIn, type, RemapRuleKind.Retired, fromId, 0, payload);
    }

    /// <summary>Contracts 8.2 kind 3: the keep-legacy copy every existing item moves onto.</summary>
    public static RemapRule MovedToLegacy(int sequence, int introducedIn, ContentTypeId type, int fromId, int toId)
        => new(sequence, introducedIn, type, RemapRuleKind.MovedToLegacy, fromId, toId, ReadOnlySpan<byte>.Empty);

    /// <summary>Contracts 8.2 kind 4: a lowered stack cap, which moves no id and carries the new cap.</summary>
    public static RemapRule StackCapLowered(int sequence, int introducedIn, ContentTypeId type, int fromId, int newCap)
    {
        byte[] payload = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, newCap);
        return new RemapRule(sequence, introducedIn, type, RemapRuleKind.StackCapLowered, fromId, 0, payload);
    }

    /// <summary>One affix list, which the builder holds ascending by mod id whatever order it is handed in.</summary>
    public static byte[] Affixed(params InstanceAffix[] affixes)
        => new ItemInstancePayloadBuilder().AddAffixes(InstancePropertyKind.Affixes, affixes).ToArray();

    /// <summary>One socket holding a contained definition, its instance id and its own nested payload.</summary>
    public static byte[] Socketed(int containedDefinitionId = Gem, byte[]? nested = null, int socketTypeId = SocketType)
        => new ItemInstancePayloadBuilder()
            .AddSockets(new[]
            {
                new InstanceSocket(
                    socketTypeId,
                    containedDefinitionId,
                    SocketedInstance,
                    nested ?? Array.Empty<byte>()),
            })
            .ToArray();

    /// <summary>
    /// A payload carrying a reference of EVERY v1 shape: an engine kind's material ids, a header varint
    /// reference, an entry reference in a repeating list, a socket with a nested payload of its own, and a
    /// header plus entry pair. The rarity byte of kind 130 is deliberately absent, because the fixture's
    /// rarity rule id is above 255 and a byte slot cannot hold it.
    /// </summary>
    public static byte[] Deep(
        int gem = Gem,
        int mod = Mod,
        int nestedMod = SecondMod,
        int socketTypeId = SocketType,
        int uniqueTemplateId = UniqueTemplate,
        int rarityRuleId = RarityRule,
        int wordId = RareNameWord)
    {
        byte[] nested = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.Quality, 5)
            .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(nestedMod, 1, 300) })
            .ToArray();

        return new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 42)
            .AddMaterials(new[] { new InstanceMaterial(gem, 2) })
            .AddIdentification(identified: true, revealedMask: 0xF)
            .AddScalar(InstancePropertyKind.UniqueTemplate, (ulong)uniqueTemplateId)
            .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(mod, Tier, 17) })
            .AddSockets(new[] { new InstanceSocket(socketTypeId, gem, SocketedInstance, nested) })
            .AddRareName(rarityRuleId, new[] { wordId })
            .ToArray();
    }

    /// <summary>The page encoded the way a commit would encode it, which is how two passes are compared
    /// BYTE for byte rather than field by field.</summary>
    public static byte[] Bytes(ItemContainerPage page)
    {
        var entries = new PageSlotInput[page.EntryCount];
        int count = page.CopyEntriesTo(entries);
        return ItemContainerPageCodec.Encode(
            page.PageIndex, page.FirstSlot, page.SlotCount, page.ContentVersion, entries.AsSpan(0, count));
    }

    /// <summary>The payload one slot holds, copied out so an assertion survives a later rewrite.</summary>
    public static byte[] PayloadAt(ItemContainerPage page, int slot) => page.SlotAt(slot).Payload.ToArray();

    /// <summary>The body of one field of a payload, which a length assertion reads directly.</summary>
    public static PayloadField Field(InstancePropertyRegistry properties, ReadOnlySpan<byte> payload, ushort kind)
    {
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(properties, payload, fields, out int count, out string? reason), reason);
        for (int index = 0; index < count; index++)
        {
            if (fields[index].Kind == kind)
            {
                return fields[index];
            }
        }

        Assert.Fail(FormattableString.Invariant($"The payload carries no kind {kind}."));
        return default;
    }

    /// <summary>One field's body bytes, copied out, which is what a length assertion reads.</summary>
    public static byte[] Body(InstancePropertyRegistry properties, byte[] payload, ushort kind)
    {
        PayloadField field = Field(properties, payload, kind);
        return payload.AsSpan(field.BodyStart, field.BodyLength).ToArray();
    }

    /// <summary>
    /// The nested payload length the FIRST socket of a kind 132 body declares, which is the inner of the two
    /// lengths a widened varint forces the pass to recompute.
    /// </summary>
    public static int NestedLength(ReadOnlySpan<byte> socketBody)
    {
        int offset = 0;
        Assert.True(ContentVarint.TryRead(socketBody, ref offset, out uint count, out string? reason), reason);
        Assert.True(count > 0);
        Assert.True(ContentVarint.TryReadUInt64(socketBody, ref offset, out _, out reason), reason);
        Assert.True(ContentVarint.TryReadUInt64(socketBody, ref offset, out _, out reason), reason);
        Assert.True(ContentVarint.TryReadUInt64(socketBody, ref offset, out _, out reason), reason);
        Assert.True(ContentVarint.TryRead(socketBody, ref offset, out uint nested, out reason), reason);
        return (int)nested;
    }

    static readonly InstanceSlotKind[] GameEntry =
    {
        InstanceSlotKind.Varint, InstanceSlotKind.Varint,
    };
}

/// <summary>
/// A snapshot that RESOLVES everything and records every row read, in the order it was asked for. It is how
/// the pass's reference walk is pinned against the validator's: the validator's reads ARE its walk, so the
/// same sweep over the page before and after a rewrite says which ids it visited and what they became.
/// <para>
/// It resolves everything on purpose. A fake that refused an id would stop the validator's sweep at the
/// first drift finding, which is the one thing a walk-order comparison cannot afford.
/// </para>
/// </summary>
internal sealed class RecordingSnapshot : IContentSnapshot
{
    readonly List<(ContentTypeId Type, int Id)> _reads = new();
    readonly ContentTypeId _tierType;
    readonly List<ContentRow> _tiers = new();

    /// <summary>Builds the snapshot with a live tier for every (mod, ordinal) pair the facts use, because
    /// check 8 is the one check that would stop the sweep before the socket list.</summary>
    /// <param name="types">The active types.</param>
    /// <param name="modIds">Every mod id a payload names before or after a rewrite.</param>
    public RecordingSnapshot(ContentTypeRegistry types, params int[] modIds)
    {
        _tierType = RemapFixtures.Type(types, RemapFixtures.ModTierKey);
        Assert.True(types.TryGet(_tierType, out ContentTypeRegistration? tier));

        int rowId = 1;
        foreach (int modId in modIds)
        {
            for (int ordinal = 1; ordinal <= 8; ordinal++)
            {
                _tiers.Add(Row(tier!, rowId++, modId, ordinal));
            }
        }
    }

    /// <summary>Every row read, in the order the sweep asked for it.</summary>
    public IReadOnlyList<(ContentTypeId Type, int Id)> Reads => _reads;

    /// <summary>The version the fixture content publishes at.</summary>
    public int VersionNumber => InstanceValidationFixtures.ActiveVersion;

    /// <summary>The identity pair, which nothing here reads.</summary>
    public ContentVersionIdentity Identity => new(VersionNumber, "recording");

    /// <summary>The rule set the snapshot carries, which the pass never reads: rules arrive by argument.</summary>
    public IReadOnlyList<RemapRule> Rules => Array.Empty<RemapRule>();

    /// <summary>Records the read and resolves it, with a row that declares no fields.</summary>
    public bool TryGetRow(ContentTypeId type, int id, [MaybeNullWhen(false)] out ContentRow row)
    {
        _reads.Add((type, id));
        row = new ContentRow(type, id, new ContentKey("recorded"), 0, false, Array.Empty<ContentFieldValue>());
        return true;
    }

    /// <summary>Not a lookup any check makes.</summary>
    public bool TryGetId(ContentTypeId type, ContentKey key, out int id)
    {
        id = 0;
        return false;
    }

    /// <summary>The tier rows, which is the one type read in bulk.</summary>
    public IReadOnlyList<ContentRow> Rows(ContentTypeId type)
        => type == _tierType ? _tiers : Array.Empty<ContentRow>();

    /// <summary>Nothing here is retired, so check 13 never fires.</summary>
    public bool IsRetired(ContentTypeId type, int id) => false;

    static ContentRow Row(ContentTypeRegistration tier, int rowId, int modId, int ordinal)
    {
        IReadOnlyList<ContentFieldEntry> schema = tier.Schema.Fields;
        var fields = new ContentFieldValue[schema.Count];
        for (int index = 0; index < fields.Length; index++)
        {
            ContentFieldEntry field = schema[index];
            fields[index] = field.Name switch
            {
                ContentReferencesFields.ModId => ContentFieldValue.OfNumber(field.Kind, modId),
                ContentReferencesFields.Ordinal => ContentFieldValue.OfNumber(field.Kind, ordinal),
                _ => ContentFieldValue.Absent(field.Kind),
            };
        }

        return new ContentRow(tier.Type, rowId, new ContentKey(FormattableString.Invariant($"tier_{rowId}")), 0, false, fields);
    }
}

/// <summary>The two <c>mod_tier</c> field names spec 8.3 fixes, named here so the recording snapshot and the
/// validator read the same strings.</summary>
internal static class ContentReferencesFields
{
    /// <summary>The field pointing back at the mod.</summary>
    public const string ModId = "mod_id";

    /// <summary>The authored ordinal a payload stores.</summary>
    public const string Ordinal = "ordinal";
}
