using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// Spec 5.5, the whole of it: a stream's stored projection sections in, the container's decoded pages, the
/// accumulated findings and the dirty set out, in ONE pass.
/// <para>
/// <b>It takes its whole world as arguments</b> (contracts 10.4, 14.4). No store read, no file read, no
/// ambient static: the sections come from a projection read the caller already did, the active content
/// version is a <see cref="IContentSnapshot"/> argument, and everything else rides the
/// <see cref="ContainerLoadContext"/>. That is what makes a load testable with no server, and it is why the
/// dirty set is RETURNED rather than written: the rewrite is lazy and rides the next ordinary commit
/// (contracts 10.3).
/// </para>
/// <para>
/// <b>The order is the whole design and it is spec 5.5's.</b> Decode, then the rules, then the unwrap, then
/// the validator. Rules BEFORE the validator is what gives a drift finding its meaning: it means no rule
/// covered it. The unwrap sits between them because a wrapper carries its OWN stamp (spec 12.4) and the page
/// stamp governs the page's live entries only, so a quarantined entry cannot ride the page wide pass.
/// </para>
/// <para>
/// <b>Nothing here throws for a stored byte.</b> A page that fails is a finding, an entry that fails is a
/// finding, and the exceptions this can raise are all about the ARGUMENTS: a null, or a rule set the publish
/// validator should have refused.
/// </para>
/// </summary>
public static partial class ContainerLoad
{
    /// <summary>
    /// Loads one container out of a stream's projection sections.
    /// </summary>
    /// <param name="sections">Every section the stream holds, as a projection read returns them. The ones
    /// that are not this container's pages are skipped, so a caller hands in the whole read rather than
    /// filtering it first and duplicating the naming rule.</param>
    /// <param name="snapshot">The ACTIVE content version. Nothing is read from a store, a file or an ambient
    /// static inside this call.</param>
    /// <param name="context">The registries, the vetted rule set, the door predicate and the two telemetry
    /// sinks.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ContainerLoadResult Load(
        IReadOnlyList<JournalProjectionSection> sections,
        IContentSnapshot snapshot,
        ContainerLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(context);

        var load = new Loading(context, snapshot);
        for (int index = 0; index < sections.Count; index++)
        {
            JournalProjectionSection section = sections[index];
            if (section is null
                || !string.Equals(section.StreamKey, context.StreamKey, StringComparison.Ordinal)
                || !ContainerSectionNames.IsPageOf(section.SectionName, context.Container, out int pageIndex))
            {
                continue;
            }

            Page(load, section, pageIndex);
        }

