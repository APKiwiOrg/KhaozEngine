using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Deterministic, bounded diagnostic lists with exact totals even when no item fits.</summary>
internal sealed class CoverageDiagnosticDocument(string command, int exit, CoverageBuildInput input, string? tableSha256, string? refusal)
{
    static readonly string[] Names = ["domains", "samples", "candidates", "candidateEntries", "candidatePairs", "entries", "pairResults",
        "selectedClaims", "holes", "selectedFailures", "replayFailures", "missingDenseWork", "changedRecordDepth", "inheritedDepth", "analysisRefusals", "runRefusals"];
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };
    readonly Dictionary<string, object[]> categories = new(StringComparer.Ordinal);
    internal void Add<T>(string name, IEnumerable<T> values) => categories[name] = values.Cast<object>().ToArray();

    internal byte[] Write(int byteLimit)
    {
        var retained = Names.ToDictionary(n => n, _ => new List<byte[]>(), StringComparer.Ordinal);
        foreach (string name in Names) categories.TryAdd(name, []);
        // Reserve all metadata and exact omitted counts before admitting items. Omitted counts only shrink.
        int remaining = byteLimit - Serialize(retained).Length;
        bool full = false;
        foreach (string name in Names)
        {
            foreach (object item in categories[name])
            {
                if (full) break;
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(item, Options);
                int cost = bytes.Length + (retained[name].Count == 0 ? 0 : 1);
                if (cost > remaining) { full = true; break; }
                retained[name].Add(bytes);
                remaining -= cost;
            }
        }
        return Serialize(retained);
    }

    byte[] Serialize(Dictionary<string, List<byte[]>> retained)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "khaozengine.coverage-diagnostics/1");
            writer.WriteString("scope", "diagnostic-not-acceptance");
            writer.WriteString("command", command);
            writer.WriteString("outcome", exit == 0 ? "accepted" : "refused");
            writer.WriteString("inputSha256", input.InputSha256);
            writer.WritePropertyName("identity"); JsonSerializer.Serialize(writer, input.Identity, Options);
            writer.WriteString("tableSha256", tableSha256);
            writer.WriteString("reason", refusal);
            int findings = categories.GetValueOrDefault("holes", []).Length + categories.GetValueOrDefault("selectedFailures", []).Length
                + categories.GetValueOrDefault("replayFailures", []).Length + categories.GetValueOrDefault("missingDenseWork", []).Length
                + categories.GetValueOrDefault("analysisRefusals", []).Length + categories.GetValueOrDefault("runRefusals", []).Length
                + categories.GetValueOrDefault("candidates", []).OfType<CoverageCandidateDiagnostic>().Count(c => c.Outcome == "refused");
            writer.WriteNumber("findingCount", exit == 0 ? 0 : findings);
            foreach (string name in Names)
            {
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                writer.WriteNumber("total", categories.GetValueOrDefault(name, []).Length);
                writer.WriteNumber("omitted", categories.GetValueOrDefault(name, []).Length - retained[name].Count);
                writer.WriteStartArray("items");
                foreach (byte[] item in retained[name]) writer.WriteRawValue(item, skipInputValidation: true);
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
