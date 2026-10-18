using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.BugReports.Contracts;

public sealed record IntakeAccepted(string ReportId, string Reference);

public sealed record RelayError(string Error);

public sealed record ServerSnapshotWire(int FormatVersion, string ReportId, string Subject, DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, JsonElement> Sections);
