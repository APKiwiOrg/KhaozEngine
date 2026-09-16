using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerLoadFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// Spec 5.5's five steps, as five facts, plus the two the spec is otherwise silent about: the page written
/// into the wrong section (13 row 10) and the entry a remap rule named and could not move.
/// <para>
/// The ORDER in the second fact is the part that is easy to get backwards and it changes the meaning of every
/// drift finding, so it is asserted rather than assumed: rules run BEFORE the validator, so a drift finding
/// means NO RULE COVERED IT.
/// </para>
/// </summary>
public class ContainerLoadTests
{
    [Fact]
    public void A_page_that_fails_at_the_PAGE_level_quarantines_as_a_UNIT()
    {
        // Spec 5.5 step 1: a page that cannot be parsed has no entries to keep, so it fails whole. The page
        // beside it is untouched by that, which is the half of the rule that matters on a ten page bank.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        JournalProjectionSection torn = Section(0, [0x02, 0x00, 0x00, 0x00]);
        JournalProjectionSection good = Page(1, ActiveVersion, Slot(100, Sword, count: 5));

        ContainerLoadResult result = ContainerLoad.Load([torn, good], snapshot, Context(types, Properties()));

        Assert.Single(result.Pages);
        Assert.Equal(1, result.Pages[0].PageIndex);
        Assert.Equal(Sword, result.PageAt(1).SlotAt(100).Stack.ItemId);

        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.PageQuarantined));
        Assert.Equal(0, finding.PageIndex);
        Assert.Equal("bank/p00", finding.SectionName);
        Assert.Equal(ItemContainerPageReason.Truncated, finding.Reason);
        Assert.Equal(-1, finding.Slot);
        Assert.True(result.HasQuarantine, Describe(result));
    }

    [Fact]
    public void Rules_apply_BEFORE_the_validator_runs_so_a_drift_finding_means_no_rule_covered_it()
    {
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] payload = AffixPayload(MissingId);
        JournalProjectionSection[] sections = [Page(0, 0, Slot(0, Sword, payload: payload, instanceId: Instance))];

        // With the rule, the drifted mod id is moved to a live one before the sweep sees it, so the sweep
        // finds nothing at all.
        RemapRuleSet rules = Rules(Replaced(1, ActiveVersion, Type(types, ModKey), MissingId, Mod));
        ContainerLoadResult covered = ContainerLoad.Load(sections, snapshot, Context(types, Properties(), rules));

        Assert.Empty(covered.Findings);
        Assert.True(covered.Reports[0].IsValid, Describe(covered));
        Assert.Equal(AffixPayload(Mod), covered.PageAt(0).SlotAt(0).Payload.ToArray());

        // Without it, the SAME page quarantines under unknown-content-reference. That is the whole meaning of
        // a drift finding: no rule covered it.
        ContainerLoadResult uncovered = ContainerLoad.Load(sections, snapshot, Context(types, Properties()));

        Assert.True(uncovered.Reports[0].TryGetQuarantine(0, out InstanceValidationFinding drift), Describe(uncovered));
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, drift.Reason);
        Assert.Equal(7, drift.Check);
    }

    [Fact]
    public void A_rule_that_changed_something_marks_the_page_dirty_and_does_not_write_it()
    {
        // Spec 5.5 step 3 and contracts 10.3: the rewrite is LAZY. It rides the next ordinary commit, because
        // rewriting every touched page at boot is a write storm proportional to the whole player base
        // arriving exactly when the server is coldest.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        JournalProjectionSection moved = Page(0, 0, Slot(0, Sword, payload: AffixPayload(MissingId), instanceId: Instance));
        JournalProjectionSection still = Page(1, 0, Slot(100, Sword, count: 5));
        byte[] storedBefore = moved.Data.ToArray();

        RemapRuleSet rules = Rules(Replaced(1, 5, Type(types, ModKey), MissingId, Mod));
        ContainerLoadResult result = ContainerLoad.Load([moved, still], snapshot, Context(types, Properties(), rules));

        ItemContainerPage dirty = Assert.Single(result.Dirty);
        Assert.Equal(0, dirty.PageIndex);
        Assert.True(dirty.IsDirty);
        Assert.Equal(5, dirty.ContentVersion);

        // A rule that changed NOTHING on the second page is a scan rather than a rewrite: not dirty, and its
        // stamp does not move.
        Assert.False(result.PageAt(1).IsDirty);
        Assert.Equal(0, result.PageAt(1).ContentVersion);

        // Nothing was written anywhere. The section still holds the bytes it arrived with.
        Assert.Equal(storedBefore, moved.Data.ToArray());
    }

    [Fact]
    public void A_failed_entry_check_quarantines_that_entry_and_leaves_the_page_loading()
    {
        // Spec 5.5 step 4: a failed check takes ONE entry out of play and the rest of the page loads. The
        // bytes are kept verbatim in the KECQ wrapper, which is what makes the recovery of 12.3 possible.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] broken = AffixPayload(MissingId);
        var counted = new List<(int Type, string Reason)>();
        var logger = new RecordingLogger();
        JournalProjectionSection[] sections =
        [
            Page(
                0,
                ActiveVersion,
                Slot(0, Sword, count: 5),
                Slot(1, Sword, payload: broken, instanceId: Instance),
                Slot(2, Sword, payload: AffixPayload(), instanceId: Instance + 1)),
        ];

        ContainerLoadResult result = ContainerLoad.Load(
            sections, snapshot, Context(types, Properties(), logger: logger, counter: (t, r) => counted.Add((t, r))));

        ItemContainerPage page = result.PageAt(0);
        Assert.False(page.SlotAt(0).Quarantined);
        Assert.False(page.SlotAt(2).Quarantined);
        Assert.True(page.SlotAt(1).Quarantined, Describe(result));

        // Verbatim, under the reason the check answered and the stamp the record failed at.
        Assert.True(QuarantineWrapper.TryUnwrap(
            page.SlotAt(1).Payload.Span, out ReadOnlySpan<byte> original, out string? reason, out int stamped));
        Assert.Equal(broken, original.ToArray());
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, reason);
        Assert.Equal(ActiveVersion, stamped);

        // Quarantining at LOAD does not dirty the page: it is not an operation and it is not a remap, so the
        // stored bytes stay as they are until something else rewrites the page.
        Assert.False(page.IsDirty);
        Assert.Empty(result.Dirty);
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.Single(counted);
        Assert.Equal((EngineContentTypes.ItemTypeId, InstanceQuarantineReason.UnknownContentReference), counted[0]);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Load_reads_no_store_touches_no_ambient_state_and_makes_one_pass()
    {
        // No store seam is an ARGUMENT of Load: it takes the sections a projection read already returned and
        // the active snapshot, and nothing else. What is left to prove is that it sweeps each page ONCE, and
        // the content reads are what say so: a second sweep reads every row a second time.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        JournalProjectionSection[] sections =
        [
            Page(0, ActiveVersion, Slot(0, Sword, payload: AffixPayload(), instanceId: Instance), Slot(1, Gem)),
            Page(1, ActiveVersion, Slot(100, Sword, payload: SocketPayload(), instanceId: Instance + 1)),
        ];

        var baseline = new CountingSnapshot(snapshot);
        foreach (JournalProjectionSection section in sections)
        {
            Sweep(section, types, baseline);
        }

        var counting = new CountingSnapshot(snapshot);
        var logger = new RecordingLogger();
        ContainerLoadResult result = ContainerLoad.Load(
            sections, counting, Context(types, Properties(), logger: logger));

        Assert.Empty(result.Findings);

        // Both halves read something, or the comparison below would hold over two zeroes.
        Assert.True(baseline.Reads > 0, "the baseline sweep read no content at all");
        Assert.Equal(baseline.Reads, counting.Reads);

        // Nothing was quarantined, so the one line is not emitted at all: checks 12 and 13 are the only
        // findings with no reason ordinal and neither is ever an alert.
        Assert.Empty(logger.Entries);

        // No ambient state: the same sections through a fresh context answer the same thing.
        ContainerLoadResult again = ContainerLoad.Load(sections, snapshot, Context(types, Properties()));
        Assert.Equal(result.Pages.Count, again.Pages.Count);
        Assert.Equal(
            result.PageAt(0).SlotAt(0).Payload.ToArray(), again.PageAt(0).SlotAt(0).Payload.ToArray());
    }

    [Fact]
    public void A_page_written_into_the_WRONG_section_is_refused()
    {
        // Spec 13 row 10. The header's own PageIndex is checked against the section name it arrived in, which
        // is the one thing ContainerSectionNames.Parse is for. The codec's FirstSlot check catches a page
        // whose header disagrees with ITSELF and cannot see the section name at all.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] pageThree = ItemContainerPageCodec.Encode(
            3, ItemContainerPage.FirstSlotOf(3), PageSlots, ActiveVersion, [Slot(300, Sword)]);
        JournalProjectionSection misfiled = Section(0, pageThree, ContainerSectionNames.Format(Container, 0));

        ContainerLoadResult result = ContainerLoad.Load([misfiled], snapshot, Context(types, Properties()));

        Assert.Empty(result.Pages);
        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.PageQuarantined));
        Assert.Equal(ContainerLoadReason.PageSectionMismatch, finding.Reason);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void A_section_that_is_not_this_containers_is_not_this_containers_problem()
    {
        // A stream carries profile, skills, quests and the other containers' pages. Walking them all and
        // keeping the ones that parse as this container is what makes Load take a whole projection read.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        JournalProjectionSection[] sections =
        [
            Section(0, [1, 2, 3], "profile"),
            Section(0, [1, 2, 3], ContainerSectionNames.Format("bag", 0)),
            Section(0, [1, 2, 3], "bank/p007"),
            Page(0, ActiveVersion, Slot(0, Sword)),
        ];

        ContainerLoadResult result = ContainerLoad.Load(sections, snapshot, Context(types, Properties()));

        Assert.Single(result.Pages);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void An_entry_the_pass_ABANDONS_is_a_finding_and_is_counted()
    {
        // https://github.com/APKiwiOrg/KhaozEngine/issues/931. A ReplacedBy whose destination is an id the
        // same item ALREADY carries produces an affix list holding one mod twice, which contracts 9.9
        // forbids, so the pass abandons the entry rather than writing bytes the decoder would refuse. The
        // publish validator cannot rule it out: its checks are over the rule LIST and it cannot see stored
        // payloads. What was missing is that NOTHING KNEW.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] twoAffixes = Encode(new ItemInstancePayloadBuilder().AddAffixes(
            InstancePropertyKind.Affixes,
            [new InstanceAffix(Mod, Tier, 17), new InstanceAffix(SecondMod, Tier, 18)]));
        var counted = new List<(int Type, string Reason)>();

        RemapRuleSet rules = Rules(Replaced(1, ActiveVersion, Type(types, ModKey), SecondMod, Mod));
        ContainerLoadResult result = ContainerLoad.Load(
            [Page(0, 0, Slot(4, Sword, payload: twoAffixes, instanceId: Instance))],
            snapshot,
            Context(types, Properties(), rules, counter: (t, r) => counted.Add((t, r))));

        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.RemapAbandoned));
        Assert.Equal(4, finding.Slot);
        Assert.Equal(0, finding.PageIndex);
        Assert.Equal(ContainerLoadReason.RemapAbandoned, finding.Reason);

        // The entry keeps the bytes it had and the page is not dirtied, so nothing half rewritten is ever
        // committed.
        Assert.Equal(twoAffixes, result.PageAt(0).SlotAt(4).Payload.ToArray());
        Assert.False(result.PageAt(0).IsDirty);

        // Counted through the same counter, under its own token, so a rule that could not be APPLIED never
        // reads as a rule that was never written.
        Assert.Contains((EngineContentTypes.ItemTypeId, ContainerLoadReason.RemapAbandoned), counted);
    }

    [Fact]
    public void The_rule_set_is_vetted_ONCE_for_the_whole_container()
    {
        // https://github.com/APKiwiOrg/KhaozEngine/issues/928. RemapRuleSet.IsIdempotent is a nested loop over
        // the whole rule list, and the pass used to run it before every page, so a 64 page container paid it
        // 64 times over a set that could not have changed. The vet moved to the context, which is per
        // CONTAINER, and the observable is that a broken set is refused even when no page would have reached
        // the pass at all: every page here is already at the active stamp.
        ContentTypeRegistry types = Types();
        ContentTypeId item = Type(types, ItemKey);
        RemapRuleSet chained = Rules(
            Replaced(1, 2, item, Sword, 200),
            Replaced(2, 2, item, 300, Sword));
        Assert.False(chained.IsIdempotent(out _));

        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => Context(types, Properties(), chained));
        Assert.Contains("contracts 8.3", refused.Message, StringComparison.Ordinal);

        // The vetted handle is the one the pass takes, so there is no "already vetted" flag to get wrong.
        VettedRemapRules vetted = VettedRemapRules.Vet(Rules(Replaced(1, 2, item, Sword, 200)));
        Assert.Equal(2, vetted.ActiveStamp);
    }

    [Fact]
    public void A_stored_page_flagging_an_entry_quarantined_over_no_bytes_never_loads_it_live()
    {
        // The shape no spec defines: spec 4.4 says a quarantined entry's payload IS the wrapper and spec
        // 12.4 pairs the flag with one, so the flag over zero bytes preserves nothing. Loading it as a live
        // non-quarantined stack CLEARED the flag and handed a player an item the store never vouched for,
        // with no finding anywhere. The decoder is the door it meets first, so the page fails as a unit,
        // and the seat door behind it never seats a quarantined entry live whatever it is handed.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);

        byte[] page = ItemContainerPageCodec.Encode(
            0, 0, PageSlots, ActiveVersion, [Slot(0, Sword, instanceId: Instance)]);
        Assert.Equal((byte)0, page[9]);
        page[9] = (byte)ItemContainerPageCodec.EntryFlagQuarantined;

        ContainerLoadResult result = ContainerLoad.Load(
            [Section(0, page)], snapshot, Context(types, Properties()));

        Assert.Empty(result.Pages);
        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.PageQuarantined));
        Assert.Equal(ItemContainerPageReason.EntryMalformed, finding.Reason);
        Assert.Equal(1, result.QuarantinedRecords);
    }

    /// <summary>One page's sweep, by hand, which is the baseline the one-pass fact is measured against.</summary>
    static void Sweep(JournalProjectionSection section, ContentTypeRegistry types, IContentSnapshot snapshot)
    {
        var entries = new PageEntry[PageSlots];
        ReadOnlySpan<byte> bytes = section.Data.Span;
        Assert.True(ItemContainerPageCodec.TryDecode(
            bytes, PageSlots, entries, out PageHeader header, out int count, out string? reason), reason);
        _ = InstanceValidator.Validate(bytes, header, entries.AsSpan(0, count), Properties(), types, snapshot);
    }
}
