using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// The entry level half of the load: the unwrap step spec 5.5 does not have, and step 4's sweep.
/// </summary>
public static partial class ContainerLoad
{
    /// <summary>
    /// Offers every quarantined entry back to the rules, AT THE WRAPPER'S OWN STAMP, and seats the ones that
    /// validate again.
    /// <para>
    /// This is what spec 12.3 promises in as many words: "a quarantined item is VISIBLY broken and
    /// recoverable in full, because the bytes are kept verbatim and the first load after the missing rule
    /// publishes re-validates and restores the item exactly". Spec 5.5's five steps contain no unwrap, and
    /// the page stamp moves whenever any OTHER entry on the page changes, so without this step the rule that
    /// would rescue an entry can stop applying to its page before it ever reaches it
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/929">#929</see>). The wrapper's stamped
    /// version is the fix: it is the version the record failed under, it never moves with the page, and the
    /// pass is run at it.
    /// </para>
    /// <para>
    /// <b>Only the two DRIFT reasons are offered.</b> A record wrapped under a structural reason cannot be
    /// helped by a remap rule, re-decoding one on every load costs something, and its bytes are not canonical
    /// by definition, so the page's own door would refuse to seat them live. A record wrapped under
    /// <c>unknown-definition</c> or <c>unknown-content-reference</c> is the opposite on both counts: content
    /// moved under it, and its payload passed checks 1 to 5 before the drift check fired, so it is seatable
    /// the moment it resolves.
    /// </para>
    /// <para>
    /// <b>A rescue DIRTIES the page and a failed rescue changes nothing.</b> The rescued bytes are new bytes
    /// and the next ordinary commit owes them a write, which is the same lazy rewrite a remap gets. A record
    /// that still fails keeps its wrapper exactly as it stands, stamped version included, so the next rule to
    /// be published still reaches it from where it failed.
    /// </para>
    /// </summary>
    static void Rescue(Loading load, JournalProjectionSection section, ItemContainerPage page)
    {
        int count = page.CopyEntriesTo(load.Slots);
        for (int index = 0; index < count; index++)
        {
            PageSlotInput entry = load.Slots[index];
            if (!entry.Quarantined) continue;

            // Seating verified every wrapper, so this cannot fail. The values it answers are the wrapper's
            // own and are what both branches below report.
            if (!QuarantineWrapper.TryUnwrap(
                entry.Payload.Span, out ReadOnlySpan<byte> original, out string? reason, out int stampedVersion))
            {
                continue;
            }

            bool rescued = Drift(reason) && TryRescue(load, page, entry, original, stampedVersion);
            load.Add(new ContainerLoadFinding(
                rescued ? ContainerLoadFindingKind.EntryRescued : ContainerLoadFindingKind.EntryQuarantined,
                section.SectionName,
                page.PageIndex,
                entry.Slot,
                reason,
                stampedVersion));
        }
    }

    /// <summary>
    /// Step 4, and the wrappers it earns. The sweep is handed every entry, because the validator reads an
    /// already quarantined entry's stored reason and stamp without decoding its <c>KECQ</c> wrapper as a
    /// payload. Keeping the whole page in the span also keeps check 10 container wide.
    /// <para>
    /// <b>The sweep reads a payload ARENA rather than the stored page.</b> It takes one contiguous buffer plus
    /// windows into it, and after the rules have run the page's payloads live in the page's own slots, so the
    /// bytes are gathered into a buffer built for the call. Re-encoding the page through the codec would
    /// answer the same question and can REFUSE (a page at the section cap), which is not an answer a load path
    /// can use.
    /// </para>
    /// </summary>
    static void Sweep(Loading load, JournalProjectionSection section, ItemContainerPage page)
    {
        int count = page.CopyEntriesTo(load.Slots);
        int bytes = 0;
        for (int index = 0; index < count; index++)
        {
            bytes += load.Slots[index].Payload.Length;
        }

        byte[] arena = new byte[bytes];
        int written = 0;
        for (int index = 0; index < count; index++)
        {
            PageSlotInput entry = load.Slots[index];
            entry.Payload.Span.CopyTo(arena.AsSpan(written));
            load.Entries[index] = new PageEntry(
                entry.Slot, entry.Flags, entry.DefinitionId, entry.Count, entry.InstanceId, written, entry.Payload.Length);
            written += entry.Payload.Length;
        }

        var header = new PageHeader(page.PageIndex, page.FirstSlot, page.SlotCount, page.ContentVersion, count);
        InstanceValidationReport report = InstanceValidator.Validate(
            arena, header, load.Entries.AsSpan(0, count), load.Context.Properties, load.Context.Types, load.Snapshot);
        load.Reports.Add(report);
        Quarantine(load, section, page, report);
    }

