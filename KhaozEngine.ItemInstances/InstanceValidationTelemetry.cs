using System;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Catalog;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The counter and the log line of spec 12.6 over contracts 10.2, which name both halves and forbid
/// inventing others.
/// <para>
/// <b>This exists as a member rather than as a paragraph in a document</b> because
/// <see cref="InstanceValidator"/> is pure: it logs nothing and counts nothing, so the emission has to live
/// somewhere, and one named place beside it is what stops the load path of the phase 2-3 plan writing a
/// second emitter with its own wording.
/// </para>
/// <para>
/// <b>ONE line per CONTAINER and one counter increment per RECORD.</b> A container that fails wholesale
/// would otherwise emit a hundred identical lines, which is how an operator learns to filter the category
/// out. The counter stays per record because a counter is what a dashboard reads and a log line is what a
/// human reads.
/// </para>
/// <para>
/// <b>Two doors, one emitter.</b> The report-shaped pair is one page's sweep. The list-shaped pair is a whole
/// PAGED container (spec 5.5), and it takes the reasons for records no report covers alongside the reports:
/// a page quarantined as a unit has no report to be in, an entry that arrived already wrapped is not swept
/// again, and an entry whose remap was abandoned is not a validator finding at all. They ride the same
/// histogram deliberately, because an operator reading one line wants the container's whole picture, and an
/// abandoned entry is counted UNDER ITS OWN TOKEN as well as under whatever the validator then says about
/// it: the two answer different questions.
/// </para>
/// </summary>
public static class InstanceValidationTelemetry
{
    /// <summary>The log category contracts 10.2 names, which is what a caller asks the log manager for.</summary>
    public const string LogCategory = "ContentValidation";

    /// <summary>
    /// The counter contracts 10.2 names, dimensioned by content type id and reason code. The engine has NO
    /// counter seam of its own, so the counter arrives here as a delegate rather than as a package-wide
    /// metrics abstraction invented on the way past.
    /// </summary>
    public const string QuarantinedRecordsCounter = "khaoz.content.quarantined_records";

    /// <summary>
    /// Emits both halves for one finished report, and nothing at all when nothing was quarantined: checks
    /// 12 and 13 are the two findings with no reason ordinal, so neither is ever an alert.
    /// </summary>
    /// <param name="report">The finished sweep. Nothing about it is changed here.</param>
    /// <param name="streamKey">The owning stream key, which the line names. Contracts 10.2 forbids the
    /// payload bytes and a raw account id, so the caller hands in the key it files the projection under and
    /// nothing else identifying.</param>
    /// <param name="logger">The logger, obtained under <see cref="LogCategory"/> through
    /// <c>LogManager.GetLogger</c> rather than through the ambient facade. Null emits no line.</param>
    /// <param name="counter">Called once per QUARANTINED record with the content type id and the reason
    /// code, which are contracts 10.2's two dimensions. Null counts nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> or <paramref name="streamKey"/> is
    /// null.</exception>
    public static void Report(
        InstanceValidationReport report,
        string streamKey,
        ILogger? logger,
        Action<int, string>? counter)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(streamKey);

        if (!report.HasQuarantine)
        {
            return;
        }

        if (counter is not null)
        {
            foreach (InstanceValidationFinding finding in report.Findings)
            {
                if (finding.IsQuarantine && finding.Reason is not null)
                {
                    counter(EngineContentTypes.ItemTypeId, finding.Reason);
                }
            }
        }

