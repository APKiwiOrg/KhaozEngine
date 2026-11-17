using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Serializes the complete logical diagnostic document, schema 2, one item at a time. Every category is
/// written in full, so each <c>total</c> equals its item count and <c>omitted</c> is always zero. A document larger
/// than the artifact bound becomes a hashed <c>khaozengine.coverage-diagnostics-set/1</c> through the common
/// artifact writer, never a truncated prefix.</summary>
internal static class CoverageDiagnosticWriter
{
    internal const string Schema = "khaozengine.coverage-diagnostics/2", Scope = "diagnostic-not-acceptance";
    /// <summary>Category order is part of the schema.</summary>
    internal static readonly string[] Categories = ["domains", "samples", "candidates", "candidateEntries", "candidatePairs", "entries",
        "pairResults", "selectedClaims", "holes", "selectedFailures", "replayFailures", "missingDenseWork", "changedRecordDepth",
        "inheritedDepth", "analysisRefusals", "runRefusals"];
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal static CoverageArtifactHeader Header(CoverageDiagnosticDocument document) => new(CoverageArtifactKind.Diagnostics, Schema,
        Scope, document.InputSha256, document.TableSha256, document.Identity, document.ToolCodeSha256, document.Render3DCodeSha256,
        Categories.ToDictionary(name => name, name => (long)document.Category(name).Count, StringComparer.Ordinal), true,
        document.ResumedEvidenceSha256, document.Command, document.Outcome);

    internal static void Write(Stream destination, CoverageDiagnosticDocument document)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteString("scope", Scope);
        writer.WriteString("command", document.Command);
        writer.WriteString("outcome", document.Outcome);
        writer.WriteString("inputSha256", document.InputSha256);
        writer.WritePropertyName("identity"); JsonSerializer.Serialize(writer, document.Identity, Options);
        writer.WriteString("tableSha256", document.TableSha256);
        writer.WriteString("reason", document.Refusal);
        writer.WriteNumber("findingCount", document.FindingCount);
        writer.WriteString("toolCodeSha256", document.ToolCodeSha256);
        writer.WriteString("render3DCodeSha256", document.Render3DCodeSha256);
        writer.WriteString("resumedEvidenceSha256", document.ResumedEvidenceSha256);
        foreach (string name in Categories)
        {
            var items = document.Category(name);
            writer.WritePropertyName(name);
            writer.WriteStartObject();
            writer.WriteNumber("total", items.Count);
            writer.WriteNumber("omitted", 0);
            writer.WriteStartArray("items");
            foreach (object? item in items)
            {
                JsonSerializer.Serialize(writer, item, Options);
                // The writer buffers until flushed, so a long category is drained in bounded steps.
                if (writer.BytesPending >= CoverageArtifactSet.BufferBytes) writer.Flush();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    /// <summary>Stages the complete document. A document within <paramref name="artifactByteLimit"/> stays one file.
    /// A larger one is split into parts of <paramref name="partByteLimit"/> bytes behind a bounded index.</summary>
    internal static CoverageStagedArtifact Stage(string destinationPath, CoverageDiagnosticDocument document, string scratchDirectory,
        int artifactByteLimit, int partByteLimit) =>
        CoverageArtifactWriter.Stage(destinationPath, Header(document), s => Write(s, document), scratchDirectory, artifactByteLimit, partByteLimit);
}

/// <summary>A category's exact count with lazy items, so a projection over a resident inventory is never copied.</summary>
internal sealed class CoverageDiagnosticCategory(int count, IEnumerable items) : IEnumerable
{
    internal static readonly CoverageDiagnosticCategory Empty = new(0, Array.Empty<object>());
    internal int Count { get; } = count;
    public IEnumerator GetEnumerator() => items.GetEnumerator();
    internal static CoverageDiagnosticCategory Of<T>(IReadOnlyCollection<T> items) => new(items.Count, items);
    internal static CoverageDiagnosticCategory Project<TSource, T>(IReadOnlyCollection<TSource> source, Func<TSource, T> projection) =>
        new(source.Count, source.Select(projection));
}
