using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.Catalog;
using Xunit;
using static KhaozEngine.Tests.Catalog.Validation.ContentValidationFixtures;

namespace KhaozEngine.Tests.Catalog.Validation;

/// <summary>
/// The facts about the SWEEP rather than about one code: that it accumulates, that it runs its passes in
/// order and never stops early, that it never throws for a content reason, and that an untrusted per-type
/// validator cannot take a publish down with a stack trace where a finding was expected.
/// <para>
/// Every candidate here is built in memory through <see cref="ContentSnapshotBuilder"/>, with no store, no
/// file and no registry beyond the one the test constructs, which is what spec 5.4 says a test does.
/// </para>
/// </summary>
public class ContentValidatorSweepTests
{
    /// <summary>The second game-band type the two-validator test needs, one past the fixture's own.</summary>
    const ushort SecondGameTypeId = 1025;

    /// <summary>The type key of that second game-band type.</summary>
    const string SecondGameTypeKey = "game_other";

    /// <summary>The first id of Scope B's reserved band, which phase 1 registers nothing in.</summary>
    const ushort InstancesTypeId = 256;

    [Fact]
    public void A_clean_candidate_is_valid_and_carries_only_the_informational_finding()
    {
        ContentTypeRegistry registry = EngineRegistry();

        ContentValidationReport report = Validate(CleanCandidate(registry), registry);

        Assert.True(report.IsValid, Describe(report));
        Assert.Equal(["KEC0000"], report.Findings.Select(finding => finding.Code).ToArray());
    }

