using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Validation;

/// <summary>
/// Spec 12.2's thirteen checks, one fact each, over contracts 10.1, 10.2 and 10.4. Eight of them are
/// STRUCTURAL and quarantine, three are DRIFT and quarantine, check 12 is POLICY and is tolerated, and
/// check 13 is POLICY and answers Retired without quarantining anything.
/// <para>
/// The two a reader gets wrong without a test are 12 and 13, so they come first.
/// </para>
/// </summary>
public class InstanceValidatorTests
{
    [Fact]
    public void Check_12_an_over_cap_count_is_counted_and_changes_nothing()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, count: InstanceValidationFixtures.LiveItemStackCap + 1),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem, count: InstanceValidationFixtures.LiveItemStackCap),
            ],
            types,
            snapshot);

        InstanceValidationFinding finding = report.At(0);
        Assert.Equal(12, finding.Check);
        Assert.Equal(InstanceValidationReason.OverCap, finding.Reason);
        Assert.Equal(InstanceValidationOutcome.Valid, finding.Outcome);

        // Counted, and nothing else moved: the page still loads, no record is quarantined, the counter the
        // telemetry reads stays empty, and the in-cap entry beside it has no finding at all.
        Assert.False(report.HasQuarantine);
        Assert.Equal(0, report.QuarantinedRecords);
        Assert.Empty(report.ReasonCounts);
        Assert.Single(report.Findings);
        Assert.Equal(InstanceValidationOutcome.Valid, report.OutcomeAt(0));
        Assert.Equal(InstanceValidationOutcome.Valid, report.OutcomeAt(1));
    }

    [Fact]
    public void Check_12_is_silent_when_a_StackCapLowered_rule_explains_the_count()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        var lowered = new RemapRule(
            1,
            InstanceValidationFixtures.ActiveVersion,
            InstanceValidationFixtures.ItemType,
            RemapRuleKind.StackCapLowered,
            InstanceValidationFixtures.LiveItem,
            0,
            StackCap(InstanceValidationFixtures.LiveItemStackCap));
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types, [lowered]);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, count: InstanceValidationFixtures.LiveItemStackCap + 5)],
            types,
            snapshot);

        Assert.True(report.IsValid, InstanceValidationFixtures.Describe(report));
    }

    [Fact]
    public void Check_13_a_RETIRED_definition_is_not_a_quarantine_and_the_container_still_loads()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.RetiredItem),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem),
            ],
            types,
            snapshot);

        InstanceValidationFinding finding = report.At(0);
        Assert.Equal(13, finding.Check);
        Assert.Equal(InstanceValidationReason.DefinitionRetired, finding.Reason);
        Assert.True(finding.IsRetired);

        // Contracts 10.1's outcome stays Valid: the bytes are not wrapped, the entry still decodes and the
        // container still loads. What changes is the presentation, and that rides in the finding.
        Assert.Equal(InstanceValidationOutcome.Valid, finding.Outcome);
        Assert.Equal(InstanceValidationOutcome.Valid, report.OutcomeAt(0));
        Assert.False(report.HasQuarantine);
        Assert.True(report.IsRetiredAt(0));
        Assert.False(report.IsRetiredAt(1));

        // It increments no counter and adds no reason code, because spec 12.4 gives it no ordinal.
        Assert.Empty(report.ReasonCounts);
        Assert.False(InstanceQuarantineReason.TryGetOrdinal(InstanceValidationReason.DefinitionRetired, out _));
    }

    [Fact]
    public void Check_13_reads_a_socket_s_contained_definition_at_depth()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        byte[] payload = InstanceValidationFixtures.SocketPayload(InstanceValidationFixtures.RetiredItem);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 11, payload: payload)],
            types,
            snapshot);

        Assert.True(report.IsRetiredAt(0));
        Assert.False(report.HasQuarantine);
    }

    [Fact]
    public void A_structural_failure_quarantines_that_ENTRY_and_leaves_the_rest_alone()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 5, payload: InstanceValidationFixtures.AffixPayload()),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem, instanceId: 6, payload: OutOfOrder()),
                InstanceValidationFixtures.Slot(2, InstanceValidationFixtures.LiveItem, instanceId: 7, payload: InstanceValidationFixtures.AffixPayload()),
            ],
            types,
            snapshot);

        Assert.Equal(InstanceValidationOutcome.Quarantined, report.OutcomeAt(1));
        Assert.Equal(InstanceValidationOutcome.Valid, report.OutcomeAt(0));
        Assert.Equal(InstanceValidationOutcome.Valid, report.OutcomeAt(2));
        Assert.Equal(1, report.QuarantinedRecords);
        Assert.Equal(InstancePayloadReason.KindOutOfOrder, report.At(1).Reason);
        Assert.Equal(1, report.At(1).Check);
    }

    [Fact]
    public void An_unresolved_content_reference_quarantines_because_no_rule_covered_it()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(
                    0,
                    InstanceValidationFixtures.LiveItem,
                    instanceId: 5,
                    payload: InstanceValidationFixtures.AffixPayload(InstanceValidationFixtures.MissingId)),
            ],
            types,
            snapshot);

        InstanceValidationFinding finding = report.At(0);
        Assert.Equal(7, finding.Check);
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, finding.Reason);
        Assert.Equal(InstanceValidationOutcome.Quarantined, finding.Outcome);

        // The reason carries the durable ordinal the KECQ wrapper stores, which is the bridge between this
        // report and the bytes the caller writes.
        Assert.True(InstanceQuarantineReason.TryGetOrdinal(finding.Reason!, out byte ordinal));
        Assert.Equal(10, ordinal);
    }

    [Fact]
    public void The_validator_accumulates_and_never_stops_at_the_first_finding()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 5, payload: OutOfOrder()),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.MissingId, instanceId: 6),
                InstanceValidationFixtures.Slot(2, InstanceValidationFixtures.LiveItem, count: 99),
                InstanceValidationFixtures.Slot(3, InstanceValidationFixtures.RetiredItem),
            ],
            types,
            snapshot);

        Assert.Equal(4, report.Findings.Count);
        Assert.Equal(2, report.QuarantinedRecords);
        Assert.Equal([1, 6, 12, 13], report.Findings.Select(f => f.Check).ToArray());
    }

    [Fact]
    public void A_quarantined_entry_reports_its_stored_reason_and_stamp()
    {
        const int wrapperStamp = 3;
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        byte[] wrapper = QuarantineWrapper.Wrap(
            InstanceQuarantineReason.UnknownContentReference,
            wrapperStamp,
            InstanceValidationFixtures.AffixPayload(InstanceValidationFixtures.MissingId));

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(
                    0,
                    InstanceValidationFixtures.LiveItem,
                    instanceId: 5,
                    payload: wrapper,
                    flags: ItemContainerPageCodec.EntryFlagQuarantined),
            ],
            types,
            snapshot);

        InstanceValidationFinding finding = report.At(0);
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, finding.Reason);
        Assert.Equal(wrapperStamp, finding.StampedVersion);
        Assert.Equal(InstanceValidationOutcome.Quarantined, finding.Outcome);
    }

    [Fact]
    public void The_validator_never_throws_never_logs_never_counts_and_never_mutates_its_input()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        InstancePropertyRegistry properties = InstanceValidationFixtures.Properties();

        // Nothing in the signature can log or count: no ILogger, no delegate, anywhere.
        foreach (MethodInfo method in typeof(InstanceValidator).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Type type = parameter.ParameterType;
                Assert.False(
                    typeof(Delegate).IsAssignableFrom(type) || type.Name.Contains("ILogger", StringComparison.Ordinal),
                    FormattableString.Invariant($"{method.Name} takes {type.Name}, which would let the validator log or count."));
            }
        }

        // Nothing it is handed comes back changed, and no byte sequence makes it throw.
        var random = new Random(20260915);
        for (int i = 0; i < 400; i++)
        {
            byte[] payload = new byte[random.Next(0, 48)];
            random.NextBytes(payload);
            byte[] copy = payload.ToArray();
            var entry = new PageEntry(3, 0, InstanceValidationFixtures.LiveItem, 1, 77, 0, payload.Length);
            _ = InstanceValidator.ValidateEntry(payload, entry, 4, properties, types, snapshot, out _);
            Assert.Equal(copy, payload);
        }
    }

    [Theory]
    [InlineData(1, InstancePayloadReason.KindOutOfOrder)]
    [InlineData(1, InstancePayloadReason.KindDuplicate)]
    [InlineData(1, InstancePayloadReason.VarintNotMinimal)]
    [InlineData(2, InstancePayloadReason.FieldTruncated)]
    [InlineData(3, InstancePayloadReason.FieldMalformed)]
    [InlineData(4, InstancePayloadReason.SocketNesting)]
    public void Checks_1_to_4_quarantine_on_the_token_spec_12_2_assigns(int check, string reason)
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 9, payload: Broken(reason))],
            types,
            snapshot);

        InstanceValidationFinding finding = report.At(0);
        Assert.Equal(reason, finding.Reason);
        Assert.Equal(check, finding.Check);
        Assert.Equal(InstanceValidationOutcome.Quarantined, finding.Outcome);
    }

    [Fact]
    public void Check_5_a_payload_over_the_cap_quarantines_through_the_standalone_door()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        byte[] payload = new byte[ItemInstancePayload.MaxInstancePayloadBytes + 1];

        InstanceValidationOutcome outcome = InstanceValidator.ValidateEntry(
            payload,
            new PageEntry(0, 0, InstanceValidationFixtures.LiveItem, 1, 12, 0, payload.Length),
            InstanceValidationFixtures.ActiveVersion,
            InstanceValidationFixtures.Properties(),
            types,
            snapshot,
            out InstanceValidationFinding finding);

        Assert.Equal(InstanceValidationOutcome.Quarantined, outcome);
        Assert.Equal(5, finding.Check);
        Assert.Equal(InstancePayloadReason.PayloadTooLong, finding.Reason);
    }

    [Fact]
    public void Check_6_an_unknown_definition_quarantines_at_the_entry_and_inside_a_socket()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.MissingId),
                InstanceValidationFixtures.Slot(
                    1,
                    InstanceValidationFixtures.LiveItem,
                    instanceId: 8,
                    payload: InstanceValidationFixtures.SocketPayload(InstanceValidationFixtures.MissingId)),
            ],
            types,
            snapshot);

        Assert.Equal(6, report.At(0).Check);
        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, report.At(0).Reason);
        Assert.Equal(6, report.At(1).Check);
        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, report.At(1).Reason);
        Assert.Equal(2, report.QuarantinedRecords);
    }

    // 9 is a tier ordinal the mod never had. 4 is InstanceValidationFixtures.RetiredTier, a tier whose row
    // exists and is RETIRED, which is the half of "live" a plain lookup misses.
    [Theory]
    [InlineData(9)]
    [InlineData(4)]
    public void Check_8_a_tier_the_mod_does_not_have_LIVE_quarantines(int tier)
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(
                    0,
                    InstanceValidationFixtures.LiveItem,
                    instanceId: 5,
                    payload: InstanceValidationFixtures.AffixPayload(tier: (byte)tier)),
            ],
            types,
            snapshot);

        InstanceValidationFinding finding = report.At(0);
        Assert.Equal(8, finding.Check);
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, finding.Reason);
        Assert.Equal(InstanceValidationOutcome.Quarantined, finding.Outcome);
    }

    [Fact]
    public void Check_9_a_non_empty_payload_with_no_instance_id_quarantines()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 0, payload: InstanceValidationFixtures.AffixPayload()),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem, instanceId: 0),
            ],
            types,
            snapshot);

        Assert.Equal(9, report.At(0).Check);
        Assert.Equal(InstanceQuarantineReason.InstanceIdMissing, report.At(0).Reason);
        Assert.Equal(1, report.QuarantinedRecords);
    }

    [Fact]
    public void Check_10_is_container_wide_so_both_sharers_quarantine_and_ValidateEntry_cannot_see_it()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        byte[] payload = InstanceValidationFixtures.AffixPayload();

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 4242, payload: payload),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem, instanceId: 4242, payload: payload),
                InstanceValidationFixtures.Slot(2, InstanceValidationFixtures.LiveItem, instanceId: 4243, payload: payload),
            ],
            types,
            snapshot);

        Assert.Equal(10, report.At(0).Check);
        Assert.Equal(10, report.At(1).Check);
        Assert.Equal(InstanceQuarantineReason.InstanceIdDuplicate, report.At(0).Reason);
        Assert.Equal(2, report.QuarantinedRecords);
        Assert.Equal(InstanceValidationOutcome.Valid, report.OutcomeAt(2));

        // The standalone door sees ONE entry, so it cannot answer a container-wide question and does not
        // pretend to: the same entry on its own is valid.
        InstanceValidationOutcome alone = InstanceValidator.ValidateEntry(
            payload,
            new PageEntry(0, 0, InstanceValidationFixtures.LiveItem, 1, 4242, 0, payload.Length),
            InstanceValidationFixtures.ActiveVersion,
            InstanceValidationFixtures.Properties(),
            types,
            snapshot,
            out _);
        Assert.Equal(InstanceValidationOutcome.Valid, alone);
    }

    [Fact]
    public void Check_10_leaves_a_zero_instance_id_alone_because_every_plain_stack_carries_one()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem),
                InstanceValidationFixtures.Slot(2, InstanceValidationFixtures.GemItem),
            ],
            types,
            snapshot);

        Assert.True(report.IsValid, InstanceValidationFixtures.Describe(report));
    }

    [Theory]
    [InlineData(InstancePropertyKind.Durability)]
    [InlineData(InstancePropertyKind.Sockets)]
    public void Check_11_an_entry_carrying_kind_5_or_132_with_a_count_above_one_quarantines(ushort kind)
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        byte[] payload = kind == InstancePropertyKind.Sockets
            ? InstanceValidationFixtures.SocketPayload()
            : InstanceValidationFixtures.Encode(new ItemInstancePayloadBuilder().AddScalars(InstancePropertyKind.Durability, 40, 100));

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, count: 2, instanceId: 31, payload: payload)],
            types,
            snapshot);

        InstanceValidationFinding finding = report.At(0);
        Assert.Equal(11, finding.Check);
        Assert.Equal(InstanceQuarantineReason.StackNotInstanceable, finding.Reason);
        Assert.Equal(InstanceValidationOutcome.Quarantined, finding.Outcome);
    }

    [Fact]
    public void No_phase_1_input_reaches_Remapped_and_the_member_ships_anyway()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        // The whole fixture vocabulary at once: clean, broken, drifted, over cap and retired.
        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 1, payload: InstanceValidationFixtures.AffixPayload()),
                InstanceValidationFixtures.Slot(1, InstanceValidationFixtures.LiveItem, instanceId: 2, payload: OutOfOrder()),
                InstanceValidationFixtures.Slot(2, InstanceValidationFixtures.MissingId, instanceId: 3),
                InstanceValidationFixtures.Slot(3, InstanceValidationFixtures.LiveItem, count: 99),
                InstanceValidationFixtures.Slot(4, InstanceValidationFixtures.RetiredItem),
                InstanceValidationFixtures.Slot(5, InstanceValidationFixtures.LiveItem, instanceId: 6, payload: InstanceValidationFixtures.SocketPayload()),
            ],
            types,
            snapshot);

        Assert.DoesNotContain(report.Findings, f => f.Outcome == InstanceValidationOutcome.Remapped);
        for (int slot = 0; slot < 6; slot++)
        {
            Assert.NotEqual(InstanceValidationOutcome.Remapped, report.OutcomeAt(slot));
        }

        // Contracts 10.1's vocabulary ships whole from the start, so a caller switching over it compiles
        // once against the final set. The remap pass that reaches the third member is the phase 2-3 plan's.
        Assert.Equal(3, Enum.GetValues<InstanceValidationOutcome>().Length);
    }

    [Fact]
    public void The_two_doors_are_one_sweep_and_answer_the_same_thing()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);
        byte[][] payloads =
        [
            [],
            InstanceValidationFixtures.AffixPayload(),
            InstanceValidationFixtures.AffixPayload(InstanceValidationFixtures.MissingId),
            InstanceValidationFixtures.SocketPayload(),
            OutOfOrder(),
        ];

        for (int i = 0; i < payloads.Length; i++)
        {
            byte[] payload = payloads[i];
            var slots = new[]
            {
                InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 40 + i, payload: payload),
            };
            InstanceValidationReport report = InstanceValidationFixtures.Sweep(slots, types, snapshot);
            InstanceValidationOutcome outcome = InstanceValidator.ValidateEntry(
                payload,
                new PageEntry(0, 0, InstanceValidationFixtures.LiveItem, 1, 40 + i, 0, payload.Length),
                InstanceValidationFixtures.ActiveVersion,
                InstanceValidationFixtures.Properties(),
                types,
                snapshot,
                out InstanceValidationFinding finding);

            Assert.Equal(report.OutcomeAt(0), outcome);
            Assert.Equal(report.Findings.Count == 0 ? 0 : report.At(0).Check, finding.Check);
        }
    }

    [Fact]
    public void An_unregistered_content_TYPE_fails_CLOSED_rather_than_skipping_the_check()
    {
        // The engine types alone: nothing registers 'mod', so an affix's mod id cannot resolve. A skip here
        // would let an item carrying a reference the active content cannot explain stay fully usable.
        var types = new ContentTypeRegistry();
        EngineContentTypes.Register(types);
        var builder = new ContentSnapshotBuilder(types);
        builder.WithIdentity(InstanceValidationFixtures.ActiveVersion, "engine-only");
        builder.AddRow(new ContentRow(
            InstanceValidationFixtures.ItemType,
            InstanceValidationFixtures.LiveItem,
            new ContentKey("sword"),
            0,
            false,
            new ContentFieldValue[17]));
        ContentSnapshot snapshot = builder.Build();

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 5, payload: InstanceValidationFixtures.AffixPayload())],
            types,
            snapshot);

        Assert.Equal(InstanceValidationOutcome.Quarantined, report.OutcomeAt(0));
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, report.At(0).Reason);
    }

    [Fact]
    public void An_unknown_kind_is_never_inspected_and_never_quarantines()
    {
        ContentTypeRegistry types = InstanceValidationFixtures.Types();
        ContentSnapshot snapshot = InstanceValidationFixtures.Snapshot(types);

        // Kind 2000 is a game kind this build never registered. Contracts 9.4 keeps it verbatim, so its
        // bytes are not walked, no reference is read out of them and nothing about them can quarantine.
        byte[] payload = InstanceValidationFixtures.Encode(
            new ItemInstancePayloadBuilder().Add(2000, [0xFF, 0x0F, 0x27]));

        InstanceValidationReport report = InstanceValidationFixtures.Sweep(
            [InstanceValidationFixtures.Slot(0, InstanceValidationFixtures.LiveItem, instanceId: 3, payload: payload)],
            types,
            snapshot);

        Assert.True(report.IsValid, InstanceValidationFixtures.Describe(report));
    }

    static byte[] StackCap(int cap)
    {
        byte[] payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, cap);
        return payload;
    }

    /// <summary>Kind 2 then kind 1, which is descending and therefore not canonical.</summary>
    static byte[] OutOfOrder() => [2, 1, 0x2A, 1, 1, 0x01];

    static byte[] Broken(string reason) => reason switch
    {
        InstancePayloadReason.KindOutOfOrder => OutOfOrder(),
        InstancePayloadReason.KindDuplicate => [2, 1, 0x2A, 2, 1, 0x2A],
        InstancePayloadReason.VarintNotMinimal => [0x82, 0x00, 1, 0x2A],
        InstancePayloadReason.FieldTruncated => [2, 5, 0x2A],
        InstancePayloadReason.FieldMalformed => [2, 2, 0x2A, 0x2A],
        InstancePayloadReason.SocketNesting => NestedSocket(),
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No fixture for that token."),
    };

    /// <summary>A socket whose nested payload carries kind 132 itself, which is the one level limit.</summary>
    static byte[] NestedSocket()
    {
        byte[] inner = InstanceValidationFixtures.Encode(new ItemInstancePayloadBuilder().AddSockets(
            [new InstanceSocket(InstanceValidationFixtures.LiveSocketType, InstanceValidationFixtures.GemItem, 7, default)]));
        return InstanceValidationFixtures.Encode(new ItemInstancePayloadBuilder().AddSockets(
            [new InstanceSocket(InstanceValidationFixtures.LiveSocketType, InstanceValidationFixtures.GemItem, 8, inner)]));
    }
}
