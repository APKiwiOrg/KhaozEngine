using System;
using System.Collections.Generic;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The THREE outcomes of contracts 10.1, and there is no fourth. In particular there is no "dropped": a
/// record that did not resolve is kept, unusable, never discarded.
/// </summary>
public enum InstanceValidationOutcome : byte
{
    /// <summary>Every id resolved, every field parsed, every cross-reference held. Used as read.</summary>
    Valid = 0,

    /// <summary>
    /// A remap rule changed something, so the record is usable and its page is dirty in memory until its
    /// next ordinary commit (contracts 10.3).
    /// <para>
    /// <b>NO phase 1 path produces this.</b> The remap pass is spec 5.5 step 2 and the phase 2-3 plan ships
    /// it. The member is here from the start because contracts 10.1 owns this vocabulary rather than this
    /// package: a report that could not express one of the three would have to be widened later, and a
    /// caller switching over the enum should compile once against the final set.
    /// </para>
    /// </summary>
    Remapped = 1,

    /// <summary>
    /// Something did not parse or did not resolve. The bytes are kept VERBATIM in a
    /// <see cref="QuarantineWrapper"/> and the item is shown as a placeholder: unusable, untradeable and
    /// undroppable, but movable between slots.
    /// </summary>
    Quarantined = 2,
}

/// <summary>
/// The two POLICY reason tokens of spec 12.2, the ones its rows 12 and 13 name.
/// <para>
/// They live apart from <see cref="InstanceQuarantineReason"/> deliberately, because spec 12.4 gives
/// neither of them a durable ordinal: <c>over-cap</c> is tolerated by contract and
/// <c>definition-retired</c> answers the Retired finding, so neither ever writes a wrapper and giving
/// either one a number would invite one to be written.
/// </para>
/// </summary>
public static class InstanceValidationReason
{
    /// <summary>
    /// Spec 12.2 check 12. The count is above the definition's cap with no lowered-cap rule to explain it.
    /// Contracts 8.2 kind 4 makes an over-cap stack LEGAL, may-only-shrink and self healing, so this is a
    /// state the contract declares valid rather than a drift the validator caught.
    /// </summary>
    public const string OverCap = "over-cap";

    /// <summary>
    /// Spec 12.2 check 13. The definition, or a socket's contained definition, is retired in the active
    /// version. Contracts 8.2 kind 2 policy <c>0x01</c> keeps the id as it stands and shows the item
    /// through the same placeholder quarantine uses.
    /// </summary>
    public const string DefinitionRetired = "definition-retired";
}

/// <summary>
/// One thing the sweep found on one entry: which spec 12.2 check answered, the token it answered with, and
/// what that does to the record.
/// <para>
/// A quarantine finding carries everything a caller needs to write the wrapper without re-deriving
/// anything: <see cref="Reason"/> is the token <see cref="InstanceQuarantineReason"/> assigns the durable
/// ordinal to, and <see cref="StampedVersion"/> is the page stamp the record failed under.
/// </para>
/// </summary>
/// <param name="Slot">The container slot, absolute, as <see cref="PageEntry.Slot"/> gives it.</param>
/// <param name="DefinitionId">The entry's definition id, which is the counter's content dimension.</param>
/// <param name="InstanceId">The entry's instance id, 0 for a plain stack.</param>
/// <param name="StampedVersion">The page stamp the record was read under, which the wrapper stores.</param>
/// <param name="Check">The spec 12.2 row number, 1 to 13, or 0 on the empty finding.</param>
/// <param name="Reason">The reason token, null only on the empty finding.</param>
/// <param name="Outcome">What the finding does to the record, contracts 10.1.</param>
public readonly record struct InstanceValidationFinding(
    int Slot,
    int DefinitionId,
    long InstanceId,
    int StampedVersion,
    int Check,
    string? Reason,
    InstanceValidationOutcome Outcome)
{
    /// <summary>Spec 12.2's row for the tolerated over-cap count.</summary>
    public const int OverCapCheck = 12;

    /// <summary>Spec 12.2's row for the retired definition, the check that is not a fourth outcome.</summary>
    public const int RetiredCheck = 13;

    /// <summary>False for the empty finding a clean entry answers with.</summary>
    public bool HasFinding => Check != 0;

    /// <summary>Whether this finding wraps the record's bytes and takes it out of play.</summary>
    public bool IsQuarantine => Outcome == InstanceValidationOutcome.Quarantined;

    /// <summary>
    /// Whether this is check 13's answer: the record stays <see cref="InstanceValidationOutcome.Valid"/>
    /// and what changes is the PRESENTATION (<see cref="InstanceValidationStrings.Retired"/>) and the
    /// refusals that ride with it.
    /// </summary>
    public bool IsRetired => Check == RetiredCheck;

    /// <summary>The durable byte a wrapper stores this reason as, or false for the two policy tokens.</summary>
    /// <param name="ordinal">The ordinal, when the reason has one.</param>
    public bool TryGetQuarantineOrdinal(out byte ordinal)
    {
        if (Reason is not null)
        {
            return InstanceQuarantineReason.TryGetOrdinal(Reason, out ordinal);
        }

        ordinal = InstanceQuarantineReason.ReservedOrdinal;
        return false;
    }
}

