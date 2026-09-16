using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.ItemInstances.Allocator;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>
/// The authored world the generator facts roll on, the random sources that make a draw observable, and the
/// payload readers a fact asserts against.
/// <para>
/// The world is small enough that every number in a fact is hand checkable, and it is authored HERE rather
/// than shipped, because the engine ships eighteen shapes and no rows. It is not PoE and it is not a
/// balance proposal: it is two kinds, a mod with a run of three tiers, an exclusivity group, a tag that
/// repeats a mod so the overlap has something to suppress, a unique and a two word rare name.
/// </para>
/// <para>
/// Every registry a fact builds is its OWN, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed. The allocation fact reads
/// <c>GC.GetAllocatedBytesForCurrentThread()</c>, which is per thread, so it needs no collection either.
/// </para>
/// </summary>
internal static class GenerationWorld
{
    /// <summary>The metal tag, which carries every weight row the facts count.</summary>
    public const int MetalTag = 5;

    /// <summary>The gem tag, which only the wand carries.</summary>
    public const int GemTag = 6;

    /// <summary>The blade tag, which repeats one of metal's mods so the overlap suppresses it.</summary>
    public const int BladeTag = 7;

    /// <summary>The greatsword, tags metal then blade, which is the base most facts roll on.</summary>
    public const int Greatsword = 40;

    /// <summary>The wand, tag gem alone, which carries no weight row at all so its pool is empty.</summary>
    public const int Wand = 41;

    /// <summary>The dagger, tag metal alone, which is the greatsword without the repeated tag.</summary>
    public const int Dagger = 42;

    /// <summary>The magic rarity rule: one affix, one prefix, one suffix, no rare name.</summary>
    public const int MagicRarity = 1;

    /// <summary>The rare rarity rule: four affixes, three of each kind, a two word name.</summary>
    public const int RareRarity = 2;

    /// <summary>The unique template seated on the greatsword.</summary>
    public const int SunbrandTemplate = 1;

    /// <summary>The gem socket type the greatsword and the unique both seat.</summary>
    public const int GemSocket = 1;

    /// <summary>A registry carrying the six engine types and all eighteen of the band.</summary>
    public static ContentTypeRegistry World() => Registry();

