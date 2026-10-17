using System.Text.Json.Serialization;

namespace KhaozEngine.BugReports.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(BugReportManifest))]
[JsonSerializable(typeof(ClipIndex))]
[JsonSerializable(typeof(ReporterIdentity))]
[JsonSerializable(typeof(IntakeAccepted))]
[JsonSerializable(typeof(RelayError))]
[JsonSerializable(typeof(ServerSnapshotWire))]
internal partial class BugReportJsonContext : JsonSerializerContext;
