using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// What a load found that a validator report cannot say. Contracts 10.1's three outcomes are about one
/// RECORD read through the validator, and these four are about the page around it: a page that never got as
/// far as a record, an entry whose verdict was already stored, an entry that came back, and a rule that could
/// not be applied.
/// </summary>
public enum ContainerLoadFindingKind : byte
{
    /// <summary>
    /// Spec 5.5 step 1. The page failed as a UNIT and is not in the result at all, because a page that cannot
    /// be parsed has no entries to keep. The stored bytes are untouched: nothing that did not load is ever
    /// written back, so the recovery is the journal's, from the event tail.
    /// </summary>
    PageQuarantined = 0,

    /// <summary>
    /// The entry carries a <c>KECQ</c> wrapper and still does: either it arrived wrapped and no rule rescued
    /// it, or this load wrapped it. The reason and the stamped version are the WRAPPER's, which is what a
    /// later rule reaches it from.
    /// <para>
    /// It is also what an entry flagged quarantined over NO payload answers. There is no wrapper behind the
    /// flag to carry a reason or a stamp, so the reason is <c>field-malformed</c> and the stamp is the
    /// page's, and the entry is left out of the page: nothing can rescue bytes that were never kept, and
    /// seating it live would clear the flag.
    /// </para>
    /// </summary>
    EntryQuarantined = 1,

    /// <summary>
    /// Spec 12.3's promise, kept: the entry arrived wrapped, a rule published since reached it, the record
    /// re-validated, and it is seated live again. The page is dirty, so the next ordinary commit writes the
    /// rescued bytes.
    /// </summary>
    EntryRescued = 2,

    /// <summary>
    /// A rule NAMED this entry and the pass could not apply it: a destination that collides with an id the
    /// same item already carries, or one too wide for the slot that holds it. The entry keeps the bytes it
    /// had. Without this finding it would read downstream as a rule nobody wrote
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/931">#931</see>).
    /// </summary>
    RemapAbandoned = 3,

    /// <summary>
    /// The validator quarantined the record and the page cannot hold the wrapper: a wrapper IS a payload, and
    /// spec 4.7 invariant 3 refuses a payload on a slot whose instance id is 0, which is every plain stack.
    /// The finding is the whole record of it
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/935">#935</see>).
    /// </summary>
    EntryUnwrappable = 4,
}

/// <summary>
/// The reason tokens the LOAD PATH answers with, beside the page tokens of
/// <see cref="ItemContainerPageReason"/> and the quarantine tokens of
/// <see cref="InstanceQuarantineReason"/>.
/// <para>
/// <b>Neither of these has a durable ordinal and neither ever will.</b> A <c>KECQ</c> wrapper's reason byte
/// comes from spec 12.4's table, which assigns a number only to a reason a record is WRAPPED under, and
/// nothing here wraps anything. They are diagnostic tokens: stable across runs so a log line, a counter
/// dimension and a test can all name one.
/// </para>
/// </summary>
public static class ContainerLoadReason
{
    /// <summary>
    /// Spec 13 row 10. The page decoded and its header names a different page than the section it arrived in.
    /// The codec's own <c>FirstSlot</c> check catches a header that disagrees with ITSELF and cannot see a
    /// section name at all, so this is the half that needs the name.
    /// </summary>
    public const string PageSectionMismatch = "page-section-mismatch";

    /// <summary>
    /// A rule named an entry and the pass could not rewrite it. Counted under its own token IN ADDITION to
    /// whatever the validator then says about the entry, deliberately: "a rule could not be applied" and "an
    /// id does not resolve" are different questions and an operator needs both answers.
    /// </summary>
    public const string RemapAbandoned = "remap-abandoned";

    /// <summary>Every load reason, in the order they are declared above.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { PageSectionMismatch, RemapAbandoned };
}

