using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>FaultSet: one output folder holding a table, a diagnostic set and an evidence set, staged from
/// synthetic logical documents. Snapshots map every output file to its exact hash.</summary>
internal sealed class CoveragePublicationFixture : IDisposable
{
    internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input",
        "publication-" + Guid.NewGuid().ToString("N"));
    internal string Output => Path.Combine(Folder, "out");
    internal string Scratch => Path.Combine(Folder, "scratch");
    internal string Table => Path.Combine(Output, "table.bin");
    internal string Diagnostics => Path.Combine(Output, "diagnostics.json");
    internal string Evidence => Path.Combine(Output, "evidence.json");
    readonly List<CoverageStagedArtifact> owned = [];
    // Links made by a test are spellings of the folder, never content of the set.
    static readonly EnumerationOptions Walk = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

    internal CoveragePublicationFixture()
    {
        Directory.CreateDirectory(Folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Output);
        Directory.CreateDirectory(Scratch);
    }

    /// <summary>Evidence payloads depend only on the sample count, so two stages with one count share a
    /// generation directory. Diagnostic payloads depend on the tag.</summary>
    internal Staged Stage(string tag, int evidenceSamples = 60, int partBytes = 2048)
    {
        string table = Path.Combine(Scratch, tag + "-table-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(table, tag + "-table");
        var file = new CoverageStagedFile(Table, table, new FileInfo(table).Length, CoverageArtifactSet.HashFile(table));
        var diagnostics = Artifact(Diagnostics, CoverageArtifactKind.Diagnostics, s => WriteDiagnostics(s, tag), partBytes);
        var evidence = Artifact(Evidence, CoverageArtifactKind.Evidence,
            s => CoverageArtifactFixture.WriteEvidence(s, evidenceSamples, 2, 24), partBytes);
        return new([file], [diagnostics, evidence]);
    }

    internal void Publish(Staged staged, CoveragePublicationFault? fault = null, IReadOnlyList<string>? protectedInputs = null)
    {
        if (fault is { } value) CoveragePublisher.PublishForTests(staged.Files, staged.Artifacts, protectedInputs ?? [], value);
        else CoveragePublisher.Publish(staged.Files, staged.Artifacts, protectedInputs ?? []);
    }

    internal Dictionary<string, string> Snapshot() => Directory.GetFiles(Output, "*", Walk)
        .ToDictionary(p => Path.GetRelativePath(Output, p).Replace('\\', '/'), CoverageArtifactSet.HashFile);

    internal IEnumerable<string> Directories() => Directory.GetDirectories(Output, "*", Walk)
        .Select(p => Path.GetRelativePath(Output, p).Replace('\\', '/')).Order(StringComparer.Ordinal);

    /// <summary>Proves the complete closure of a published set through the public reader.</summary>
    internal void AssertPublished(Staged staged)
    {
        Assert.Equal(staged.Files[0].Sha256, CoverageArtifactSet.HashFile(Table));
        foreach (var artifact in staged.Artifacts)
        {
            using var read = CoverageArtifactReader.Open(artifact.RootDestinationPath, Scratch);
            Assert.Equal(artifact.RootSha256, read.RootSha256);
        }
    }

    internal static bool IsRecoveryFile(string relative) => relative.EndsWith(CoveragePublicationJournal.JournalSuffix, StringComparison.Ordinal)
        || relative.EndsWith(CoveragePublicationJournal.MarkerSuffix, StringComparison.Ordinal)
        || relative.EndsWith(".bak", StringComparison.Ordinal) || relative.EndsWith(".tmp", StringComparison.Ordinal);

    CoverageStagedArtifact Artifact(string root, CoverageArtifactKind kind, Action<Stream> write, int partBytes)
    {
        byte[] bytes;
        using (var buffer = new MemoryStream()) { write(buffer); bytes = buffer.ToArray(); }
        CoverageArtifactHeader header;
        using (var check = new MemoryStream(bytes)) header = CoverageLogicalDocumentReader.Validate(check, kind).Header;
        var staged = CoverageArtifactWriter.Stage(root, header, s => s.Write(bytes), Scratch, 16384, partBytes);
        owned.Add(staged);
        Assert.NotEmpty(staged.Parts);
        return staged;
    }

    /// <summary>A complete synthetic diagnostic logical document. Only the tag changes its items.</summary>
    static void WriteDiagnostics(Stream destination, string tag)
    {
        using var writer = new Utf8JsonWriter(destination);
        string hash = CoverageBuildFixture.Hash;
        writer.WriteStartObject();
        writer.WriteString("schema", "khaozengine.coverage-diagnostics/2");
        writer.WriteString("scope", "diagnostic-not-acceptance");
        writer.WriteString("command", "build"); writer.WriteString("outcome", "refused");
        writer.WriteString("inputSha256", hash);
        writer.WriteStartObject("identity");
        foreach (string name in new[] { "hostSha256", "hostMapSha256", "configurationSha256", "refinementSha256", "arithmeticSha256", "stateSchemaSha256" })
            writer.WriteString(name, hash);
        writer.WriteEndObject();
        writer.WriteNull("tableSha256"); writer.WriteString("reason", "SampleHole"); writer.WriteNumber("findingCount", 1024);
        writer.WriteString("toolCodeSha256", hash); writer.WriteString("render3DCodeSha256", hash); writer.WriteNull("resumedEvidenceSha256");
        foreach (string category in new[] { "domains", "samples", "candidates", "candidateEntries", "candidatePairs", "entries", "pairResults",
            "selectedClaims", "holes", "selectedFailures", "replayFailures", "missingDenseWork", "changedRecordDepth", "inheritedDepth",
            "analysisRefusals", "runRefusals" })
        {
            int count = category == "pairResults" ? 1024 : 0;
            writer.WriteStartObject(category);
            writer.WriteNumber("total", count); writer.WriteNumber("omitted", 0);
            writer.WriteStartArray("items");
            for (int i = 0; i < count; i++) writer.WriteStringValue(tag + "-finding-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    public void Dispose()
    {
        foreach (var staged in owned) staged.Dispose();
        Directory.Delete(Folder, true);
    }

    internal sealed record Staged(CoverageStagedFile[] Files, CoverageStagedArtifact[] Artifacts)
    {
        internal CoverageStagedArtifact DiagnosticSet => Artifacts[0];
        internal CoverageStagedArtifact EvidenceSet => Artifacts[1];
    }
}
