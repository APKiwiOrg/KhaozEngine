using System;
using KhaozEngine.Catalog;
using Xunit;
using static KhaozEngine.Tests.Catalog.Validation.ContentValidationFixtures;

namespace KhaozEngine.Tests.Catalog.Validation;

/// <summary>
/// One test per ISSUED finding code, spec 5.2's table read top to bottom. Each builds the smallest
/// candidate that triggers exactly that code, in memory, with no store and no file, and asserts the code,
/// the type and the id.
/// <para>
/// <b>Twelve codes are present and cannot fire in phase 1</b>, and each has a test that pins it QUIET
/// rather than one that triggers it, which is the same shape the four inheritance codes take. The reason is
/// always that the code's producer has not shipped: <c>KEC0010</c>, <c>KEC0011</c>, <c>KEC0012</c> and
/// <c>KEC0037</c> need the family declarations and the per-row family claim, which are authoring data that
/// never enters a pack and that no snapshot carries. <c>KEC0028</c> is refused earlier, at registration,
/// so a live registry cannot hold the value it looks for. <c>KEC0032</c> to <c>KEC0035</c> wait on the
/// inheritance resolver, which <c>KEC0031</c> refuses the input to. <c>KEC0041</c> is a statement about a
/// Fork EDIT and <c>KEC0039</c> about a rollback, and both are emitted by the authoring store rather than
/// by this sweep. <c>KEC0014</c> waits on the client chunk encode, and its two tests pin the legal
/// per-field override of spec 6.7 ACCEPTED, which is the reading that got <c>KEC0013</c> withdrawn.
/// </para>
/// </summary>
public class ContentFindingCodeTests
{
    [Fact]
    public void KEC0000_is_informational_and_does_not_make_the_report_invalid()
    {
        ContentTypeRegistry registry = EngineRegistry();

        ContentValidationReport report = Validate(CleanCandidate(registry), registry);

        ContentFinding finding = Single(report, "KEC0000");
        Assert.Equal(0, finding.Id);
        Assert.Contains("KEC0003", finding.Message, StringComparison.Ordinal);
        Assert.Contains("KEC0029", finding.Message, StringComparison.Ordinal);
        Assert.True(report.IsValid, Describe(report));
    }

