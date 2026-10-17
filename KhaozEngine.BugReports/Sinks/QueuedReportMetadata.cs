using System;
using System.Text.Json.Serialization;

namespace KhaozEngine.BugReports;

/// <summary>
/// The <c>&lt;reportId&gt;.json</c> file beside a queued bundle. Its presence marks a complete queue entry.
/// </summary>
/// <param name="ReportId">The bundle's report id, which also names both files.</param>
/// <param name="Subject">The account the report belongs to, or null until the first account flushes.</param>
/// <param name="QueuedAt">When the bundle was queued, from the queue's clock. Expiry and eviction order read it.</param>
/// <param name="Bytes">The bundle's length. The queue's byte budget reads it.</param>
internal sealed record QueuedReportMetadata(string ReportId, string? Subject, DateTimeOffset QueuedAt, long Bytes);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(QueuedReportMetadata))]
internal sealed partial class QueuedReportJsonContext : JsonSerializerContext;
