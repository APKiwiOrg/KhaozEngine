using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

internal sealed record IssueInput(ReportRecord Report, IReadOnlyList<CapturedSection> Sections, ClipIndex? Clip,
    IReadOnlyList<string> LogTail, ServerSnapshotWire? Snapshot);
