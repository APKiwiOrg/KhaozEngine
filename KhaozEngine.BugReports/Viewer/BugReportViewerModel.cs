using System;
using System.Collections.Generic;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports;

/// <summary>Everything <see cref="BugReportViewerPage.Render"/> shows for one report.</summary>
/// <param name="Manifest">The validated bundle manifest. Its entries decide which media the page links.</param>
/// <param name="Clip">The bundle's clip index, or null when the report has no clip.</param>
/// <param name="Sections">The report's sections. Each one is rewritten token by token, and one that cannot be read shows as an error.</param>
/// <param name="Reporter">Who filed the report, when known. A local save has no reporter.</param>
/// <param name="ServerSnapshotJson">The server snapshot document as received, or null when there is none.</param>
/// <param name="LogTail">The last log lines to show, oldest first.</param>
public sealed record BugReportViewerModel(BugReportManifest Manifest, ClipIndex? Clip, IReadOnlyList<CapturedSection> Sections,
    ReporterIdentity? Reporter, ReadOnlyMemory<byte>? ServerSnapshotJson, IReadOnlyList<string> LogTail);