    /// <summary>
    /// The authored rows, in one list so a fact can add to them or drop one. Weights are chosen so no two
    /// sums collide, which is what makes "the live weight dropped by exactly this run" a readable assertion.
    /// </summary>
    public static List<ContentRow> Rows(ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return
        [
            Tag(registry, MetalTag, "metal"),
            Tag(registry, GemTag, "gem"),
            Tag(registry, BladeTag, "blade"),
            Stat(registry, 3, "attack"),

            Base(registry, Greatsword, "greatsword", [MetalTag, BladeTag], durabilityMax: 0, socketMax: 2),
            Base(registry, Wand, "wand", [GemTag], durabilityMax: 0, socketMax: 0),
            Base(registry, Dagger, "dagger", [MetalTag], durabilityMax: 120, socketMax: 0),

            SocketType(registry, GemSocket, "gem_socket"),
            BaseSocket(registry, 1, "greatsword_socket_1", Greatsword, sort: 1, socketTypeId: GemSocket),
            BaseSocket(registry, 2, "greatsword_socket_2", Greatsword, sort: 2, socketTypeId: GemSocket),

            // The run of tiers and the exclusivity group sit on DIFFERENT mods, so a fact about one is not
            // reading the other's deduction: sharp carries three tiers and no group, heavy and strong carry
            // one tier each and share a group capped at one per item.
            ModGroup(registry, 1, "added_attack", maxPerItem: 1),
            Mod(registry, 1, "sharp", kind: ModContentType.PrefixKind),
            Mod(registry, 2, "keen", kind: ModContentType.SuffixKind),
            Mod(registry, 3, "heavy", kind: ModContentType.PrefixKind, group: 1),
            Mod(registry, 4, "swift", kind: ModContentType.SuffixKind),
            Mod(registry, 5, "strong", kind: ModContentType.PrefixKind, group: 1),
            Mod(registry, 6, "sunbrand_line", kind: ModContentType.PrefixKind),

            // Mod 1 carries a RUN of three tiers, which is what "deducts its whole run" is measured on.
            ModTier(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            ModTier(registry, 2, "sharp_t2", modId: 1, ordinal: 2),
            ModTier(registry, 3, "sharp_t3", modId: 1, ordinal: 3),
            ModTier(registry, 4, "keen_t1", modId: 2, ordinal: 1),
            ModTier(registry, 5, "heavy_t1", modId: 3, ordinal: 1),
            ModTier(registry, 6, "swift_t1", modId: 4, ordinal: 1),
            ModTier(registry, 7, "strong_t1", modId: 5, ordinal: 1),

            // The unique's line is an ORDINARY mod row with a single tier and NO weight row anywhere, so
            // the generator can never roll it onto a rare (spec 8.6).
            ModTier(registry, 8, "sunbrand_line_t1", modId: 6, ordinal: 1),
            StatLine(registry, 1, "sharp_t1_l1", tierId: 1, statId: 3),

            ModTierWeight(registry, 1, "sharp_t1_metal", tierId: 1, tagId: MetalTag, weight: 100),
            ModTierWeight(registry, 2, "sharp_t2_metal", tierId: 2, tagId: MetalTag, weight: 50),
            ModTierWeight(registry, 3, "sharp_t3_metal", tierId: 3, tagId: MetalTag, weight: 25),
            ModTierWeight(registry, 4, "keen_t1_metal", tierId: 4, tagId: MetalTag, weight: 300),
            ModTierWeight(registry, 5, "heavy_t1_metal", tierId: 5, tagId: MetalTag, weight: 70),
            ModTierWeight(registry, 6, "swift_t1_metal", tierId: 6, tagId: MetalTag, weight: 200),
            ModTierWeight(registry, 7, "strong_t1_metal", tierId: 7, tagId: MetalTag, weight: 90),

            // The blade tag repeats sharp tier 1, so the greatsword's second tag position carries a copy the
            // overlap suppresses and the dagger carries no copy at all.
            ModTierWeight(registry, 8, "sharp_t1_blade", tierId: 1, tagId: BladeTag, weight: 1000),
            ModTierWeight(registry, 9, "heavy_t1_blade", tierId: 5, tagId: BladeTag, weight: 7),

            RarityRule(registry, MagicRarity, "magic", minAffixes: 1, maxAffixes: 1, maxPrefixes: 1, maxSuffixes: 1),
            RarityRule(
                registry,
                RareRarity,
                "rare",
                minAffixes: 4,
                maxAffixes: 4,
                maxPrefixes: 3,
                maxSuffixes: 3,
                nameWordPositions: 2),
            RarityWeight(registry, 1, "magic_metal", rarityId: MagicRarity, tagId: MetalTag, weight: 1000),
            RarityWeight(registry, 2, "rare_metal", rarityId: RareRarity, tagId: MetalTag, weight: 500),

            UniqueTemplate(registry, SunbrandTemplate, "sunbrand", baseId: Greatsword, itemLevelMin: 1, weight: 850),
            UniqueLine(registry, 1, "sunbrand_line_1", templateId: SunbrandTemplate, modId: 6, tierOrdinal: 1),
            UniqueSocket(registry, 1, "sunbrand_socket_1", templateId: SunbrandTemplate, sort: 1, socketTypeId: GemSocket),

            RareNameWord(registry, 1, "gloom", position: 1),
            RareNameWord(registry, 2, "blade", position: 2),
            RareNameWordWeight(registry, 1, "gloom_metal", wordId: 1, tagId: MetalTag, weight: 400),
            RareNameWordWeight(registry, 2, "blade_metal", wordId: 2, tagId: MetalTag, weight: 400),
        ];
    }

    /// <summary>The candidate carrying the authored world, with no store and no file.</summary>
    public static ContentSnapshot Candidate(ContentTypeRegistry registry) => Snapshot(registry, [.. Rows(registry)]);

    /// <summary>A generator over the authored world, on the source and the allocator the fact hands in.</summary>
    public static ItemGenerator Generator(IRandomSource random, InstanceIdAllocator? allocator = null)
    {
        ContentTypeRegistry registry = World();
        ContentSnapshot candidate = Candidate(registry);
        return new ItemGenerator(
            ModCandidateTables.Build(candidate),
            candidate,
            random,
            allocator ?? FreshAllocator());
    }

    /// <summary>An allocator over a fresh store, which issues from counter 1.</summary>
    public static InstanceIdAllocator FreshAllocator() => new(new RecordingInstanceIdStore(), 0);

    /// <summary>
    /// One item base, which the shared fixture cannot build because the facts need a durability and a socket
    /// cap on it. The field ORDER is <c>ItemContentType</c>'s schema, which
    /// <c>ItemGeneratorTests.The_engine_item_field_indexes_the_generator_reads_are_still_where_it_reads_them</c>
    /// pins, so this list and the generator's reader move together or the fact goes red.
    /// </summary>
    public static ContentRow Base(
        ContentTypeRegistry registry,
        int id,
        string key,
        IReadOnlyList<int> tags,
        int durabilityMax,
        int socketMax)
        => RowAt(
            Lookup(registry, EngineContentTypes.ItemTypeKey),
            id,
            key,
            Marker(),
            Marker(),
            ContentRowCodecBase.TagListValue(tags),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 0),
            Int(1),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1),
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 10),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.Int),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            durabilityMax == 0 ? ContentFieldValue.Absent(ContentFieldKind.Int) : Int(durabilityMax),
            Int(socketMax),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference));

    /// <summary>One authored socket on a base, in <c>sort</c> order, which is what kind 132 seats.</summary>
    public static ContentRow BaseSocket(
        ContentTypeRegistry registry,
        int id,
        string key,
        int itemId,
        int sort,
        int socketTypeId)
        => RowAt(
            Lookup(registry, EngineContentTypes.BaseSocketTypeKey),
            id,
            key,
            Reference(itemId),
            Int(sort),
            Reference(socketTypeId));

    /// <summary>Every field of one payload, decoded structurally, which is all a fact needs to read one.</summary>
    public static List<PayloadField> Fields(ReadOnlyMemory<byte> payload)
    {
        var fields = new PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(
            ItemInstancePayload.TryDecode(payload.Span, fields, out int count, out string? reason),
            reason ?? "no reason");
        var decoded = new List<PayloadField>(count);
        for (int index = 0; index < count; index++)
        {
            decoded.Add(fields[index]);
        }

        return decoded;
    }

    /// <summary>One field's body, or an empty span when the payload carries no field of that kind.</summary>
    public static ReadOnlyMemory<byte> Body(ReadOnlyMemory<byte> payload, ushort kind)
    {
        foreach (PayloadField field in Fields(payload))
        {
            if (field.Kind == kind)
            {
                return payload.Slice(field.BodyStart, field.BodyLength);
            }
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>Kind 131's entries, in the order the payload holds them.</summary>
    public static List<InstanceAffix> Affixes(ReadOnlyMemory<byte> payload)
    {
        ReadOnlyMemory<byte> body = Body(payload, InstancePropertyKind.Affixes);
        var affixes = new List<InstanceAffix>();
        if (body.IsEmpty)
        {
            return affixes;
        }

        ReadOnlySpan<byte> bytes = body.Span;
        int count = bytes[0];
        int offset = 1;
        for (int entry = 0; entry < count; entry++)
        {
            Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint modId, out string? reason), reason ?? "no reason");
            byte tier = bytes[offset++];
            ushort position = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
            offset += 2;
            Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint flags, out reason), reason ?? "no reason");
            affixes.Add(new InstanceAffix((int)modId, tier, position, flags));
        }

        Assert.Equal(bytes.Length, offset);
        return affixes;
    }

    /// <summary>Kind 132's socket type ids, in authored order.</summary>
    public static List<int> SocketTypes(ReadOnlyMemory<byte> payload)
    {
        ReadOnlyMemory<byte> body = Body(payload, InstancePropertyKind.Sockets);
        var sockets = new List<int>();
        if (body.IsEmpty)
        {
            return sockets;
        }

        ReadOnlySpan<byte> bytes = body.Span;
        int offset = 0;
        Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint count, out string? reason), reason ?? "no reason");
        for (int entry = 0; entry < count; entry++)
        {
            Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint socketType, out reason), reason ?? "no reason");
            Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint definition, out reason), reason ?? "no reason");
            Assert.True(ContentVarint.TryReadUInt64(bytes, ref offset, out ulong instance, out reason), reason ?? "no reason");
            Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint nested, out reason), reason ?? "no reason");
            Assert.Equal(0u, definition);
            Assert.Equal(0ul, instance);
            Assert.Equal(0u, nested);
            sockets.Add((int)socketType);
        }

        return sockets;
    }

    /// <summary>Kind 134's word ids, in composed order, and the rarity rule that formatted them.</summary>
    public static (int RarityRuleId, List<int> WordIds) RareName(ReadOnlyMemory<byte> payload)
    {
        ReadOnlyMemory<byte> body = Body(payload, InstancePropertyKind.RareName);
        var words = new List<int>();
        if (body.IsEmpty)
        {
            return (0, words);
        }

        ReadOnlySpan<byte> bytes = body.Span;
        int offset = 0;
        Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint rarityRuleId, out string? reason), reason ?? "no reason");
        int count = bytes[offset++];
        for (int word = 0; word < count; word++)
        {
            Assert.True(ContentVarint.TryRead(bytes, ref offset, out uint wordId, out reason), reason ?? "no reason");
            words.Add((int)wordId);
        }

        return ((int)rarityRuleId, words);
    }
}

