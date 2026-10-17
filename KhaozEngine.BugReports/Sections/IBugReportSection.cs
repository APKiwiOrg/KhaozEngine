using System.Text.Json;

namespace KhaozEngine.BugReports;

/// <summary>
/// One named block of report state, such as the build, the platform or the recent log. A section writes a single
/// JSON object when a report is captured. It may throw or write too much: <see cref="BugReportSectionSet"/> isolates
/// each section, and every string it writes is redacted before the report leaves the machine.
/// </summary>
public interface IBugReportSection
{
    /// <summary>The bundle key, matching <c>[a-z0-9][a-z0-9._-]{0,47}</c>. The section lands at <c>sections/&lt;key&gt;.json</c>.</summary>
    string Key { get; }

    /// <summary>The most bytes the finished section may take. A larger section is cut structurally to fit.</summary>
    int MaxBytes { get; }

    /// <summary>Writes exactly one JSON object. Runs on the thread that captures the report, so keep it cheap.</summary>
    void Write(Utf8JsonWriter writer, BugReportContext context);
}