    [Fact]
    public void KEC0001_fires_on_a_key_outside_the_character_set()
    {
        ContentTypeRegistry registry = EngineRegistry();

        ContentValidationReport report = Validate(Snapshot(registry, Item(7, "Sword")), registry);

        ContentFinding finding = Single(report, "KEC0001");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0002_fires_on_a_key_that_is_not_unique_within_its_type()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword"), Item(8, "sword"));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0002");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(8, finding.Id);
    }

    [Fact]
    public void KEC0003_fires_when_a_key_moved_on_a_published_row_and_only_then()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot previous = Snapshot(registry, Item(7, "sword"));
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sabre"));

        ContentValidationReport report = Validate(candidate, registry, previous);

        ContentFinding finding = Single(report, "KEC0003");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
        AssertNone(report, "KEC0000");
        AssertNone(Validate(candidate, registry), "KEC0003");
    }

    [Fact]
    public void KEC0004_fires_on_a_row_carrying_a_field_its_schema_does_not_declare()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, WithExtraField(Item(7, "sword")));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0004");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0005_fires_on_a_live_row_missing_a_required_field()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentRow row = WithField(Item(7, "sword"), 4, ContentFieldValue.Absent(ContentFieldKind.Int));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0005");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
        Assert.Contains("max_stack", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KEC0006_fires_on_a_reference_to_a_row_that_is_not_live()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            LootTable(100, "goblin"),
            LootEntry(500, "ghost", table: 100, item: 999));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0006");
        Assert.Equal(LootEntryType, finding.Type);
        Assert.Equal(500, finding.Id);
    }

    [Fact]
    public void KEC0007_fires_on_a_reference_to_a_content_type_that_was_never_registered()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword", equipProfile: 5));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0007");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
        Assert.Contains(EngineContentTypes.EquipProfileTypeKey, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KEC0008_fires_on_a_tag_list_naming_a_tag_that_is_not_live()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword", tagIds: [42]));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0008");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0009_fires_on_a_definition_id_of_zero()
    {
        ContentTypeRegistry registry = EngineRegistry();

        ContentValidationReport report = Validate(Snapshot(registry, Item(0, "sword")), registry);

        ContentFinding finding = Single(report, "KEC0009");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(0, finding.Id);
    }

    [Fact]
    public void KEC0010_is_quiet_because_a_snapshot_carries_no_family_claim()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0010");
    }

    [Fact]
    public void KEC0011_is_quiet_because_a_snapshot_carries_no_family_blocks()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0011");
    }

    [Fact]
    public void KEC0012_is_quiet_because_a_snapshot_carries_no_family_blocks()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0012");
    }

    [Fact]
    public void KEC0013_is_withdrawn_and_is_never_reissued()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(0, "Sword", stackable: true, maxStack: 1, equipProfile: 5),
            Item(0, "Sword"));

        ContentValidationReport report = Validate(candidate, registry);

        Assert.False(report.IsValid, Describe(report));
        AssertNone(report, "KEC0013");
    }

    [Fact]
    public void KEC0014_accepts_the_per_field_server_only_override_on_a_client_type()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("secret", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, false));
        ContentTypeRegistry registry = GameRegistry(schema);
        ContentRow row = GameRow(5, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 3));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        AssertNone(report, "KEC0014");
        Assert.True(report.IsValid, Describe(report));
    }

    [Fact]
    public void KEC0014_accepts_a_required_server_only_field_carrying_a_value()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("secret", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true));
        ContentTypeRegistry registry = GameRegistry(schema);
        ContentRow row = GameRow(5, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 3));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        AssertNone(report, "KEC0014", "KEC0005");
        Assert.True(report.IsValid, Describe(report));
    }

    [Fact]
    public void KEC0015_fires_on_a_rule_set_that_is_not_idempotent()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(10, "one"),
            Item(11, "two"),
            Item(12, "three"));
        RemapRule[] rules =
        [
            new RemapRule(1, 1, ItemType, RemapRuleKind.ReplacedBy, 10, 11, default),
            new RemapRule(2, 1, ItemType, RemapRuleKind.ReplacedBy, 12, 10, default),
        ];

        ContentValidationReport report = Validate(candidate, registry, rules: rules);

        ContentFinding finding = Single(report, "KEC0015");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(12, finding.Id);
    }

    [Fact]
    public void KEC0016_fires_on_a_rule_whose_source_never_existed()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword"));
        RemapRule[] rules = [new RemapRule(1, 1, ItemType, RemapRuleKind.ReplacedBy, 999, 7, default)];

        ContentValidationReport report = Validate(candidate, registry, rules: rules);

        ContentFinding finding = Single(report, "KEC0016");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(999, finding.Id);
    }

    [Fact]
    public void KEC0017_fires_on_a_rule_whose_destination_is_not_live()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword"));
        RemapRule[] rules = [new RemapRule(1, 1, ItemType, RemapRuleKind.ReplacedBy, 7, 999, default)];

        ContentValidationReport report = Validate(candidate, registry, rules: rules);

        ContentFinding finding = Single(report, "KEC0017");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0018_fires_on_a_sequence_that_is_not_contiguous_from_one()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword"), Item(8, "sabre"));
        RemapRule[] rules = [new RemapRule(5, 1, ItemType, RemapRuleKind.ReplacedBy, 7, 8, default)];

        ContentValidationReport report = Validate(candidate, registry, rules: rules);

        ContentFinding finding = Single(report, "KEC0018");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0019_fires_on_a_payload_that_is_malformed_for_its_kind()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword"));
        RemapRule[] rules =
        [
            new RemapRule(1, 1, ItemType, RemapRuleKind.StackCapLowered, 7, 0, new byte[sizeof(int)]),
        ];

        ContentValidationReport report = Validate(candidate, registry, rules: rules);

        ContentFinding finding = Single(report, "KEC0019");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0020_fires_on_a_stat_scale_that_is_not_a_power_of_ten()
    {
        ContentTypeRegistry registry = EngineRegistry();

        ContentValidationReport report = Validate(Snapshot(registry, Stat(3, "attack", scale: 7)), registry);

        ContentFinding finding = Single(report, "KEC0020");
        Assert.Equal(StatType, finding.Type);
        Assert.Equal(3, finding.Id);
    }

    [Fact]
    public void KEC0021_fires_on_a_stat_whose_min_exceeds_its_max()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Stat(3, "attack", min: 5, max: 1));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0021");
        Assert.Equal(StatType, finding.Type);
        Assert.Equal(3, finding.Id);
    }

    [Fact]
    public void KEC0022_fires_on_a_stackable_definition_that_declares_durability()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentRow row = Item(7, "potion", stackable: true, maxStack: 20, durabilityMax: 10);

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0022");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0023_fires_on_a_loot_entry_that_names_no_draw()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            LootTable(100, "goblin"),
            LootEntry(500, "empty", table: 100));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0023");
        Assert.Equal(LootEntryType, finding.Type);
        Assert.Equal(500, finding.Id);
    }

    [Fact]
    public void KEC0024_fires_on_a_loot_table_graph_with_a_cycle()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            LootTable(100, "goblin"),
            LootEntry(500, "loop", table: 100, nestedTable: 100));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0024");
        Assert.Equal(LootTableType, finding.Type);
        Assert.Equal(100, finding.Id);
    }

    [Fact]
    public void KEC0025_fires_on_a_stackable_row_capped_at_one()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentRow row = Item(7, "potion", stackable: true, maxStack: 1);

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0025");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0026_fires_on_a_row_over_its_types_row_cap()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("blob", ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false));
        ContentTypeRegistry registry = GameRegistry(schema, maxRowBytes: 64);
        ContentRow row = GameRow(5, "thing", Blob(200));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0026");
        Assert.Equal(GameType, finding.Type);
        Assert.Equal(5, finding.Id);
    }

    [Fact]
    public void KEC0027_fires_on_a_codec_whose_round_trip_is_not_byte_identical()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, true));
        ContentTypeRegistry registry = GameRegistry(
            schema,
            codec: new DriftingCodec(GameType, "n"));
        ContentRow row = GameRow(5, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 5));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0027");
        Assert.Equal(GameType, finding.Type);
        Assert.Equal(5, finding.Id);
    }

    [Fact]
    public void KEC0028_is_quiet_because_registration_refuses_an_illegal_chunk_slot_count_first()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, false));

        Assert.Throws<ContentRegistrationException>(() =>
        {
            _ = GameRegistry(schema, chunkSlots: 100);
        });

        ContentTypeRegistry registry = EngineRegistry();
        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0028");
    }

    [Fact]
    public void KEC0029_fires_when_a_published_types_id_is_no_longer_registered()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, false));
        ContentTypeRegistry published = GameRegistry(schema);
        ContentSnapshot previous = Snapshot(
            published,
            GameRow(5, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)));
        var reassigned = new ContentTypeRegistry();

        ContentValidationReport report = Validate(Snapshot(reassigned), reassigned, previous);

        ContentFinding finding = Single(report, "KEC0029");
        Assert.Equal(GameType, finding.Type);
        Assert.Equal(0, finding.Id);
    }

    [Fact]
    public void KEC0030_fires_on_a_derived_localized_text_key_over_its_bound()
    {
        string typeKey = Repeat('a', 64);
        string fieldName = Repeat('b', 64);
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry(fieldName, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true));
        ContentTypeRegistry registry = GameRegistry(schema, typeKey: typeKey);
        ContentRow row = GameRow(5, Repeat('c', 64), ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0030");
        Assert.Equal(GameType, finding.Type);
        Assert.Equal(5, finding.Id);
    }

    [Fact]
    public void KEC0031_fires_on_a_non_zero_parent_id()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword", parentId: 8), Item(8, "sabre"));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0031");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0032_is_quiet_while_every_parent_id_is_zero()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0032");
    }

    [Fact]
    public void KEC0033_is_quiet_while_every_parent_id_is_zero()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0033");
    }

    [Fact]
    public void KEC0034_is_quiet_while_every_parent_id_is_zero()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0034");
    }

    [Fact]
    public void KEC0035_is_quiet_while_every_parent_id_is_zero()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0035");
    }

    [Fact]
    public void KEC0036_fires_on_two_live_rows_sharing_a_definition_id()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword"), Item(7, "sabre"));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0036");
        Assert.Equal(ItemType, finding.Type);
        Assert.Equal(7, finding.Id);
    }

    [Fact]
    public void KEC0037_is_quiet_because_a_snapshot_carries_no_family_blocks()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0037");
    }

    [Fact]
    public void KEC0038_fires_on_a_chunk_over_the_uncompressed_ceiling()
    {
        const int RowBytes = 4_200_000;
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("blob", ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false));
        ContentTypeRegistry registry = GameRegistry(schema, maxRowBytes: ContentPackFormat.MaxContentRowBytes);
        ContentSnapshot candidate = Snapshot(
            registry,
            GameRow(1, "one", Blob(RowBytes)),
            GameRow(2, "two", Blob(RowBytes)),
            GameRow(3, "three", Blob(RowBytes)),
            GameRow(4, "four", Blob(RowBytes)));

        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0038");
        Assert.Equal(GameType, finding.Type);
        Assert.Equal(0, finding.Id);
    }

    [Fact]
    public void KEC0039_is_quiet_because_the_rollback_path_emits_it_rather_than_the_sweep()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "sword", isRetired: true));
        RemapRule[] rules =
        [
            new RemapRule(1, 1, ItemType, RemapRuleKind.Retired, 7, 0, [RemapRule.RetirePolicyPlaceholder]),
        ];

        AssertNone(Validate(candidate, registry, rules: rules), "KEC0039");
    }

    [Fact]
    public void KEC0040_carries_a_type_validators_finding_under_the_engines_own_code()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, false));
        ContentTypeRegistry registry = GameRegistry(
            schema,
            validator: new SpeakingValidator("GAME001", "a rule of the game's own"));
        ContentRow row = GameRow(5, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 1));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0040");
        Assert.Equal(GameType, finding.Type);
        Assert.Equal(5, finding.Id);
        Assert.StartsWith(GameTypeKey, finding.Message, StringComparison.Ordinal);
        Assert.Contains("a rule of the game's own", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KEC0041_is_quiet_because_the_fork_path_emits_it_rather_than_the_sweep()
    {
        ContentTypeRegistry registry = EngineRegistry();

        AssertNone(Validate(CleanCandidate(registry), registry), "KEC0041");
    }

    [Fact]
    public void KEC0042_fires_on_a_definition_id_over_the_types_declared_ceiling()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, false));
        ContentTypeRegistry registry = GameRegistry(schema, maxDefinitionId: 100);
        ContentRow row = GameRow(101, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 1));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0042");
        Assert.Equal(GameType, finding.Type);
        Assert.Equal(101, finding.Id);
    }
}