        logger?.Warn(Line(report, streamKey));
    }

    /// <summary>
    /// Emits both halves ONCE for a whole paged container, and nothing at all when nothing was quarantined.
    /// </summary>
    /// <param name="reports">One finished sweep per page that decoded, in page order.</param>
    /// <param name="loadReasons">One reason token per record the reports do not cover: a page the load path
    /// quarantined as a unit, an entry that arrived quarantined and stayed, an entry whose remap was
    /// abandoned. The same token appears twice when two records answered the same way.</param>
    /// <param name="streamKey">The owning stream key, which the line names and which is the only identifying
    /// thing it carries.</param>
    /// <param name="activeVersion">The active content version, which the line names even when every page
    /// failed to decode and no report carries it.</param>
    /// <param name="logger">The logger, obtained under <see cref="LogCategory"/>. Null emits no line.</param>
    /// <param name="counter">Called once per quarantined record and once per load reason. Null counts
    /// nothing.</param>
    /// <exception cref="ArgumentNullException">An argument other than the two nullable sinks is null.</exception>
    public static void Report(
        IReadOnlyList<InstanceValidationReport> reports,
        IReadOnlyList<string> loadReasons,
        string streamKey,
        int activeVersion,
        ILogger? logger,
        Action<int, string>? counter)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(loadReasons);
        ArgumentNullException.ThrowIfNull(streamKey);

        if (Quarantined(reports) + loadReasons.Count == 0)
        {
            return;
        }

        if (counter is not null)
        {
            foreach (InstanceValidationReport report in reports)
            {
                foreach (InstanceValidationFinding finding in report.Findings)
                {
                    if (finding.IsQuarantine && finding.Reason is not null)
                    {
                        counter(EngineContentTypes.ItemTypeId, finding.Reason);
                    }
                }
            }

            foreach (string reason in loadReasons)
            {
                counter(EngineContentTypes.ItemTypeId, reason);
            }
        }

        logger?.Warn(Line(reports, loadReasons, streamKey, activeVersion));
    }

    /// <summary>
    /// The container line's text: the stream, how many records over how many pages, the reason code with the
    /// highest count, the oldest page stamp in the sweep, the active version and the counts. It NEVER
    /// carries the payload bytes or a raw account id.
    /// </summary>
    /// <param name="reports">One finished sweep per page that decoded.</param>
    /// <param name="loadReasons">One reason token per record the reports do not cover.</param>
    /// <param name="streamKey">The owning stream key.</param>
    /// <param name="activeVersion">The active content version.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string Line(
        IReadOnlyList<InstanceValidationReport> reports,
        IReadOnlyList<string> loadReasons,
        string streamKey,
        int activeVersion)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(loadReasons);
        ArgumentNullException.ThrowIfNull(streamKey);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (InstanceValidationReport report in reports)
        {
            foreach (KeyValuePair<string, int> pair in report.ReasonCounts)
            {
                counts.TryGetValue(pair.Key, out int held);
                counts[pair.Key] = held + pair.Value;
            }
        }

        foreach (string reason in loadReasons)
        {
            counts.TryGetValue(reason, out int held);
            counts[reason] = held + 1;
        }

        var ordered = new List<KeyValuePair<string, int>>(counts);

        // Descending by count, then ordinally by token, so the line an operator reads and the dominant
        // reason it names are the same on every machine and in every run.
        ordered.Sort(static (left, right) => left.Value == right.Value
            ? string.CompareOrdinal(left.Key, right.Key)
            : right.Value.CompareTo(left.Value));

        var text = new StringBuilder();
        text.Append(CultureInvariant($"stream {streamKey}: "));
        text.Append(CultureInvariant(
            $"{Quarantined(reports) + loadReasons.Count} records quarantined across {reports.Count} pages, "));
        text.Append(CultureInvariant($"reason {(ordered.Count == 0 ? null : ordered[0].Key)}, "));
        text.Append(CultureInvariant(
            $"stamped version {OldestStamp(reports)}, active version {activeVersion}, counts"));
        foreach (KeyValuePair<string, int> pair in ordered)
        {
            text.Append(CultureInvariant($" {pair.Key}={pair.Value}"));
        }

        return text.ToString();
    }

    static int Quarantined(IReadOnlyList<InstanceValidationReport> reports)
    {
        int quarantined = 0;
        foreach (InstanceValidationReport report in reports)
        {
            quarantined += report.QuarantinedRecords;
        }

        return quarantined;
    }

    /// <summary>The oldest page stamp the sweep read, which is the version the load path is bringing the
    /// container forward FROM. Zero when every page failed to decode, because a page that did not decode
    /// declares no stamp anyone can trust.</summary>
    static int OldestStamp(IReadOnlyList<InstanceValidationReport> reports)
    {
        int oldest = 0;
        for (int index = 0; index < reports.Count; index++)
        {
            int stamp = reports[index].StampedVersion;
            if (index == 0 || stamp < oldest) oldest = stamp;
        }

        return oldest;
    }

    /// <summary>
    /// The one line's text: the container, the reason code with the highest count, the counts, the stamped
    /// version and the active version. It NEVER carries the payload bytes or a raw account id.
    /// </summary>
    /// <param name="report">The finished sweep.</param>
    /// <param name="streamKey">The owning stream key.</param>
    public static string Line(InstanceValidationReport report, string streamKey)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(streamKey);

        var text = new StringBuilder();
        text.Append(CultureInvariant($"stream {streamKey} page {report.PageIndex}: "));
        text.Append(CultureInvariant($"{report.QuarantinedRecords} of {report.EntryCount} records quarantined, "));
        text.Append(CultureInvariant($"reason {report.DominantReason}, "));
        text.Append(CultureInvariant($"stamped version {report.StampedVersion}, active version {report.ActiveVersion}, counts"));
        foreach (System.Collections.Generic.KeyValuePair<string, int> pair in report.ReasonCounts)
        {
            text.Append(CultureInvariant($" {pair.Key}={pair.Value}"));
        }

        return text.ToString();
    }

    static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);
}