/// <summary>
/// Every call the generator makes on its source, in order, beside the answers a real source gave. A draw
/// COUNT is the reproducibility contract of spec 9.3, so a fact about it has to see the CALLS rather than
/// the stream position: <c>NextInt(0, 1)</c> is a defined call that consumes nothing from the wrapped
/// generator, which is exactly why the discard has to be a call rather than nothing at all.
/// </summary>
internal sealed class RecordingRandomSource(IRandomSource inner) : IRandomSource
{
    readonly List<string> _calls = [];

    /// <summary>Every call, as <c>int:min:max</c> or <c>position</c>, in the order they were made.</summary>
    public IReadOnlyList<string> Calls => _calls;

    /// <summary>The <c>maxExclusive</c> of every <see cref="NextInt"/>, which is the pool a draw saw.</summary>
    public List<int> Bounds { get; } = [];

    /// <inheritdoc />
    public int NextInt(int minInclusive, int maxExclusive)
    {
        _calls.Add(string.Create(CultureInfo.InvariantCulture, $"int:{minInclusive}:{maxExclusive}"));
        Bounds.Add(maxExclusive);
        return inner.NextInt(minInclusive, maxExclusive);
    }

    /// <inheritdoc />
    public ulong NextULong()
    {
        _calls.Add("ulong");
        return inner.NextULong();
    }