    [Fact]
    public void The_sweep_accumulates_every_defect_rather_than_stopping_at_the_first()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(0, "Sword"),
            Item(7, "sword", tagIds: [42]),
            Stat(3, "attack", min: 5, max: 1));
        RemapRule[] rules = [new RemapRule(1, 1, ItemType, RemapRuleKind.ReplacedBy, 999, 7, default)];

        ContentValidationReport report = Validate(candidate, registry, rules: rules);

        Assert.False(report.IsValid, Describe(report));
        foreach (string code in new[] { "KEC0001", "KEC0008", "KEC0009", "KEC0016", "KEC0021" })
        {
            Assert.True(Has(report, code), FormattableString.Invariant($"Expected {code}. {Describe(report)}"));
        }
    }

    [Fact]
    public void The_five_passes_report_in_order_and_none_of_them_stops_early()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(
            registry,
            Item(7, "Sword", tagIds: [42]),
            Item(9, "potion", stackable: true, maxStack: 20, durabilityMax: 5),
            Stat(3, "attack", min: 5, max: 1));
        RemapRule[] rules = [new RemapRule(1, 1, ItemType, RemapRuleKind.ReplacedBy, 999, 7, default)];

        ContentValidationReport report = Validate(candidate, registry, rules: rules);

        int structure = FirstIndexOf(report, "KEC0001");
        int schema = FirstIndexOf(report, "KEC0021");
        int references = FirstIndexOf(report, "KEC0008");
        int codec = FirstIndexOf(report, "KEC0022");
        int remap = FirstIndexOf(report, "KEC0016");
        Assert.True(
            structure < schema && schema < references && references < codec && codec < remap,
            FormattableString.Invariant(
                $"Expected pass order 1 to 5 and got {structure}, {schema}, {references}, {codec}, {remap}. {Describe(report)}"));
    }

    [Fact]
    public void The_sweep_never_throws_for_a_content_reason()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new ContentFieldEntry("tags", ContentFieldKind.TagList, ContentFieldEntry.TagReferenceTarget, ContentVisibility.Client, false));
        ContentTypeRegistry registry = GameRegistry(schema);
        ContentSnapshot candidate = Snapshot(
            registry,

            // A row shorter than its schema, which the codec refuses to encode.
            GameRow(1, Repeat('z', 300)),

            // A tag list whose varint never terminates, pointing at a type this registry does not carry.
            GameRow(
                2,
                "thing",
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 1),
                ContentFieldValue.OfBytes(ContentFieldKind.TagList, new byte[] { 0x80 })),
            GameRow(2, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)));

        // A null rule and a sequence that starts nowhere, which the rule set's own constructor refuses.
        RemapRule[] rules =
        [
            null!,
            new RemapRule(9, 1, GameType, RemapRuleKind.ReplacedBy, 1, 2, default),
        ];

        ContentValidationReport? report = null;
        Exception? thrown = Record.Exception(() => report = Validate(candidate, registry, rules: rules));

        Assert.Null(thrown);
        Assert.NotNull(report);
        Assert.False(report.IsValid, Describe(report));
    }

    [Fact]
    public void A_type_validator_that_throws_becomes_one_finding_carrying_its_message()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, false));
        ContentTypeRegistry registry = GameRegistry(schema, validator: new ThrowingValidator("the game's own validator fell over"));
        ContentRow row = GameRow(5, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 1));

        ContentValidationReport report = Validate(Snapshot(registry, row), registry);

        ContentFinding finding = Single(report, "KEC0040");
        Assert.Equal(GameType, finding.Type);
        Assert.Contains(GameTypeKey, finding.Message, StringComparison.Ordinal);
        Assert.Contains("the game's own validator fell over", finding.Message, StringComparison.Ordinal);
        Assert.False(report.IsValid, Describe(report));
    }

    [Fact]
    public void One_type_validator_that_throws_does_not_silence_another_types()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, false));
        ContentTypeRegistry registry = GameRegistry(schema, validator: new ThrowingValidator("fell over"));
        var secondType = new ContentTypeId(SecondGameTypeId);
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            SecondGameTypeId,
            SecondGameTypeKey,
            new PlainCodec(secondType, schema),
            new SpeakingValidator("GAME002", "ran anyway"),
            schema,
            ContentVisibility.Client,
            ContentTypeRegistry.MinChunkSlots);
        ContentSnapshot candidate = Snapshot(
            registry,
            GameRow(5, "thing", ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)),
            new ContentRow(
                secondType,
                6,
                new ContentKey("other"),
                0,
                false,
                [ContentFieldValue.OfNumber(ContentFieldKind.Int, 2)]));

        ContentValidationReport report = Validate(candidate, registry);

        string[] messages = report.Findings
            .Where(finding => string.Equals(finding.Code, "KEC0040", StringComparison.Ordinal))
            .Select(finding => finding.Message)
            .ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Contains(messages, message => message.Contains("fell over", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("ran anyway", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_runs_over_one_candidate_agree_and_neither_touches_it()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = Snapshot(registry, Item(7, "Sword", tagIds: [42]), Stat(3, "attack", scale: 7));
        ContentFieldValue[] before = candidate.Rows(ItemType)[0].Fields.ToArray();

        ContentValidationReport first = Validate(candidate, registry);
        ContentValidationReport second = Validate(candidate, registry);

        Assert.Equal(Describe(first), Describe(second));
        Assert.Equal(before, candidate.Rows(ItemType)[0].Fields.ToArray());
    }

    [Fact]
    public void A_type_in_the_instances_band_is_swept_by_the_five_passes_and_emits_no_code_of_its_own()
    {
        ContentFieldSchema schema = Schema(
            new ContentFieldEntry("n", ContentFieldKind.Int, null, ContentVisibility.Client, false));
        var registry = new ContentTypeRegistry();
        var instancesType = new ContentTypeId(InstancesTypeId);
        registry.RegisterContentType(
            ContentRegistrationBand.Instances,
            InstancesTypeId,
            "instances_thing",
            new PlainCodec(instancesType, schema),
            null,
            schema,
            ContentVisibility.Client,
            ContentTypeRegistry.MinChunkSlots);
        ContentSnapshot candidate = Snapshot(
            registry,
            new ContentRow(
                instancesType,
                5,
                new ContentKey("Thing"),
                0,
                false,
                [ContentFieldValue.OfNumber(ContentFieldKind.Int, 1)]));

        ContentValidationReport report = Validate(candidate, registry);

        // Pass 1 still sees the malformed key, and pass 6 has nothing of its own to say until Scope B ships.
        Assert.True(Has(report, "KEC0001"), Describe(report));
        Assert.DoesNotContain(report.Findings, finding => IsInstanceBand(finding.Code));
    }

    [Fact]
    public void A_null_argument_is_a_programming_error_and_is_the_one_thing_that_throws()
    {
        ContentTypeRegistry registry = EngineRegistry();
        ContentSnapshot candidate = CleanCandidate(registry);

        Assert.Throws<ArgumentNullException>(() => ContentValidator.Validate(null!, null, [], registry));
        Assert.Throws<ArgumentNullException>(() => ContentValidator.Validate(candidate, null, null!, registry));
        Assert.Throws<ArgumentNullException>(() => ContentValidator.Validate(candidate, null, [], null!));
    }

    /// <summary>Where a code first appears in the report, which is the pass that emitted it.</summary>
    static int FirstIndexOf(ContentValidationReport report, string code)
    {
        IReadOnlyList<ContentFinding> findings = report.Findings;
        for (int i = 0; i < findings.Count; i++)
        {
            if (string.Equals(findings[i].Code, code, StringComparison.Ordinal))
            {
                return i;
            }
        }

        Assert.Fail(FormattableString.Invariant($"Expected {code}. {Describe(report)}"));
        return -1;
    }

    /// <summary>True for a code in Scope B's reserved band, which nothing in phase 1 emits.</summary>
    static bool IsInstanceBand(string code)
        => code.Length == 7
            && code.StartsWith("KEC", StringComparison.Ordinal)
            && int.TryParse(code.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number >= ContentValidator.InstanceBandFirstCode
            && number <= ContentValidator.InstanceBandLastCode;
}
