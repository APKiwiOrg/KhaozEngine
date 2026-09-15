using System;
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
