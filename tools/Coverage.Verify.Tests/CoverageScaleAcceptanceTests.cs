using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>Real byte bounds, not reduced test limits. Each fixture is generated once and is a correctness proof, not
/// a load or timing measurement.</summary>
public sealed class CoverageScaleAcceptanceTests
{
    const int RealBound = 67_108_864;
    static string Scratch
    {
        get
        {
            string path = Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "task-7");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    [Fact]
    public void Ordinary_input_larger_than_the_real_json_bound_builds_and_verifies_when_packed()
    {
        const int ordinary = 8192;
        byte[] reducedTable;
        using (var reduced = CoverageLargeCampaignFixture.Create(Scratch, 6))
        {
            // The reduced control proves exact KERT parity between the two representations of the same words.
            string inline = Path.Combine(reduced.Folder, "inline.json");
            CoverageLargeCampaignFixture.WriteInline(reduced, 6, inline, dense: true);
            Assert.Equal(0, Run("build", inline, reduced.TablePath + ".inline", reduced.EvidencePath + ".inline"));
            Assert.Equal(0, Run("build", reduced.ManifestPath, reduced.TablePath, reduced.EvidencePath));
            reducedTable = File.ReadAllBytes(reduced.TablePath);
            Assert.Equal(File.ReadAllBytes(reduced.TablePath + ".inline"), reducedTable);
        }
        using var files = CoverageLargeCampaignFixture.Create(Scratch, ordinary);
        string ordinaryOnly = Path.Combine(files.Folder, "inline-ordinary.json");
        long inlineBytes = CoverageLargeCampaignFixture.WriteInline(files, ordinary, ordinaryOnly, dense: false);
        Assert.True(inlineBytes > RealBound, $"Ordinary-only inline manifest is {inlineBytes} bytes.");
        Assert.Equal(inlineBytes, new FileInfo(ordinaryOnly).Length);
        var error = new StringWriter();
        string refusedTable = Path.Combine(files.Folder, "inline-table.bin"), refusedEvidence = Path.Combine(files.Folder, "inline-evidence.json");
        Assert.Equal(2, Run("build", ordinaryOnly, refusedTable, refusedEvidence, error));
        Assert.Equal(nameof(CoverageBuildInputRefusal.CapacityExceeded), error.ToString().Split(' ')[0]);
        Assert.False(File.Exists(refusedTable));
        Assert.False(File.Exists(refusedEvidence));
        File.Delete(ordinaryOnly);

        string samplesEvidence = Path.Combine(files.Folder, "samples-evidence.json"), denseEvidence = Path.Combine(files.Folder, "dense-evidence.json");
        Assert.Equal(0, Run("build", files.ManifestPath, files.TablePath, files.EvidencePath));
        // Table identity and selection do not depend on how many samples share a pose.
        Assert.Equal(reducedTable, File.ReadAllBytes(files.TablePath));
        Assert.Equal(0, Run("verify-samples", files.ManifestPath, files.TablePath, samplesEvidence));
        Assert.Equal(0, Run("verify", files.ManifestPath, files.TablePath, denseEvidence));
        foreach (string evidence in new[] { files.EvidencePath, samplesEvidence, denseEvidence })
        {
            var header = Header(evidence);
            Assert.True(header.Complete);
            Assert.Equal(ordinary, header.Totals["samples"]);
            Assert.Equal(ordinary, header.Totals["selectedClaims"]);
            Assert.Equal(0, header.Totals["holes"]);
            Assert.InRange(header.Totals["entries"], 1, 2);
            Assert.Equal(header.Totals["entries"] * ordinary, header.Totals["pairResults"]);
            Assert.Equal(CoverageLargeCampaignFixture.DenseRows, header.Totals["denseRows"]);
            Assert.Equal(evidence == denseEvidence ? CoverageLargeCampaignFixture.DenseRows : 0, header.Totals["replayPoses"]);
        }
    }

    [Fact]
    public void Evidence_larger_than_the_real_artifact_bound_is_complete_or_unpublished()
    {
        using var set = new CoveragePublicationFixture();
        string logical = Path.Combine(set.Scratch, "logical.json");
        // ArtifactRows at the real bound. Already supplied records, so this proves transport and publication only.
        using (var output = File.Create(logical)) CoverageArtifactFixture.WriteEvidence(output, 4096, 64, 256);
        long length = new FileInfo(logical).Length;
        Assert.True(length > RealBound, $"Logical evidence is {length} bytes.");
        string expectedPayload = CoverageArtifactSet.HashFile(logical);
        CoverageArtifactHeader header;
        using (var input = File.OpenRead(logical)) header = CoverageLogicalDocumentReader.Validate(input, CoverageArtifactKind.Evidence).Header;
        Assert.Equal(4096L * 64, header.Totals["pairResults"]);
        string table = Path.Combine(set.Scratch, "old-table");
        File.WriteAllText(table, "old-table");
        using (var staged = CoverageArtifactWriter.Stage(set.Evidence, header, s => { using var source = File.OpenRead(logical); source.CopyTo(s); }, set.Scratch))
        {
            Assert.True(staged.Parts.Count >= 2);
            Assert.All(staged.Parts, p => Assert.InRange(p.ByteLength, 1, RealBound));
            Assert.All(staged.Parts.SkipLast(1), p => Assert.Equal(RealBound, p.ByteLength));
            Assert.Equal(length, staged.Parts.Sum(p => p.ByteLength));
            Assert.InRange(new FileInfo(staged.RootStagePath).Length, 1, RealBound);
            CoveragePublisher.Publish([new(set.Table, table, new FileInfo(table).Length, CoverageArtifactSet.HashFile(table))], [staged], []);
        }
        File.Delete(logical);
        AssertComplete(set, expectedPayload);
        var before = set.Snapshot();
        Assert.Contains(before.Keys, k => k.EndsWith(".part", StringComparison.Ordinal));
        var next = set.Stage("next");
        Assert.Throws<CoverageBuildValidationException>(() => set.Publish(next, new(CoveragePublicationStep.ReplaceEvidence, 0)));
        foreach (var artifact in next.Artifacts) artifact.Dispose();
        // The old root, table and every old part are exact, and the old set is still complete.
        Assert.Equal(before, set.Snapshot());
        AssertComplete(set, expectedPayload);
    }

    static void AssertComplete(CoveragePublicationFixture set, string expectedPayload)
    {
        Assert.Equal("old-table", File.ReadAllText(set.Table));
        using var read = CoverageArtifactReader.Open(set.Evidence, set.Scratch);
        using var payload = read.OpenPayload();
        // Byte equality with the validated logical document recovers every emitted pair, in order.
        Assert.Equal(expectedPayload, Convert.ToHexStringLower(SHA256.HashData(payload)));
    }

    [Fact]
    public void Declared_input_and_output_buffers_do_not_scale_with_raw_pose_or_logical_json_size()
    {
        const int pose = 64 + 64 * 128 + 144;
        var runs = new List<(CoverageInputStorageUsage Input, long LogicalBytes, int OutputBuffer, int Parts)>();
        // A small campaign in one chunk against a larger one in many chunks, staged under one reduced part bound.
        foreach (var (samples, perChunk) in new[] { (12, 64), (48, 3) })
        {
            using var files = CoverageCampaignFixture.Files(jointCount: 128, sampleCount: samples, denseCount: 9);
            CoverageCampaignFixture.Pack(files, perChunk);
            using var session = CoverageInputSession.Open(files.ManifestPath, files.Folder);
            var built = CoverageTableBuilder.Build(session.Campaign);
            built.Evidence.IncludeReplay(CoverageReplay.Verify(session.Campaign, built.Table));
            Assert.True(built.Evidence.Complete);
            using var staged = CoverageArtifactWriter.Stage(files.EvidencePath, built.Evidence.CreateHeader(), built.Evidence.WriteTo, files.Folder, 65_536, 65_536);
            CoverageArtifactFixture.Publish(staged);
            var totals = Header(files.EvidencePath, files.Folder).Totals;
            // Every output record is present, so a bounded buffer is not bought by omission.
            Assert.Equal(samples, totals["samples"]);
            Assert.Equal(samples, totals["selectedClaims"]);
            Assert.Equal(totals["entries"] * samples, totals["pairResults"]);
            Assert.Equal(9, totals["denseRows"]);
            Assert.Equal(9, totals["replayPoses"]);
            runs.Add((session.StorageForTests, staged.Parts.Sum(p => p.ByteLength) + (staged.Parts.Count == 0 ? new FileInfo(staged.RootStagePath).Length : 0),
                staged.MaximumBufferBytesForTests, staged.Parts.Count));
        }
        Assert.True(runs[1].LogicalBytes > runs[0].LogicalBytes);
        Assert.True(runs[1].Input.SnapshotBytes > runs[0].Input.SnapshotBytes);
        Assert.True(runs[1].Parts > 1);
        Assert.Equal(runs[0].Input.MaximumChunkBufferBytes, runs[1].Input.MaximumChunkBufferBytes);
        Assert.All(runs, r => Assert.InRange(r.Input.MaximumChunkBufferBytes, pose, CoverageArtifactSet.BufferBytes));
        // Decoding and binding hold two copies of one pose. Every production read releases its pose before the next.
        Assert.All(runs, r => Assert.InRange(r.Input.MaximumLiveNumericPoseBytes, pose, 2 * pose));
        Assert.All(runs, r => Assert.InRange(r.OutputBuffer, 1, CoverageArtifactSet.BufferBytes));

        // Positive control: a consumer that keeps every decoded pose it is handed is counted and exceeds the budget.
        using var retained = CoverageCampaignFixture.Files(jointCount: 128, sampleCount: 12, denseCount: 9);
        CoverageCampaignFixture.Pack(retained);
        using var held = CoverageInputSession.Open(retained.ManifestPath, retained.Folder);
        var leases = Enumerable.Range(0, 3).Select(held.Campaign.ReadSample).ToList();
        // Each of the three held poses is counted, so the high-water mark passes the two pose budget.
        Assert.True(held.StorageForTests.MaximumLiveNumericPoseBytes >= 3 * pose);
        foreach (var lease in leases) lease.Dispose();
    }

    static CoverageArtifactHeader Header(string root, string? scratch = null)
    {
        using var read = CoverageArtifactReader.Open(root, scratch ?? Scratch);
        using var payload = read.OpenPayload();
        return CoverageLogicalDocumentReader.Validate(payload, CoverageArtifactKind.Evidence).Header;
    }

    static int Run(string command, string manifest, string table, string evidence, TextWriter? error = null) =>
        Program.RunForTests([command, "--manifest", manifest, "--table", table, "--evidence", evidence],
            RealBound, RealBound, Scratch, new() { Error = error });
}