    /// <inheritdoc />
    public ushort NextRollPosition()
    {
        _calls.Add("position");
        return inner.NextRollPosition();
    }

    /// <inheritdoc />
    public void NextBytes(Span<byte> destination)
    {
        _calls.Add("bytes");
        inner.NextBytes(destination);
    }
}

/// <summary>
/// A source that answers from a SCRIPT, so a fact can put a draw on a chosen entry rather than hoping a
/// seed lands there. An exhausted script answers the bottom of the range, which is a defined answer rather
/// than a throw, because several facts care only about the first few draws.
/// </summary>
internal sealed class ScriptedRandomSource(IReadOnlyList<int> draws, IReadOnlyList<ushort>? positions = null)
    : IRandomSource
{
    int _draw;
    int _position;

    /// <summary>How many draws the script has handed out, which is the generator's own draw count.</summary>
    public int DrawsTaken => _draw;

    /// <inheritdoc />
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "maxExclusive must be greater than minInclusive.");
        }

        if (_draw >= draws.Count)
        {
            return minInclusive;
        }

        int value = draws[_draw++];
        return Math.Clamp(minInclusive + value, minInclusive, maxExclusive - 1);
    }

    /// <inheritdoc />
    public ulong NextULong() => 0;

    /// <inheritdoc />
    public ushort NextRollPosition()
        => positions is null || _position >= positions.Count ? (ushort)0 : positions[_position++];

    /// <inheritdoc />
    public void NextBytes(Span<byte> destination) => destination.Clear();
}
