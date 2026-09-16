using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// Spec 20 phase 4's acceptance clause, end to end and with nothing stubbed: an authored row set goes into
/// an <see cref="IContentAuthoringStore"/>, through <see cref="ContentPublisher"/> into a
/// <see cref="FileSystemPackStore"/> on disk, back out through <see cref="ContentPackReader"/>, into a
/// <see cref="ContentRuntime"/> whose boot step 7b builds <see cref="ModCandidateTablesIndex"/>, and out of
/// an <see cref="ItemGenerator"/> as payloads that decode, validate and stack.
/// <para>
/// It lives in <c>Server.Tests</c> rather than beside the generator's own facts because it needs the
/// authoring store and the publish path, which <c>ItemInstances.Tests</c> does not reference. Every other
/// generator fact builds its snapshot by hand, which is the right shape for a fact about one step. This one
/// is the fact that the steps JOIN UP: a row that authors cleanly and then fails to survive a chunk encode,
/// a manifest, a hash verify and a runtime index would pass every one of those and still ship nothing.
/// </para>
/// <para>
/// <b>The rows are a TEST FIXTURE and they are not PoE</b> (gate 0, spec 8.1). The engine ships eighteen
/// SHAPES and no rows, so what is authored here is a handful chosen to exercise the joins: two mod kinds, a
/// mod carrying two tiers, an exclusivity group, a unique template with its own line and socket, and a two
/// word rare name. Nothing here is a balance proposal and nothing here is shipped.
/// </para>
/// <para>
/// <b>The ids are CARRIED rather than allocated</b>, through <see cref="ContentEdit.Import"/>, which is the
/// bulk import into an empty database contracts 5.1 permits. Every cross reference in an authored set is by
/// id, so the alternative is two publishes with a read of the allocated ids in between, which would say
/// nothing this fact is about and would hide the row set behind a round trip.
/// </para>
/// <para>
/// Nothing here writes process-global state: the registry, the store, the pack directory and the runtime
/// holder are all per fact, so no collection attribute is needed.
/// </para>
/// </summary>
public sealed class AuthoredPackGenerationTests
{
    /// <summary>The actor every edit and publish in this fact carries.</summary>
    const string Actor = "authored-pack-tests";

    /// <summary>The build the boot is told it is running, and the pack's own minimum.</summary>
    const int ServerBuild = 4;

    /// <summary>The stone tag, which the maul carries and which every weight below is keyed by.</summary>
    const int StoneTag = 5;

    /// <summary>The rune tag, which the rod carries.</summary>
    const int RuneTag = 6;

    /// <summary>The impact stat every stat line points at.</summary>
    const int ImpactStat = 3;

    /// <summary>The maul, tag stone, one socket, which the unique is seated on.</summary>
    const int Maul = 40;

    /// <summary>The rod, tag rune, no socket.</summary>
    const int Rod = 41;

    /// <summary>The rune socket both the maul and the unique seat.</summary>
    const int RuneSocket = 1;

    /// <summary>The exclusivity group the two impact prefixes share, capped at one per item.</summary>
    const int ImpactGroup = 1;

    /// <summary>The prefix carrying a run of TWO tiers.</summary>
    const int WeightedMod = 1;

    /// <summary>The other prefix of the group, one tier.</summary>
    const int BalancedMod = 2;

    /// <summary>The suffix, one tier.</summary>
    const int TemperedMod = 3;

    /// <summary>The unique's line, an ordinary mod row with one tier and no weight row anywhere.</summary>
    const int RelicLineMod = 4;

    /// <summary>The one affix rarity, which rolls no name.</summary>
    const int PlainRarity = 1;

    /// <summary>The two affix rarity, which rolls a two word name.</summary>
    const int StoriedRarity = 2;

    /// <summary>The unique template seated on the maul.</summary>
    const int WorldsplitterTemplate = 1;

    /// <summary>How many items the fact rolls, spread over both bases and a run of item levels.</summary>
    const int Rolls = 64;

