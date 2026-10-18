using System;
using System.Collections.Generic;

namespace KhaozEngine.BugReports;

/// <summary>A server capture awaiting off-thread redaction and posting through <see cref="ServerSnapshotPoster"/>.</summary>
/// <param name="ReportId">The canonical version 7 report id.</param>
/// <param name="Subject">The authenticated account that requested the capture.</param>
/// <param name="CapturedAt">When the simulation captured the sections.</param>
/// <param name="Sections">Raw sections, before redaction and truncation.</param>
public sealed record ServerSnapshot(string ReportId, string Subject, DateTimeOffset CapturedAt, IReadOnlyList<RawSection> Sections);