/// <summary>
/// What one sweep of a container found, accumulated rather than stopped at the first finding, following
/// <c>JsonSchemaValidator.ValidationReport</c>'s bool-plus-ordered-list shape (contracts 10.4).
/// <para>
/// The report is the ONLY thing the sweep produces. It logs nothing, counts nothing and mutates nothing,
/// which is what keeps <see cref="InstanceValidator"/> testable with no server and no sink anywhere.
/// <see cref="InstanceValidationTelemetry"/> is the one place the side effects happen, and it takes a
/// finished report.
/// </para>
/// </summary>
public sealed class InstanceValidationReport
{
    readonly InstanceValidationFinding[] _findings;
    readonly KeyValuePair<string, int>[] _reasonCounts;

    /// <summary>Takes the finished sweep, which is the only caller.</summary>
    internal InstanceValidationReport(
        int pageIndex,
        int stampedVersion,
        int activeVersion,
        int entryCount,
        List<InstanceValidationFinding> findings)
    {
        PageIndex = pageIndex;
        StampedVersion = stampedVersion;
        ActiveVersion = activeVersion;
        EntryCount = entryCount;
        _findings = findings.ToArray();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int quarantined = 0;
        foreach (InstanceValidationFinding finding in _findings)
        {
            if (!finding.IsQuarantine || finding.Reason is null)
            {
                continue;
            }

            quarantined++;
            counts.TryGetValue(finding.Reason, out int held);
            counts[finding.Reason] = held + 1;
        }

        QuarantinedRecords = quarantined;
        var ordered = new List<KeyValuePair<string, int>>(counts);

        // Descending by count, then ordinally by token, so the line an operator reads and the dominant
        // reason it names are the same on every machine and in every run.
        ordered.Sort(static (left, right) => left.Value == right.Value
            ? string.CompareOrdinal(left.Key, right.Key)
            : right.Value.CompareTo(left.Value));
        _reasonCounts = ordered.ToArray();
    }

    /// <summary>Which page of the container this was, spec 5.2. 0 for a whole container.</summary>
    public int PageIndex { get; }

    /// <summary>The page stamp the entries were read under, contracts 7.2.</summary>
    public int StampedVersion { get; }

    /// <summary>The active content version they were swept against.</summary>
    public int ActiveVersion { get; }

    /// <summary>How many entries the sweep walked.</summary>
    public int EntryCount { get; }

    /// <summary>Every finding, in slot order, with the container-wide ones last.</summary>
    public IReadOnlyList<InstanceValidationFinding> Findings => _findings;

    /// <summary>True when the sweep found NOTHING at all, tolerated findings included.</summary>
    public bool IsValid => _findings.Length == 0;

    /// <summary>How many records were quarantined, which is how many times the counter is incremented.</summary>
    public int QuarantinedRecords { get; }

    /// <summary>Whether anything was quarantined, which is what makes this report an alert.</summary>
    public bool HasQuarantine => QuarantinedRecords > 0;

    /// <summary>
    /// The quarantine reason tokens and their counts, descending by count. The two POLICY tokens are NOT
    /// here: contracts 10.2's counter is dimensioned by reason CODE, and spec 12.4 assigns neither of them
    /// an ordinal, so neither is ever an alert.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, int>> ReasonCounts => _reasonCounts;

    /// <summary>The reason code with the highest count, which is the one the log line names.</summary>
    public string? DominantReason => _reasonCounts.Length == 0 ? null : _reasonCounts[0].Key;

    /// <summary>
    /// What happens to the record in one slot. <see cref="InstanceValidationOutcome.Valid"/> for a slot
    /// with no quarantine finding, INCLUDING one carrying check 12's or check 13's policy finding.
    /// </summary>
    /// <param name="slot">The container slot.</param>
    public InstanceValidationOutcome OutcomeAt(int slot)
        => TryGetQuarantine(slot, out _) ? InstanceValidationOutcome.Quarantined : InstanceValidationOutcome.Valid;

    /// <summary>
    /// Whether the record in one slot is shown through the retired placeholder
    /// (<see cref="InstanceValidationStrings.Retired"/>), which is a presentation rather than an outcome.
    /// </summary>
    /// <param name="slot">The container slot.</param>
    public bool IsRetiredAt(int slot)
    {
        foreach (InstanceValidationFinding finding in _findings)
        {
            if (finding.Slot == slot && finding.IsRetired)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The quarantine finding for one slot, which is what a caller writes a <see cref="QuarantineWrapper"/>
    /// from. At most one exists per slot: the entry sweep stops at its first quarantine, because every
    /// later check would be reading bytes that have already been shown to mean nothing.
    /// </summary>
    /// <param name="slot">The container slot.</param>
    /// <param name="finding">The finding, when that slot quarantined.</param>
    public bool TryGetQuarantine(int slot, out InstanceValidationFinding finding)
    {
        foreach (InstanceValidationFinding held in _findings)
        {
            if (held.Slot == slot && held.IsQuarantine)
            {
                finding = held;
                return true;
            }
        }

        finding = default;
        return false;
    }
}
