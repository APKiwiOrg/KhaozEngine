using System;
using System.Collections.Generic;

namespace KhaozEngine.BugReports.Contracts;

public sealed record BundleEntry(string Name, long Size, string Sha256);

public sealed record BugReportManifest(int FormatVersion, string ReportId, string GameId, BugReportKind Kind,
    string Category, string Description, DateTimeOffset CapturedAt, IReadOnlyList<BundleEntry> Entries);
