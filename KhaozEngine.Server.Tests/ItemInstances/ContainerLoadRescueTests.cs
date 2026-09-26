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
/// The unwrap step of spec 5.5, which spec 12.3 and 13 row 5 promise outright and which spec 5.5's five
/// steps do not contain: "a quarantined item is VISIBLY broken and recoverable in full, because the bytes are
/// kept verbatim and the first load after the missing rule publishes re-validates and restores the item
/// exactly".
/// <para>
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/929">#929</see> is why it is a step rather than
/// an assumption. The pass skips a quarantined entry, correctly, because contracts 10.2 requires the bytes be
/// kept verbatim. Then <c>ItemContainerPage.ApplyRemap</c> moves the PAGE stamp when any OTHER entry changes,
/// so the rule that would rescue the quarantined one beside it can never apply to that page again. The
/// wrapper's own stamped version is what closes that: it is the version the record failed under, it does not
/// move when the page does, and the unwrap runs the pass at THAT stamp.
/// </para>
/// </summary>
public class ContainerLoadRescueTests
{
    const int WrapperStamp = 3;
    const int PageStamp = 6;
    const int RuleVersion = 5;

    [Fact]
    public void A_quarantined_entry_whose_missing_rule_publishes_LATER_comes_back_on_the_next_load()
    {
        // The page has already moved PAST the rule: its stamp is 6 and the rule was introduced at 5, so the
        // page wide pass applies nothing at all. The wrapper is stamped 3, so the same rule still reaches the
        // entry inside it.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] original = AffixPayload(MissingId);
        RemapRuleSet rules = Rules(Replaced(1, RuleVersion, Type(types, ModKey), MissingId, Mod));

        ContainerLoadResult result = ContainerLoad.Load(
            [Page(0, PageStamp, Wrapped(2, Sword, InstanceQuarantineReason.UnknownContentReference, WrapperStamp, original))],
            snapshot,
            Context(types, Properties(), rules));

        ItemContainerPage page = result.PageAt(0);
        Assert.False(page.SlotAt(2).Quarantined, Describe(result));
        Assert.Equal(AffixPayload(Mod), page.SlotAt(2).Payload.ToArray());
        Assert.Equal(Sword, page.SlotAt(2).Stack.ItemId);
        Assert.Equal(Instance, page.SlotAt(2).Stack.InstanceId);

        // Dirty, so the next ordinary commit writes the rescued bytes. Never written here: the rewrite is
        // lazy for a rescue exactly as it is for a remap.
        Assert.True(page.IsDirty);
        Assert.Single(result.Dirty);

        // The page stamp is never LOWERED to the wrapper's, which is spec 5.5's policy for a page stamped
        // newer than the version a rule brings it to.
        Assert.Equal(PageStamp, page.ContentVersion);

        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryRescued));
        Assert.Equal(2, finding.Slot);
        Assert.Equal(WrapperStamp, finding.StampedVersion);
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, finding.Reason);
        Assert.Equal(0, result.QuarantinedRecords);
    }

    [Fact]
    public void A_still_broken_entry_keeps_its_wrapper_and_its_original_stamp()
    {
        // No rule covers this one. The wrapper is left exactly as it is, stamp included, so a rule published
        // later still reaches it from the version it failed at rather than from wherever the page has got to.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] original = AffixPayload(MissingId);
        PageSlotInput wrapped = Wrapped(2, Sword, InstanceQuarantineReason.UnknownContentReference, WrapperStamp, original);
        var counted = new List<(int Type, string Reason)>();

        RemapRuleSet unrelated = Rules(Replaced(1, RuleVersion, Type(types, ModKey), 4242, Mod));
        ContainerLoadResult result = ContainerLoad.Load(
            [Page(0, PageStamp, wrapped)],
            snapshot,
            Context(types, Properties(), unrelated, counter: (t, r) => counted.Add((t, r))));

        ItemContainerPage page = result.PageAt(0);
        Assert.True(page.SlotAt(2).Quarantined, Describe(result));
        Assert.Equal(wrapped.Payload.ToArray(), page.SlotAt(2).Payload.ToArray());
        Assert.True(QuarantineWrapper.TryUnwrap(
            page.SlotAt(2).Payload.Span, out ReadOnlySpan<byte> kept, out string? reason, out int stamped));
        Assert.Equal(original, kept.ToArray());
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, reason);
        Assert.Equal(WrapperStamp, stamped);

        // Nothing changed, so nothing is dirty: a failed rescue costs the next commit nothing.
        Assert.False(page.IsDirty);
        Assert.Empty(result.Dirty);

        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryQuarantined));
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, finding.Reason);
        Assert.Equal(WrapperStamp, finding.StampedVersion);
        Assert.Equal(1, result.QuarantinedRecords);
        Assert.Contains((EngineContentTypes.ItemTypeId, InstanceQuarantineReason.UnknownContentReference), counted);
    }

    [Fact]
    public void Check_10_sees_a_live_entry_sharing_an_id_with_a_quarantined_entry()
    {
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);

        ContainerLoadResult result = ContainerLoad.Load(
            [
                Page(
                    0,
                    PageStamp,
                    Wrapped(
                        2,
                        Sword,
                        InstanceQuarantineReason.UnknownContentReference,
                        WrapperStamp,
                        AffixPayload(MissingId)),
                    Slot(3, Sword, instanceId: Instance, payload: AffixPayload())),
            ],
            snapshot,
            Context(types, Properties()));

        InstanceValidationReport report = Assert.Single(result.Reports);
        Assert.True(report.TryGetQuarantine(2, out InstanceValidationFinding stored), Describe(result));
        Assert.Equal(InstanceQuarantineReason.UnknownContentReference, stored.Reason);
        Assert.Equal(WrapperStamp, stored.StampedVersion);

        Assert.True(report.TryGetQuarantine(3, out InstanceValidationFinding duplicate), Describe(result));
        Assert.Equal(10, duplicate.Check);
        Assert.Equal(InstanceQuarantineReason.InstanceIdDuplicate, duplicate.Reason);
        Assert.Equal(2, report.QuarantinedRecords);
        Assert.Equal(2, result.QuarantinedRecords);
    }

    [Fact]
    public void Rescuing_one_entry_does_not_touch_the_others()
    {
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] live = AffixPayload();
        byte[] rescuable = AffixPayload(MissingId);
        PageSlotInput stuck = Wrapped(
            5, Sword, InstanceQuarantineReason.UnknownContentReference, WrapperStamp, AffixPayload(4242), Instance + 2);

        RemapRuleSet rules = Rules(Replaced(1, RuleVersion, Type(types, ModKey), MissingId, Mod));
        ContainerLoadResult result = ContainerLoad.Load(
            [
                Page(
                    0,
                    PageStamp,
                    Slot(0, Sword, payload: live, instanceId: Instance),
                    Wrapped(1, Sword, InstanceQuarantineReason.UnknownContentReference, WrapperStamp, rescuable, Instance + 1),
                    stuck),
            ],
            snapshot,
            Context(types, Properties(), rules));

        ItemContainerPage page = result.PageAt(0);
        Assert.Equal(live, page.SlotAt(0).Payload.ToArray());
        Assert.False(page.SlotAt(0).Quarantined);
        Assert.Equal(AffixPayload(Mod), page.SlotAt(1).Payload.ToArray());
        Assert.False(page.SlotAt(1).Quarantined);
        Assert.Equal(stuck.Payload.ToArray(), page.SlotAt(5).Payload.ToArray());
        Assert.True(page.SlotAt(5).Quarantined);

        Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryRescued));
        Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryQuarantined));
    }

    [Fact]
    public void A_STRUCTURAL_quarantine_is_not_offered_to_the_rules_at_all()
    {
        // Issue #929's third question, answered: the unwrap is for the DRIFT reasons only. A structurally
        // broken payload cannot be helped by a remap rule, re-decoding it on every load costs something, and
        // the page's own door would refuse to seat bytes that are not canonical anyway. So the two drift
        // reasons are offered to the rules and the other eleven are left wrapped.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        PageSlotInput wrapped = Wrapped(
            0, Sword, InstancePayloadReason.FieldTruncated, WrapperStamp, [0x80, 0x01, 0x05]);

        RemapRuleSet rules = Rules(Replaced(1, RuleVersion, Type(types, ModKey), MissingId, Mod));
        ContainerLoadResult result = ContainerLoad.Load(
            [Page(0, PageStamp, wrapped)], snapshot, Context(types, Properties(), rules));

        Assert.True(result.PageAt(0).SlotAt(0).Quarantined);
        Assert.Equal(wrapped.Payload.ToArray(), result.PageAt(0).SlotAt(0).Payload.ToArray());
        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryQuarantined));
        Assert.Equal(InstancePayloadReason.FieldTruncated, finding.Reason);
    }

    [Fact]
    public void A_quarantined_PLAIN_STACK_is_reported_rather_than_wrapped()
    {
        // The one entry a page cannot hold in wrapped form. A wrapper IS a payload, and spec 4.7 invariant 3
        // refuses a payload on a slot with instance id 0, so a plain stack whose definition id no longer
        // resolves cannot be given one. The finding is the record of it and the stored bytes are untouched,
        // because a load time quarantine never dirties the page. Filed as
        // https://github.com/APKiwiOrg/KhaozEngine/issues/935.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        var counted = new List<(int Type, string Reason)>();

        ContainerLoadResult result = ContainerLoad.Load(
            [Page(0, ActiveVersion, Slot(0, MissingId, count: 4))],
            snapshot,
            Context(types, Properties(), counter: (t, r) => counted.Add((t, r))));

        ItemContainerPage page = result.PageAt(0);
        Assert.False(page.SlotAt(0).Quarantined);
        Assert.Equal(MissingId, page.SlotAt(0).Stack.ItemId);
        Assert.False(page.IsDirty);

        // The report is what says it is quarantined, and it is counted like any other quarantined record.
        Assert.True(result.Reports[0].TryGetQuarantine(0, out InstanceValidationFinding finding), Describe(result));
        Assert.Equal(InstanceQuarantineReason.UnknownDefinition, finding.Reason);
        Assert.Contains((EngineContentTypes.ItemTypeId, InstanceQuarantineReason.UnknownDefinition), counted);
        Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryUnwrappable));
    }

    [Fact]
    public void An_entry_carrying_a_payload_with_NO_instance_id_quarantines_the_page_as_a_unit()
    {
        // The same invariant seen from the other side: this entry cannot be seated at all, wrapped or not, so
        // there is no shape of the page that holds it. Failing the page whole keeps the bytes, because
        // nothing that did not load is ever written back.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        JournalProjectionSection section = Page(
            0, ActiveVersion, Slot(0, Sword, payload: AffixPayload(), instanceId: 0), Slot(1, Sword));

        ContainerLoadResult result = ContainerLoad.Load([section], snapshot, Context(types, Properties()));

        Assert.Empty(result.Pages);
        ContainerLoadFinding finding = Assert.Single(result.OfKind(ContainerLoadFindingKind.PageQuarantined));
        Assert.Equal(InstanceQuarantineReason.InstanceIdMissing, finding.Reason);
        Assert.Equal(0, finding.Slot);
    }

    [Fact]
    public void A_wrapper_the_store_CORRUPTED_is_re_wrapped_rather_than_read()
    {
        // A quarantined entry whose bytes are not a wrapper at all cannot be unwrapped and must not be read
        // as a payload either. It is wrapped verbatim under field-malformed, which is a wrapper the page can
        // hold and which keeps the corrupt bytes for whoever comes looking.
        ContentTypeRegistry types = Types();
        ContentSnapshot snapshot = Snapshot(types);
        byte[] corrupt = [0x4B, 0x45, 0x43, 0x51, 0x09, 0x00, 0x09, 0x01, 0x02];

        ContainerLoadResult result = ContainerLoad.Load(
            [
                Page(
                    0,
                    ActiveVersion,
                    new PageSlotInput(0, ItemContainerPageCodec.EntryFlagQuarantined, Sword, 1, Instance, corrupt)),
            ],
            snapshot,
            Context(types, Properties()));

        ItemContainerPage page = result.PageAt(0);
        Assert.True(page.SlotAt(0).Quarantined);
        Assert.True(QuarantineWrapper.TryUnwrap(
            page.SlotAt(0).Payload.Span, out ReadOnlySpan<byte> kept, out string? reason, out _));
        Assert.Equal(corrupt, kept.ToArray());
        Assert.Equal(InstancePayloadReason.FieldMalformed, reason);
        Assert.Single(result.OfKind(ContainerLoadFindingKind.EntryQuarantined));
    }
}