    /// <summary>
    /// Wraps what the sweep quarantined, which is the caller's half of spec 12.2: the validator is pure, so
    /// the bytes are replaced here. It does NOT dirty the page. Quarantining is neither an operation nor a
    /// remap (spec 5.3), so the stored bytes stay as they are and the same wrapper is derived again on the
    /// next load, which is also what keeps the recovery of 12.3 exact.
    /// </summary>
    static void Quarantine(
        Loading load, JournalProjectionSection section, ItemContainerPage page, InstanceValidationReport report)
    {
        foreach (InstanceValidationFinding finding in report.Findings)
        {
            if (!finding.IsQuarantine || finding.Reason is null) continue;

            ItemSlot slot = page.SlotAt(finding.Slot);
            if (slot.Quarantined) continue;

            // The one record a page cannot hold in wrapped form: a wrapper IS a payload, and spec 4.7
            // invariant 3 refuses a payload on a slot whose instance id is 0, which is every plain stack. The
            // finding is the whole record of it and the stored bytes are untouched (#935).
            if (slot.Payload.IsEmpty || !slot.Stack.HasInstance)
            {
                load.Add(new ContainerLoadFinding(
                    ContainerLoadFindingKind.EntryUnwrappable,
                    section.SectionName,
                    page.PageIndex,
                    finding.Slot,
                    finding.Reason,
                    finding.StampedVersion));
                continue;
            }

            page.Seat(
                finding.Slot,
                new ItemSlot(
                    slot.Stack,
                    QuarantineWrapper.Wrap(finding.Reason, finding.StampedVersion, slot.Payload.Span),
                    Quarantined: true));
        }
    }

    /// <summary>Whether a wrapper's reason is one CONTENT moving could have caused, which is the only kind a
    /// remap rule can answer.</summary>
    static bool Drift(string? reason)
        => reason is InstanceQuarantineReason.UnknownDefinition or InstanceQuarantineReason.UnknownContentReference;

    /// <summary>
    /// One rescue: the original bytes through the rules at the wrapper's stamp, then the validator, then the
    /// page. Everything it allocates is on the rare path, which is the trade the whole step is: a container
    /// with no quarantined entry does none of this.
    /// </summary>
    static bool TryRescue(
        Loading load,
        ItemContainerPage page,
        in PageSlotInput entry,
        ReadOnlySpan<byte> original,
        int stampedVersion)
    {
        // The door would refuse these before the rules ever ran, so they are answered first rather than
        // thrown at further down.
        if (entry.InstanceId == 0
            || original.IsEmpty
            || ItemInstancePayload.Validate(load.Context.Properties, original) is not null)
        {
            return false;
        }

        var scratch = new ItemContainerPage(
            page.PageIndex, load.Context.Stackable, load.Canonical, QuarantineWrapper.Verify);
        scratch.SeatStamp(stampedVersion);
        scratch.Seat(
            entry.Slot,
            new ItemSlot(
                new ItemStack(entry.DefinitionId, entry.Count, entry.InstanceId),
                original.ToArray(),
                Quarantined: false));

        _ = InstanceRemapPass.Apply(
            scratch,
            load.Context.Rules,
            stampedVersion,
            load.Context.Properties,
            load.Context.Types,
            new PageSlotInput[1]);

        ItemSlot rescued = scratch.SlotAt(entry.Slot);
        var check = new PageEntry(
            entry.Slot,
            0,
            rescued.Stack.ItemId,
            rescued.Stack.Count,
            rescued.Stack.InstanceId,
            0,
            rescued.Payload.Length);

        // Re-validated as a whole record rather than trusted because a rule moved: the rescue is the
        // VALIDATOR's answer, so an entry whose row came back with no rule at all comes back too.
        InstanceValidationOutcome outcome = InstanceValidator.ValidateEntry(
            rescued.Payload.Span,
            check,
            scratch.ContentVersion,
            load.Context.Properties,
            load.Context.Types,
            load.Snapshot,
            out _);
        if (outcome == InstanceValidationOutcome.Quarantined) return false;

        return page.ApplyRemap(entry.Slot, rescued, load.Context.Rules.ActiveStamp);
    }
}
