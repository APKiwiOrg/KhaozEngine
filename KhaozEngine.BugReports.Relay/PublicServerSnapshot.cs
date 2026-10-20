using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using KhaozEngine.BugReports.Contracts;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// Writes the anonymous public form of a validated server snapshot. It keeps the report id, capture time and
/// sections, and drops the authenticated subject, which stays in the private pending area.
/// </summary>
internal static class PublicServerSnapshot
{
    public static byte[] Serialize(ServerSnapshotWire snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = BugReportJson.Options.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteString("reportId", snapshot.ReportId);
            writer.WritePropertyName("capturedAt");
            JsonSerializer.Serialize(writer, snapshot.CapturedAt, BugReportJson.Options);
            writer.WriteStartObject("sections");
            foreach (KeyValuePair<string, JsonElement> section in snapshot.Sections)
            {
                writer.WritePropertyName(section.Key);
                section.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }
}