    [Fact]
    public async Task An_authored_pack_publishes_boots_and_rolls_items_that_decode_validate_and_stack()
    {
        string root = Directory.CreateTempSubdirectory("authored-pack-").FullName;
        try
        {
            var index = new ModCandidateTablesIndex();
            ContentTypeRegistry registry = Registry(index);
            var pack = new FileSystemPackStore(root);
            var store = new InMemoryContentAuthoringStore(registry, pack);

            // Author, then publish through the real pipeline: the candidate is validated, the KEC0100 band
            // runs in pass 6, every chunk is encoded and hashed, both manifests are written and the version
            // pointer lands last.
            _ = await store.ApplyEditsAsync(Rows(), Actor, "oid:tests", "the authored pack fact");
            ContentPublishResult published = await store.PublishAsync(
                new ContentPublishRequest(Actor, "oid:tests", "the authored pack fact", 0, ServerBuild, 1));
            Assert.Equal(1, published.VersionNumber);
            Assert.True(published.ChunksWritten > 0);
            Assert.True(await pack.ExistsAsync(published.ServerManifestHash));

            // Read the pack back the way a server does, through the reader that fuses verify and decode.
            PackVersionPointer pointer = Assert.IsType<PackVersionPointer>(
                await pack.GetVersionPointerAsync(published.VersionNumber));
            Assert.Equal(published.ServerManifestHash, pointer.ServerManifestHash);
            ContentManifestRead manifest = await ContentPackReader.ReadManifestAsync(
                pack,
                pointer.ServerManifestHash,
                ContentManifestSide.Server,
                registry);
            Assert.True(manifest.Success, manifest.Reason ?? "no reason");
            var reader = new ContentPackReader(pack, registry, manifest.Manifest!, pointer.ServerManifestHash);
            ContentPackRead read = await reader.ReadAllAsync();
            Assert.True(read.Success, read.Reason ?? "no reason");
            Assert.Equal(4, read.Snapshot!.Rows(new ContentTypeId(InstanceContentTypeIds.ModTypeId)).Count);

            // Boot it. Step 7b is what builds the candidate tables, so a generator that rolls at all is the
            // evidence the index ran and the evidence it saw the published rows rather than a hand built
            // snapshot.
            var holder = new ContentRuntimeHolder();
            ContentBootResult boot = await ContentBoot.RunAsync(new ContentBootOptions
            {
                Registry = registry,
                Store = pack,
                Pointers = pack,
                Holder = holder,
                ServerBuild = ServerBuild,
                ConfiguredVersion = published.VersionNumber,
                StoreName = "the authored pack",
            });

            Assert.True(boot.Success, string.Join(Environment.NewLine, boot.StandardError));
            ContentRuntime runtime = Assert.IsType<ContentRuntime>(holder.Current);
            Assert.Equal(published.VersionNumber, runtime.VersionNumber);
            ModCandidateTables tables = index.Tables;
            Assert.True(tables.TableEntries > 0);
            Assert.Equal(0, tables.ConsistencyFailures);
            GenerationTables generation = index.Generation;

            // Roll, on two generators over the SAME immutable tables and the same seed, which is the replay
            // shape contracts 14.4 describes and is also how a twin is built.
            InstanceIdAllocator allocator = Allocator();
            var left = new ItemGenerator(generation, new SeededRandomSource(915), allocator);
            var right = new ItemGenerator(generation, new SeededRandomSource(915), allocator);
            var registryV1 = InstancePropertyRegistry.CreateV1();
            int withAffixes = 0;
            int withName = 0;
            foreach (GenerationContext context in Contexts())
            {
                GenerationResult rolled = left.Generate(context);
                GenerationResult twin = right.Generate(context);

                Assert.Null(ItemInstancePayload.Validate(registryV1, rolled.Payload.Span));
                Assert.Null(ItemInstancePayload.Validate(registryV1, twin.Payload.Span));

                // Byte equality IS the stacking rule (spec 4.6), so the twin merges with it and the instance
                // ids are the only thing the two differ by.
                Assert.True(
                    ItemInstancePayload.SequenceEqual(rolled.Payload.Span, twin.Payload.Span),
                    Describe(context, rolled));
                // One allocator serves both, so the twin is a SECOND item rather than the same one read
                // twice: same bytes, different durable identity, which is exactly what stacking means.
                Assert.NotEqual(rolled.InstanceId, twin.InstanceId);
                Assert.Equal(published.VersionNumber, rolled.ContentVersion);

                List<InstanceAffix> affixes = Affixes(rolled.Payload);
                foreach (InstanceAffix affix in affixes)
                {
                    Assert.Contains(affix.ModId, new[] { WeightedMod, BalancedMod, TemperedMod, RelicLineMod });
                    Assert.InRange(affix.Tier, 1, 2);
                }

                // The group is capped at one per item, so an item never carries both prefixes of it.
                Assert.False(
                    Has(affixes, WeightedMod) && Has(affixes, BalancedMod),
                    "One item carried both mods of a group capped at one per item.");

                withAffixes += affixes.Count > 0 ? 1 : 0;
                withName += HasField(rolled.Payload, InstancePropertyKind.RareName) ? 1 : 0;
            }

            // The run actually exercised what it authored rather than rolling empty payloads at every level.
            Assert.True(withAffixes > Rolls / 2, $"Only {withAffixes} of {Rolls} rolls carried an affix.");
            Assert.True(withName > 0, "No roll carried a rare name, so the two word name never ran.");

            // The forced unique takes its line and its socket from content and draws nothing at all, which is
            // the one path through the generator that reads the unique family's three types.
            GenerationResult unique = left.Generate(new GenerationContext(Maul, 40, PlainRarity, WorldsplitterTemplate, 0));
            Assert.Null(ItemInstancePayload.Validate(registryV1, unique.Payload.Span));
            InstanceAffix line = Assert.Single(Affixes(unique.Payload));
            Assert.Equal(RelicLineMod, line.ModId);
            Assert.Equal(1, line.Tier);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>The registry a server builds: the six engine types plus the eighteen of the band.</summary>
    static ContentTypeRegistry Registry(ModCandidateTablesIndex index)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        InstanceContentTypes.Register(registry, index);
        return registry;
    }

    /// <summary>A fresh allocator on node 0, which issues from counter 1.</summary>
    static InstanceIdAllocator Allocator() => new(new MemoryInstanceIdStore(), 0);

    /// <summary>What the fact rolls: both bases, alternating rarity, over a run of item levels.</summary>
    static IEnumerable<GenerationContext> Contexts()
    {
        for (int roll = 0; roll < Rolls; roll++)
        {
            int baseId = roll % 2 == 0 ? Maul : Rod;
            int rarity = roll % 4 < 2 ? PlainRarity : StoriedRarity;
            yield return new GenerationContext(baseId, 1 + (roll % 40), rarity, 0, 0);
        }
    }

    /// <summary>
    /// The authored set, as the edits a console would apply. Two tags, two bases, one socket type, four
    /// mods of two kinds with a run of two tiers on one of them, an exclusivity group, two rarities, a
    /// unique with a line and a socket, and a two word rare name.
    /// </summary>
    static ContentEdit[] Rows() =>
    [
        Add(EngineContentTypes.TagTypeId, StoneTag, "stone", Field(TagContentType.SortField, ContentFieldKind.Int, 1)),
        Add(EngineContentTypes.TagTypeId, RuneTag, "rune", Field(TagContentType.SortField, ContentFieldKind.Int, 2)),
        Add(
            EngineContentTypes.StatTypeId,
            ImpactStat,
            "impact",
            Field(StatContentType.ScaleField, ContentFieldKind.Int, 1),
            Field(StatContentType.MinField, ContentFieldKind.Int, 0),
            Field(StatContentType.MaxField, ContentFieldKind.Int, 1_000)),

        Add(EngineContentTypes.ItemTypeId, Maul, "maul", [.. Base([StoneTag], socketMax: 1)]),
        Add(EngineContentTypes.ItemTypeId, Rod, "rod", [.. Base([RuneTag], socketMax: 0)]),

        Add(
            InstanceContentTypeIds.SocketTypeTypeId,
            RuneSocket,
            "rune_socket",
            Field(SocketTypeContentType.MaxNestedBytesField, ContentFieldKind.Int, 0)),
        Add(
            EngineContentTypes.BaseSocketTypeId,
            1,
            "maul_socket_1",
            Field(BaseSocketContentType.ItemField, ContentFieldKind.KeyReference, Maul),
            Field(BaseSocketContentType.SortField, ContentFieldKind.Int, 1),
            Field(BaseSocketContentType.SocketTypeField, ContentFieldKind.KeyReference, RuneSocket)),

        Add(
            InstanceContentTypeIds.ModGroupTypeId,
            ImpactGroup,
            "impact_group",
            Field(ModGroupContentType.MaxPerItemField, ContentFieldKind.Int, 1)),
        Add(InstanceContentTypeIds.ModTypeId, WeightedMod, "weighted", [.. Mod(ModContentType.PrefixKind, ImpactGroup)]),
        Add(InstanceContentTypeIds.ModTypeId, BalancedMod, "balanced", [.. Mod(ModContentType.PrefixKind, ImpactGroup)]),
        Add(InstanceContentTypeIds.ModTypeId, TemperedMod, "tempered", [.. Mod(ModContentType.SuffixKind, 0)]),
        Add(InstanceContentTypeIds.ModTypeId, RelicLineMod, "relic_line", [.. Mod(ModContentType.PrefixKind, 0)]),

        // The run of TWO tiers sits on the weighted prefix, so a placement deducts both and a tier ordinal
        // above 1 has to survive the chunk round trip to be rollable at all.
        Add(InstanceContentTypeIds.ModTierTypeId, 1, "weighted_t1", [.. Tier(WeightedMod, 1)]),
        Add(InstanceContentTypeIds.ModTierTypeId, 2, "weighted_t2", [.. Tier(WeightedMod, 2)]),
        Add(InstanceContentTypeIds.ModTierTypeId, 3, "balanced_t1", [.. Tier(BalancedMod, 1)]),
        Add(InstanceContentTypeIds.ModTierTypeId, 4, "tempered_t1", [.. Tier(TemperedMod, 1)]),
        Add(InstanceContentTypeIds.ModTierTypeId, 5, "relic_line_t1", [.. Tier(RelicLineMod, 1)]),

        Add(InstanceContentTypeIds.StatLineTypeId, 1, "weighted_t1_l1", [.. Line(1)]),
        Add(InstanceContentTypeIds.StatLineTypeId, 2, "weighted_t2_l1", [.. Line(2)]),
        Add(InstanceContentTypeIds.StatLineTypeId, 3, "balanced_t1_l1", [.. Line(3)]),
        Add(InstanceContentTypeIds.StatLineTypeId, 4, "tempered_t1_l1", [.. Line(4)]),
        Add(InstanceContentTypeIds.StatLineTypeId, 5, "relic_line_t1_l1", [.. Line(5)]),

        Add(InstanceContentTypeIds.ModTierWeightTypeId, 1, "weighted_t1_stone", [.. Weight(1, StoneTag, 100)]),
        Add(InstanceContentTypeIds.ModTierWeightTypeId, 2, "weighted_t2_stone", [.. Weight(2, StoneTag, 50)]),
        Add(InstanceContentTypeIds.ModTierWeightTypeId, 3, "balanced_t1_stone", [.. Weight(3, StoneTag, 70)]),
        Add(InstanceContentTypeIds.ModTierWeightTypeId, 4, "tempered_t1_stone", [.. Weight(4, StoneTag, 300)]),
        Add(InstanceContentTypeIds.ModTierWeightTypeId, 5, "balanced_t1_rune", [.. Weight(3, RuneTag, 60)]),
        Add(InstanceContentTypeIds.ModTierWeightTypeId, 6, "tempered_t1_rune", [.. Weight(4, RuneTag, 90)]),

        Add(InstanceContentTypeIds.RarityRuleTypeId, PlainRarity, "plain", [.. Rarity(1, 1, 0)]),
        Add(InstanceContentTypeIds.RarityRuleTypeId, StoriedRarity, "storied", [.. Rarity(2, 2, 2)]),
        Add(InstanceContentTypeIds.RarityWeightTypeId, 1, "plain_stone", [.. RarityWeight(PlainRarity, StoneTag, 600)]),
        Add(InstanceContentTypeIds.RarityWeightTypeId, 2, "storied_stone", [.. RarityWeight(StoriedRarity, StoneTag, 400)]),
        Add(InstanceContentTypeIds.RarityWeightTypeId, 3, "plain_rune", [.. RarityWeight(PlainRarity, RuneTag, 600)]),
        Add(InstanceContentTypeIds.RarityWeightTypeId, 4, "storied_rune", [.. RarityWeight(StoriedRarity, RuneTag, 400)]),

        Add(
            InstanceContentTypeIds.UniqueTemplateTypeId,
            WorldsplitterTemplate,
            "worldsplitter",
            Field(UniqueTemplateContentType.BaseIdField, ContentFieldKind.KeyReference, Maul),
            Field(UniqueTemplateContentType.ItemLevelMinField, ContentFieldKind.Int, 1),
            Field(UniqueTemplateContentType.WeightField, ContentFieldKind.Int, 500)),
        Add(
            InstanceContentTypeIds.UniqueLineTypeId,
            1,
            "worldsplitter_line_1",
            Field(UniqueLineContentType.UniqueTemplateIdField, ContentFieldKind.KeyReference, WorldsplitterTemplate),
            Field(UniqueLineContentType.SortField, ContentFieldKind.Int, 1),
            Field(UniqueLineContentType.ModIdField, ContentFieldKind.KeyReference, RelicLineMod),
            Field(UniqueLineContentType.TierOrdinalField, ContentFieldKind.Int, 1)),
        Add(
            InstanceContentTypeIds.UniqueSocketTypeId,
            1,
            "worldsplitter_socket_1",
            Field(UniqueSocketContentType.UniqueTemplateIdField, ContentFieldKind.KeyReference, WorldsplitterTemplate),
            Field(UniqueSocketContentType.SortField, ContentFieldKind.Int, 1),
            Field(UniqueSocketContentType.SocketTypeIdField, ContentFieldKind.KeyReference, RuneSocket)),

        Add(
            InstanceContentTypeIds.RareNameWordTypeId,
            1,
            "silent",
            Field(RareNameWordContentType.PositionField, ContentFieldKind.Int, 1)),
        Add(
            InstanceContentTypeIds.RareNameWordTypeId,
            2,
            "echo",
            Field(RareNameWordContentType.PositionField, ContentFieldKind.Int, 2)),
        Add(InstanceContentTypeIds.RareNameWordWeightTypeId, 1, "silent_stone", [.. WordWeight(1, StoneTag, 400)]),
        Add(InstanceContentTypeIds.RareNameWordWeightTypeId, 2, "echo_stone", [.. WordWeight(2, StoneTag, 400)]),
        Add(InstanceContentTypeIds.RareNameWordWeightTypeId, 3, "silent_rune", [.. WordWeight(1, RuneTag, 400)]),
        Add(InstanceContentTypeIds.RareNameWordWeightTypeId, 4, "echo_rune", [.. WordWeight(2, RuneTag, 400)]),
    ];

    /// <summary>One item base, with its authored tag list and its socket cap.</summary>
    static ContentFieldEdit[] Base(IReadOnlyList<int> tags, int socketMax) =>
    [
        new(ItemContentType.TagsField, ContentRowCodecBase.TagListValue(tags)),
        Field(ItemContentType.StackableField, ContentFieldKind.Bool, 0),
        Field(ItemContentType.MaxStackField, ContentFieldKind.Int, 1),
        Field(ItemContentType.TradableField, ContentFieldKind.Bool, 1),
        Field(ItemContentType.ValueField, ContentFieldKind.ScaledInt, 10),
        Field(ItemContentType.SocketMaxField, ContentFieldKind.Int, socketMax),
    ];

    /// <summary>One mod row: its kind, its group, and never legacy.</summary>
    static ContentFieldEdit[] Mod(int kind, int group)
    {
        var fields = new List<ContentFieldEdit>
        {
            Field(ModContentType.KindField, ContentFieldKind.Int, kind),
            Field(ModContentType.LegacyField, ContentFieldKind.Bool, 0),
        };

        if (group != 0)
        {
            fields.Add(Field(ModContentType.GroupIdField, ContentFieldKind.KeyReference, group));
        }

        return [.. fields];
    }

    /// <summary>One tier of one mod, live at every item level the fact rolls.</summary>
    static ContentFieldEdit[] Tier(int modId, int ordinal) =>
    [
        Field(ModTierContentType.ModIdField, ContentFieldKind.KeyReference, modId),
        Field(ModTierContentType.OrdinalField, ContentFieldKind.Int, ordinal),
        Field(ModTierContentType.ItemLevelMinField, ContentFieldKind.Int, 1),
        Field(ModTierContentType.ItemLevelMaxField, ContentFieldKind.Int, ModTierContentType.MaxItemLevel),
    ];

    /// <summary>One stat line on one tier, flat, over a range whose two ends are reachable.</summary>
    static ContentFieldEdit[] Line(int tierId) =>
    [
        Field(StatLineContentType.ModTierIdField, ContentFieldKind.KeyReference, tierId),
        Field(StatLineContentType.SortField, ContentFieldKind.Int, 1),
        Field(StatLineContentType.StatIdField, ContentFieldKind.KeyReference, ImpactStat),
        Field(StatLineContentType.CombineField, ContentFieldKind.Int, StatLineContentType.CombineFlat),
        Field(StatLineContentType.MinField, ContentFieldKind.Int, 10),
        Field(StatLineContentType.MaxField, ContentFieldKind.Int, 40),
    ];

    /// <summary>One tier's weight against one tag.</summary>
    static ContentFieldEdit[] Weight(int tierId, int tagId, int weight) =>
    [
        Field(ModTierWeightContentType.ModTierIdField, ContentFieldKind.KeyReference, tierId),
        Field(ModTierWeightContentType.TagIdField, ContentFieldKind.KeyReference, tagId),
        Field(ModTierWeightContentType.WeightField, ContentFieldKind.Int, weight),
    ];

    /// <summary>One rarity rule: an exact affix count, one of each kind per affix, and its name positions.</summary>
    static ContentFieldEdit[] Rarity(int affixes, int perKind, int namePositions) =>
    [
        Field(RarityRuleContentType.MinAffixesField, ContentFieldKind.Int, affixes),
        Field(RarityRuleContentType.MaxAffixesField, ContentFieldKind.Int, affixes),
        Field(RarityRuleContentType.MaxPrefixesField, ContentFieldKind.Int, perKind),
        Field(RarityRuleContentType.MaxSuffixesField, ContentFieldKind.Int, perKind),
        Field(RarityRuleContentType.NameWordPositionsField, ContentFieldKind.Int, namePositions),
    ];

    /// <summary>One rarity's weight against one tag.</summary>
    static ContentFieldEdit[] RarityWeight(int rarityId, int tagId, int weight) =>
    [
        Field(RarityWeightContentType.RarityRuleIdField, ContentFieldKind.KeyReference, rarityId),
        Field(RarityWeightContentType.TagIdField, ContentFieldKind.KeyReference, tagId),
        Field(RarityWeightContentType.WeightField, ContentFieldKind.Int, weight),
    ];

    /// <summary>One rare name word's weight against one tag.</summary>
    static ContentFieldEdit[] WordWeight(int wordId, int tagId, int weight) =>
    [
        Field(RareNameWordWeightContentType.RareNameWordIdField, ContentFieldKind.KeyReference, wordId),
        Field(RareNameWordWeightContentType.TagIdField, ContentFieldKind.KeyReference, tagId),
        Field(RareNameWordWeightContentType.WeightField, ContentFieldKind.Int, weight),
    ];

    /// <summary>One add carrying its own id, which is the bulk import path of contracts 5.1.</summary>
    static ContentEdit Add(ushort typeId, int id, string key, params ContentFieldEdit[] fields)
        => ContentEdit.Import(new ContentTypeId(typeId), id, new ContentKey(key), fields);

    /// <summary>One field edit, by schema field name, with its kind carried alongside the number.</summary>
    static ContentFieldEdit Field(string name, ContentFieldKind kind, long value)
        => new(name, ContentFieldValue.OfNumber(kind, value));

    /// <summary>Whether the affix list carries one mod at all.</summary>
    static bool Has(IReadOnlyList<InstanceAffix> affixes, int modId)
    {
        foreach (InstanceAffix affix in affixes)
        {
            if (affix.ModId == modId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the payload carries a field of that kind at all.</summary>
    static bool HasField(ReadOnlyMemory<byte> payload, ushort kind)
    {
        var fields = new PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(payload.Span, fields, out int count, out string? reason), reason ?? "no reason");
        for (int index = 0; index < count; index++)
        {
            if (fields[index].Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Kind 131's entries, decoded structurally, which is all this fact reads of a payload.</summary>
    static List<InstanceAffix> Affixes(ReadOnlyMemory<byte> payload)
    {
        var decoded = new List<InstanceAffix>();
        var fields = new PayloadField[ItemInstancePayload.MaxFields];
        Assert.True(ItemInstancePayload.TryDecode(payload.Span, fields, out int count, out string? reason), reason ?? "no reason");
        for (int index = 0; index < count; index++)
        {
            if (fields[index].Kind != InstancePropertyKind.Affixes)
            {
                continue;
            }

            ReadOnlySpan<byte> body = payload.Span.Slice(fields[index].BodyStart, fields[index].BodyLength);
            int affixes = body[0];
            int offset = 1;
            for (int entry = 0; entry < affixes; entry++)
            {
                Assert.True(ContentVarint.TryRead(body, ref offset, out uint modId, out reason), reason ?? "no reason");
                byte tier = body[offset++];
                ushort position = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
                offset += 2;
                Assert.True(ContentVarint.TryRead(body, ref offset, out uint flags, out reason), reason ?? "no reason");
                decoded.Add(new InstanceAffix((int)modId, tier, position, flags));
            }
        }

        return decoded;
    }

    /// <summary>What a failed stacking assertion names, so a red run says which roll broke.</summary>
    static string Describe(in GenerationContext context, in GenerationResult result)
        => FormattableString.Invariant(
            $"Base {context.BaseId} at level {context.ItemLevel} rarity {context.ForcedRarityId} produced {result.Payload.Length} bytes.");

    /// <summary>Removes the pack directory, tolerating a writer that has not let go yet.</summary>
    static void Delete(string directory)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }
}

/// <summary>
/// An instance id store that keeps its whole record in memory, which is all this fact needs: it never
/// crashes and never restores, so the epoch and the retired list are whatever the allocator last wrote.
/// </summary>
internal sealed class MemoryInstanceIdStore : IInstanceIdStore
{
    InstanceIdState _state;

    /// <inheritdoc />
    public InstanceIdState Read() => _state;

    /// <inheritdoc />
    public void Persist(in InstanceIdState state) => _state = state;
}