/// <summary>
/// One thing a load found, at the page or the entry level.
/// </summary>
/// <param name="Kind">What was found.</param>
/// <param name="SectionName">The projection section the page arrived in, which is what an operator greps for.</param>
/// <param name="PageIndex">Which page of the container, taken from the SECTION NAME rather than the header,
/// because a page level finding may be a page whose header was never read.</param>
/// <param name="Slot">The absolute container slot, or -1 on a finding about the whole page.</param>
/// <param name="Reason">The reason token: a page reason, a quarantine reason or a load reason.</param>
/// <param name="StampedVersion">The version the record stands at: a wrapper's own stamp where there is one,
/// and the page stamp otherwise.</param>
public readonly record struct ContainerLoadFinding(
    ContainerLoadFindingKind Kind,
    string SectionName,
    int PageIndex,
    int Slot,
    string? Reason,
    int StampedVersion)
{
    /// <summary>The slot value a finding about a whole page carries.</summary>
    public const int NoSlot = -1;

    /// <summary>
    /// Whether this finding is a record OUT OF PLAY, which is what the counter of contracts 10.2 counts. A
    /// rescue is not, and an abandoned entry is counted under its own token rather than as a quarantine.
    /// </summary>
    public bool IsQuarantine => Kind is ContainerLoadFindingKind.PageQuarantined
        or ContainerLoadFindingKind.EntryQuarantined
        or ContainerLoadFindingKind.EntryUnwrappable;
}

/// <summary>
/// What spec 5.5 step 5 returns: the decoded pages, the accumulated findings and the dirty set.
/// <para>
/// The findings come in two shapes and both are here. <see cref="Reports"/> is the validator's own, one per
/// page that decoded, pure and accumulated (contracts 10.4). <see cref="Findings"/> records the load path:
/// a page that failed whole, an entry whose wrapper remained or was rescued, or a rule that could not be
/// applied.
/// </para>
/// <para>
/// <b>The pages are in memory and nothing has been written.</b> A page a rule changed, or a rescue brought an
/// entry back into, is DIRTY and rides the next ordinary commit (contracts 10.3). A page whose entries were
/// quarantined by this load is NOT dirty: quarantining is neither an operation nor a remap, so the stored
/// bytes stay as they are and the same wrapper is derived again on the next load.
/// </para>
/// </summary>
public sealed class ContainerLoadResult
{
    readonly ItemContainerPage[] _pages;
    readonly ItemContainerPage[] _dirty;
    readonly InstanceValidationReport[] _reports;
    readonly ContainerLoadFinding[] _findings;

    internal ContainerLoadResult(
        string container,
        List<ItemContainerPage> pages,
        List<InstanceValidationReport> reports,
        List<ContainerLoadFinding> findings)
    {
        Container = container;
        _pages = pages.ToArray();
        _reports = reports.ToArray();
        _findings = findings.ToArray();

        var dirty = new List<ItemContainerPage>();
        int quarantined = 0;
        foreach (ItemContainerPage page in _pages)
        {
            if (page.IsDirty) dirty.Add(page);
        }

        foreach (InstanceValidationReport report in _reports)
        {
            quarantined += report.QuarantinedRecords;
        }

        foreach (ContainerLoadFinding finding in _findings)
        {
            // A page the validator never swept is counted here. Entry findings are already in a report, so
            // counting either one again would make one broken record look like two.
            if (finding.IsQuarantine
                && finding.Kind is not ContainerLoadFindingKind.EntryQuarantined
                    and not ContainerLoadFindingKind.EntryUnwrappable)
            {
                quarantined++;
            }
        }

        _dirty = dirty.ToArray();
        QuarantinedRecords = quarantined;
    }

    /// <summary>The container these pages belong to, which is the first half of every section name.</summary>
    public string Container { get; }

    /// <summary>Every page that decoded, ascending by page index. A page that failed whole is NOT here.</summary>
    public IReadOnlyList<ItemContainerPage> Pages => _pages;

    /// <summary>One validator sweep per page in <see cref="Pages"/>, in the same order.</summary>
    public IReadOnlyList<InstanceValidationReport> Reports => _reports;

    /// <summary>Everything the sweeps cannot say, in the order it was found.</summary>
    public IReadOnlyList<ContainerLoadFinding> Findings => _findings;

    /// <summary>The pages that owe the next ordinary commit a rewrite.</summary>
    public IReadOnlyList<ItemContainerPage> Dirty => _dirty;

    /// <summary>How many records are out of play, which is how many times the counter was incremented for a
    /// quarantine.</summary>
    public int QuarantinedRecords { get; }

    /// <summary>Whether anything is out of play, which is what makes this load an alert.</summary>
    public bool HasQuarantine => QuarantinedRecords > 0;

    /// <summary>One page by its INDEX rather than its position, because a failed page leaves a hole.</summary>
    /// <param name="pageIndex">Which page of the container.</param>
    /// <param name="page">The page, when it decoded.</param>
    public bool TryGetPage(int pageIndex, out ItemContainerPage? page)
    {
        foreach (ItemContainerPage held in _pages)
        {
            if (held.PageIndex == pageIndex)
            {
                page = held;
                return true;
            }
        }

        page = null;
        return false;
    }
}