        // ONE line per container and one counter increment per record, spec 12.6 over contracts 10.2. The
        // load's own findings ride in beside the sweeps because they are records the sweeps never saw.
        InstanceValidationTelemetry.Report(
            load.Reports, load.Reasons, context.StreamKey, snapshot.VersionNumber, context.Logger, context.Counter);
        return new ContainerLoadResult(context.Container, load.Pages, load.Reports, load.Findings);
    }

    /// <summary>One section, through spec 5.5's five steps.</summary>
    static void Page(Loading load, JournalProjectionSection section, int pageIndex)
    {
        // ONE read of the section's bytes. The property hands back a defensive COPY on every access, which is
        // the journal's own rule about immutable values, so reading it twice would copy the page twice.
        ReadOnlyMemory<byte> data = section.Data;
        ReadOnlySpan<byte> bytes = data.Span;

        // Step 1. A page that fails to decode at the PAGE level is a whole page failure and is quarantined as
        // a unit, because a page that cannot be parsed has no entries to keep.
        if (!ItemContainerPageCodec.TryDecode(
            bytes,
            ItemContainerPageCodec.ContainerPageSlots,
            load.Entries,
            out PageHeader header,
            out int count,
            out string? reason))
        {
            load.PageFailed(section, pageIndex, reason, ContainerLoadFinding.NoSlot, 0);
            return;
        }

        // Spec 13 row 10, the half the codec cannot see. Its own check holds FirstSlot against the header's
        // page index, which catches a header that disagrees with itself. Whether that page is in the section
        // it belongs to is a question only the section NAME can answer.
        if (header.PageIndex != pageIndex)
        {
            load.PageFailed(
                section, pageIndex, ContainerLoadReason.PageSectionMismatch, ContainerLoadFinding.NoSlot, header.ContentVersion);
            return;
        }

        ItemContainerPage? page = Seat(load, section, pageIndex, header, count, bytes);
        if (page is null) return;

        // Steps 2 and 3. Every rule whose IntroducedIn is strictly greater than the page stamp, in sequence
        // order, in one pass. A rule that changed something dirties the page and moves its stamp, and nothing
        // is written.
        InstanceRemapOutcome outcome = InstanceRemapPass.Apply(
            page,
            load.Context.Rules,
            page.ContentVersion,
            load.Context.Properties,
            load.Context.Types,
            load.Slots,
            load.Abandoned);
        load.Abandonments(section, page, outcome);

        // The unwrap step spec 5.5 does not have and spec 12.3 promises (#929), then step 4's sweep and the
        // wrappers it earns.
        Rescue(load, section, page);
        Sweep(load, section, page);
        load.Pages.Add(page);
    }

    /// <summary>
    /// Seats the decoded entries, which is the LOAD door and dirties nothing: the bytes it seats are the
    /// bytes the store already holds.
    /// <para>
    /// It is also where an entry the page cannot hold is answered, because <c>ItemContainer.SetSlotAt</c>
    /// THROWS for a caller bug rather than refusing for a stored byte, and these bytes are stored. Four
    /// shapes are answered rather than thrown: an entry flagged quarantined over NO payload is left out of
    /// the page under its own finding, because there is nothing there to preserve and seating it live would
    /// clear the flag, a payload that is not canonical is wrapped under the reason the decoder gave, a
    /// quarantined payload that is not a well formed wrapper is wrapped verbatim under
    /// <c>field-malformed</c>, and a payload on a slot with no instance id fails the PAGE, because a wrapper
    /// is itself a payload and spec 4.7 invariant 3 refuses that shape whatever the flag says.
    /// </para>
    /// </summary>
    static ItemContainerPage? Seat(
        Loading load,
        JournalProjectionSection section,
        int pageIndex,
        in PageHeader header,
        int count,
        ReadOnlySpan<byte> bytes)
    {
        var page = new ItemContainerPage(
            pageIndex, load.Context.Stackable, load.Canonical, QuarantineWrapper.Verify);
        page.SeatStamp(header.ContentVersion);

        for (int index = 0; index < count; index++)
        {
            PageEntry entry = load.Entries[index];
            var stack = new ItemStack(entry.DefinitionId, entry.Count, entry.InstanceId);
            ReadOnlySpan<byte> payload = bytes.Slice(entry.PayloadStart, entry.PayloadLength);

            // The quarantined flag is tested FIRST, ahead of the empty payload case, because a flag over no
            // bytes fell through it and seated a LIVE non-quarantined stack, clearing the flag and handing
            // a player an item the store never vouched for with no finding anywhere. The decoder refuses
            // that shape now, so this is the second door rather than the first, and it exists for the same
            // reason the remap walk re-checks the nesting depth the decoder already checked.
            if (entry.Quarantined && payload.IsEmpty)
            {
                load.Add(new ContainerLoadFinding(
                    ContainerLoadFindingKind.EntryQuarantined,
                    section.SectionName,
                    pageIndex,
                    entry.Slot,
                    InstancePayloadReason.FieldMalformed,
                    header.ContentVersion));
                continue;
            }

            if (payload.IsEmpty)
            {
                page.Seat(entry.Slot, new ItemSlot(stack, ReadOnlyMemory<byte>.Empty, Quarantined: false));
                continue;
            }

            if (entry.InstanceId == 0)
            {
                load.PageFailed(
                    section,
                    pageIndex,
                    InstanceQuarantineReason.InstanceIdMissing,
                    entry.Slot,
                    header.ContentVersion);
                return null;
            }

            if (entry.Quarantined)
            {
                byte[] wrapper = QuarantineWrapper.Verify(payload)
                    ? payload.ToArray()
                    : QuarantineWrapper.Wrap(InstancePayloadReason.FieldMalformed, header.ContentVersion, payload);
                page.Seat(entry.Slot, new ItemSlot(stack, wrapper, Quarantined: true));
                continue;
            }

            string? refusal = ItemInstancePayload.Validate(load.Context.Properties, payload);
            page.Seat(
                entry.Slot,
                refusal is null
                    ? new ItemSlot(stack, payload.ToArray(), Quarantined: false)
                    : new ItemSlot(
                        stack,
                        QuarantineWrapper.Wrap(refusal, header.ContentVersion, payload),
                        Quarantined: true));
        }

        return page;
    }

    /// <summary>The lists, the buffers and the two registries one load carries, so nothing is allocated per
    /// page that can be allocated per container.</summary>
    sealed class Loading
    {
        public Loading(ContainerLoadContext context, IContentSnapshot snapshot)
        {
            Context = context;
            Snapshot = snapshot;
            Canonical = payload => ItemInstancePayload.IsCanonical(context.Properties, payload);
        }

        public ContainerLoadContext Context { get; }

        public IContentSnapshot Snapshot { get; }

        /// <summary>The door predicate of spec 4.7 invariant 4, built once rather than once per page.</summary>
        public Func<ReadOnlyMemory<byte>, bool> Canonical { get; }

        public PageEntry[] Entries { get; } = new PageEntry[ItemContainerPageCodec.ContainerPageSlots];

        public PageSlotInput[] Slots { get; } = new PageSlotInput[ItemContainerPageCodec.ContainerPageSlots];

        public int[] Abandoned { get; } = new int[ItemContainerPageCodec.ContainerPageSlots];

        public List<ItemContainerPage> Pages { get; } = new();

        public List<InstanceValidationReport> Reports { get; } = new();

        public List<ContainerLoadFinding> Findings { get; } = new();

        /// <summary>One reason token per record the sweeps do not cover, which is what the one log line and
        /// the counter take alongside the reports.</summary>
        public List<string> Reasons { get; } = new();

        /// <summary>A whole page out of play: a finding, a counted record, and no page in the result.</summary>
        public void PageFailed(
            JournalProjectionSection section, int pageIndex, string? reason, int slot, int stampedVersion)
            => Add(
                new ContainerLoadFinding(
                    ContainerLoadFindingKind.PageQuarantined,
                    section.SectionName,
                    pageIndex,
                    slot,
                    reason,
                    stampedVersion));

        /// <summary>The entries a rule named and the pass could not move (#931).</summary>
        public void Abandonments(JournalProjectionSection section, ItemContainerPage page, InstanceRemapOutcome outcome)
        {
            int named = Math.Min(outcome.EntriesAbandoned, Abandoned.Length);
            for (int index = 0; index < named; index++)
            {
                Add(new ContainerLoadFinding(
                    ContainerLoadFindingKind.RemapAbandoned,
                    section.SectionName,
                    page.PageIndex,
                    Abandoned[index],
                    ContainerLoadReason.RemapAbandoned,
                    page.ContentVersion));
            }
        }

        /// <summary>Records one finding and, when it names a record no sweep will, its reason.</summary>
        public void Add(ContainerLoadFinding finding)
        {
            Findings.Add(finding);
            bool counted = finding.Kind != ContainerLoadFindingKind.EntryRescued
                && finding.Kind != ContainerLoadFindingKind.EntryUnwrappable
                && finding.Reason is not null;
            if (counted) Reasons.Add(finding.Reason!);
        }
    }
}
