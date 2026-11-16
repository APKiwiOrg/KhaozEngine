using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoveragePackedInputTests
{
    [Theory]
    [InlineData(false, "palette", "$.samples[0].palette")]
    [InlineData(false, "unknown-command", "$.samples[0].commands.id")]
    [InlineData(false, "command-count", "$.samples[0].commands")]
    [InlineData(true, "palette", "$.domains[0].denseRows[0].sample.palette")]
    [InlineData(true, "unknown-command", "$.domains[0].denseRows[0].sample.commands.id")]
    [InlineData(true, "command-count", "$.domains[0].denseRows[0].sample.commands")]
    public void Format_one_binding_refusals_keep_the_predecessor_field_paths(bool dense, string defect, string expectedPath)
    {
        // Field paths are the separate binding branches in the c7423c5fb reader.
        using var files = CoveragePackedFixture.Files();
        var sample = dense ? files.Manifest["domains"]![0]!["denseRows"]![0]!["sample"]! : files.Manifest["samples"]![0]!;
        string accepted = files.Manifest.ToJsonString();
        Assert.Equal(2, CoverageBuildInput.Read(files.ManifestPath).Samples.Count);
        switch (defect)
        {
            case "palette": sample["palette"]!.AsArray().RemoveAt(1); break;
            case "unknown-command": sample["commands"]![0]!["id"] = 99; break;
            case "command-count": sample["commands"] = new JsonArray(); break;
        }
        files.Write();
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath));
        Assert.Equal(CoverageBuildInputRefusal.AssetBinding, error.Reason);
        Assert.Equal(expectedPath, error.Path);
        Assert.Equal("a", error.SampleId);
        Assert.Null(error.ChunkIndex); Assert.Null(error.RecordIndex);
        File.WriteAllText(files.ManifestPath, accepted);
        var control = CoverageBuildInput.Read(files.ManifestPath);
        Assert.Equal(2, control.Samples.Count); Assert.Equal(5, control.Domains[0].DenseRows.Count);
    }

    [Fact]
    public void Packed_and_mixed_manifests_materialize_the_same_input_words()
    {
        using var files = CoveragePackedFixture.Files();
        var inline = CoverageBuildInput.Read(files.ManifestPath);
        var original = files.Manifest.DeepClone().AsObject();
        CoveragePackedFixture.Pack(files);
        Same(inline, CoverageBuildInput.Read(files.ManifestPath));
        CoveragePackedFixture.ReverseRecords(files);
        Same(inline, CoverageBuildInput.Read(files.ManifestPath));
        files.Manifest.Clear();
        foreach (var property in original) files.Manifest.Add(property.Key, property.Value?.DeepClone());
        CoveragePackedFixture.Pack(files, mixed: true);
        Same(inline, CoverageBuildInput.Read(files.ManifestPath));
        files.Manifest.Clear();
        foreach (var property in original) files.Manifest.Add(property.Key, property.Value?.DeepClone());
        files.Manifest["formatVersion"] = 2;
        files.Manifest["poseChunks"] = new JsonArray();
        files.Write();
        Same(inline, CoverageBuildInput.Read(files.ManifestPath));
    }

    internal static void Same(CoverageBuildInput expected, CoverageBuildInput actual)
    {
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.NotEqual(expected.InputSha256, actual.InputSha256);
        Assert.Equal(expected.InverseBind.ToArray(), actual.InverseBind.ToArray());
        Assert.Equal(expected.CandidateSampleIds, actual.CandidateSampleIds);
        Assert.Equal(expected.Samples.Count, actual.Samples.Count);
        Assert.Equal(expected.Domains.Count, actual.Domains.Count);
        void Pose(CoverageSample left, CoverageSample right)
        {
            Assert.Equal(new CoverageSampleInventory(left.Id, left.DomainId, left.AuthoredFrameId, left.BlendPathId, left.StateKey.Value),
                new(right.Id, right.DomainId, right.AuthoredFrameId, right.BlendPathId, right.StateKey.Value));
            Assert.Equal(CoveragePackedFixture.SampleBytes(left), CoveragePackedFixture.SampleBytes(right));
        }
        for (int i = 0; i < expected.Samples.Count; i++) Pose(expected.Samples[i], actual.Samples[i]);
        for (int d = 0; d < expected.Domains.Count; d++)
        {
            var left = expected.Domains[d]; var right = actual.Domains[d];
            Assert.Equal(left.Id, right.Id); Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.AuthoredFrameIds, right.AuthoredFrameIds); Assert.Equal(left.BlendPathIds, right.BlendPathIds);
            Assert.Equal(5, right.DenseRows.Count);
            for (int r = 0; r < left.DenseRows.Count; r++)
            {
                var a = left.DenseRows[r]; var b = right.DenseRows[r];
                Assert.Equal((a.DenseIndex, a.PrecedingPhaseTick, a.ResolvedStateKey, a.Looped, a.Clamped),
                    (b.DenseIndex, b.PrecedingPhaseTick, b.ResolvedStateKey, b.Looped, b.Clamped));
                Pose(a.Sample, b.Sample);
            }
        }
    }

    [Theory]
    [InlineData("missing", CoverageBuildInputRefusal.PackedChunkMissing)]
    [InlineData("short", CoverageBuildInputRefusal.PackedChunkLengthMismatch)]
    [InlineData("long", CoverageBuildInputRefusal.PackedChunkLengthMismatch)]
    [InlineData("declared-length", CoverageBuildInputRefusal.PackedChunkLengthMismatch)]
    [InlineData("hash", CoverageBuildInputRefusal.PackedChunkHashMismatch)]
    [InlineData("altered", CoverageBuildInputRefusal.PackedChunkHashMismatch)]
    [InlineData("header-count", CoverageBuildInputRefusal.PackedCountMismatch)]
    [InlineData("oversized", CoverageBuildInputRefusal.CapacityExceeded)]
    [InlineData("declared-oversized", CoverageBuildInputRefusal.CapacityExceeded)]
    [InlineData("nonfinite", CoverageBuildInputRefusal.PackedWordNonFinite)]
    public void Packed_chunk_admission_refuses_each_integrity_defect_before_candidate_work(string defect, CoverageBuildInputRefusal reason)
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files);
        string chunkPath = Path.Combine(files.Folder, "poses.kepp");
        byte[] accepted = File.ReadAllBytes(chunkPath);
        var validManifest = files.Manifest.DeepClone();
        Assert.Equal(2, CoverageBuildInput.Read(files.ManifestPath).Samples.Count);
        // A later candidate is singular. Transport admission must win before its construction.
        byte[] bytes = accepted.ToArray();
        Array.Clear(bytes, 32 + 336, 64);
        var descriptor = files.Manifest["poseChunks"]![0]!;
        switch (defect)
        {
            case "short": bytes = bytes[..^1]; break;
            case "long": bytes = [.. bytes, 0]; break;
            case "declared-length": descriptor["byteLength"] = bytes.Length + 1; break;
            case "header-count":
                bytes = [.. bytes, .. accepted.AsSpan(32, 336).ToArray()];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 8);
                descriptor["byteLength"] = bytes.Length;
                break;
            case "oversized": Array.Resize(ref bytes, 8_388_609); break;
            case "declared-oversized": descriptor["byteLength"] = 8_388_609; break;
            case "nonfinite": case "altered": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 0x7f800000); break;
        }
        descriptor["sha256"] = defect == "hash" ? new string('b', 64)
            : defect == "altered" ? CoverageBuildInputJson.Hash(accepted) : CoverageBuildInputJson.Hash(bytes);
        File.WriteAllBytes(chunkPath, bytes);
        if (defect == "missing") File.Delete(chunkPath);
        files.Write();
        byte[] manifest = File.ReadAllBytes(files.ManifestPath);
        File.WriteAllBytes(files.TablePath, [1, 3, 5]); File.WriteAllBytes(files.EvidencePath, [2, 4, 6]);
        var error = Assert.Throws<CoverageBuildDataException>(() => CoverageTableBuilder.Build(CoverageBuildInput.Read(files.ManifestPath)));
        Assert.Equal(reason, error.Reason); Assert.Equal(0, error.ChunkIndex);
        Assert.StartsWith("$.poseChunks[0]", error.Path, StringComparison.Ordinal);
        if (defect == "nonfinite") { Assert.Equal(0, error.RecordIndex); Assert.Equal(32, error.ByteOffset); }
        if (defect == "header-count") { Assert.Equal(24, error.ByteOffset); Assert.Equal(8, error.Count); Assert.Equal(7, error.Limit); }
        if (reason == CoverageBuildInputRefusal.PackedChunkHashMismatch)
        { Assert.NotNull(error.ExpectedSha256); Assert.NotEqual(error.ExpectedSha256, error.ActualSha256); }
        if (reason == CoverageBuildInputRefusal.PackedChunkLengthMismatch)
        { Assert.NotNull(error.ExpectedLength); Assert.NotEqual(error.ExpectedLength, error.ActualLength); }
        Assert.Equal(manifest, File.ReadAllBytes(files.ManifestPath));
        if (defect != "missing") Assert.Equal(bytes, File.ReadAllBytes(chunkPath));
        Assert.Equal(new byte[] { 1, 3, 5 }, File.ReadAllBytes(files.TablePath));
        Assert.Equal(new byte[] { 2, 4, 6 }, File.ReadAllBytes(files.EvidencePath));
        File.WriteAllBytes(chunkPath, accepted);
        File.WriteAllText(files.ManifestPath, validManifest.ToJsonString());
        Assert.Equal(2, CoverageTableBuilder.Build(CoverageBuildInput.Read(files.ManifestPath)).Table.EntryCount);
    }

    [Theory]
    [InlineData("duplicate", CoverageBuildInputRefusal.PackedReferenceInvalid)]
    [InlineData("unreferenced", CoverageBuildInputRefusal.PackedReferenceInvalid)]
    [InlineData("unused-descriptor", CoverageBuildInputRefusal.PackedReferenceInvalid)]
    [InlineData("out-of-range", CoverageBuildInputRefusal.PackedReferenceInvalid)]
    [InlineData("missing-dense", CoverageBuildInputRefusal.MissingMember)]
    [InlineData("mixed-fields", CoverageBuildInputRefusal.PackedReferenceInvalid)]
    [InlineData("fractional", CoverageBuildInputRefusal.PackedReferenceInvalid)]
    [InlineData("negative", CoverageBuildInputRefusal.PackedReferenceInvalid)]
    [InlineData("sample-count", CoverageBuildInputRefusal.CapacityExceeded)]
    [InlineData("dense-count", CoverageBuildInputRefusal.CapacityExceeded)]
    [InlineData("pair-count", CoverageBuildInputRefusal.CapacityExceeded)]
    public void Packed_reference_inventory_is_exact_across_ordinary_and_dense_rows(string defect, CoverageBuildInputRefusal reason)
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files, mixed: true);
        string accepted = files.Manifest.ToJsonString();
        var dense = files.Manifest["domains"]![0]!["denseRows"]!.AsArray();
        var reference = dense[4]!["sample"]!["poseRef"]!;
        switch (defect)
        {
            case "duplicate": reference["record"] = 0; break;
            case "unreferenced": dense.RemoveAt(4); break;
            case "unused-descriptor": files.Manifest["poseChunks"]!.AsArray().Add(files.Manifest["poseChunks"]![0]!.DeepClone()); break;
            case "out-of-range": reference["record"] = 6; break;
            case "missing-dense": dense[4]!["sample"]!.AsObject().Remove("poseRef"); break;
            case "mixed-fields": dense[4]!["sample"]!["actorModel"] = CoverageBuildFiles.Matrix(Matrix4x4.Identity); break;
            case "fractional": reference["record"] = .5; break;
            case "negative": reference["chunk"] = -1; break;
            case "sample-count":
                files.Manifest["samples"] = new JsonArray(Enumerable.Range(0, 65_537).Select(_ => files.Manifest["samples"]![0]!.DeepClone()).ToArray()); break;
            case "dense-count":
                var domain = files.Manifest["domains"]![0]!.DeepClone();
                domain["denseRows"] = new JsonArray(Enumerable.Range(0, 65_532).Select(_ => dense[0]!.DeepClone()).ToArray());
                files.Manifest["domains"]!.AsArray().Add(domain); break;
            case "pair-count":
                files.Manifest["samples"] = new JsonArray(Enumerable.Range(0, 1_025).Select(_ => files.Manifest["samples"]![0]!.DeepClone()).ToArray());
                files.Manifest["candidateSampleIds"] = new JsonArray(Enumerable.Range(0, 4_096).Select(i => (JsonNode)JsonValue.Create("c" + i)!).ToArray()); break;
        }
        files.Write();
        Assert.Equal(reason, Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath)).Reason);
        File.WriteAllText(files.ManifestPath, accepted);
        var control = CoverageBuildInput.Read(files.ManifestPath);
        Assert.Equal(new[] { "a", "b" }, control.Samples.Select(s => s.Id));
        Assert.Equal(new[] { "a", "a", "a", "a", "b" }, control.Domains[0].DenseRows.Select(r => r.Sample.Id));
    }

    [Fact]
    public void Packed_reference_and_chunk_admission_precede_source_binding()
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files);
        string accepted = files.Manifest.ToJsonString();
        string hostPath = Path.Combine(files.Folder, "host.glb");
        byte[] host = File.ReadAllBytes(hostPath);
        File.Delete(hostPath);
        files.Manifest["domains"]![0]!["denseRows"]![4]!["sample"]!["poseRef"]!["record"] = 7; files.Write();
        Assert.Equal(CoverageBuildInputRefusal.PackedReferenceInvalid,
            Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath)).Reason);
        File.WriteAllText(files.ManifestPath, accepted);
        files.Manifest["domains"]![0]!["denseRows"]![4]!["sample"]!["poseRef"]!["record"] = 6;
        files.Manifest["poseChunks"]![0]!["sha256"] = new string('b', 64); files.Write();
        Assert.Equal(CoverageBuildInputRefusal.PackedChunkHashMismatch,
            Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath)).Reason);
        File.WriteAllText(files.ManifestPath, accepted);
        Assert.Equal(CoverageBuildInputRefusal.InputIo,
            Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath)).Reason);
        File.WriteAllBytes(hostPath, host);
        Assert.Equal(2, CoverageBuildInput.Read(files.ManifestPath).Samples.Count);
    }

    [Fact]
    public void Packed_input_identity_is_transitive_without_changing_runtime_identity()
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files);
        var first = CoverageBuildInput.Read(files.ManifestPath);
        string chunkPath = Path.Combine(files.Folder, "poses.kepp");
        byte[] bytes = File.ReadAllBytes(chunkPath);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32 + 48), 0x3e800001);
        File.WriteAllBytes(chunkPath, bytes);
        files.Manifest["poseChunks"]![0]!["sha256"] = CoverageBuildInputJson.Hash(bytes); files.Write();
        var changed = CoverageBuildInput.Read(files.ManifestPath);
        Assert.NotEqual(first.InputSha256, changed.InputSha256); Assert.Equal(first.Identity, changed.Identity);
        Assert.NotEqual(CoveragePackedFixture.SampleBytes(first.Samples[0]), CoveragePackedFixture.SampleBytes(changed.Samples[0]));
        files.Configuration["parts"]![0]!["socket"] = CoverageBuildFiles.Matrix(Matrix4x4.CreateTranslation(.25f, 0, 0));
        files.WriteConfiguration();
        Assert.NotEqual(changed.Identity.ConfigurationSha256, CoverageBuildInput.Read(files.ManifestPath).Identity.ConfigurationSha256);
    }

    [Fact]
    public void Format_two_does_not_change_configuration_version_or_host_joint_admission()
    {
        using var files = CoveragePackedFixture.Files(jointCount: 128);
        CoveragePackedFixture.Pack(files);
        Assert.Equal(128, CoverageBuildInput.Read(files.ManifestPath).InverseBind.Length);
        files.Configuration["formatVersion"] = 2; files.WriteConfiguration();
        Assert.Equal(CoverageBuildInputRefusal.UnsupportedVersion,
            Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(files.ManifestPath)).Reason);
        files.Configuration["formatVersion"] = 1; files.WriteConfiguration();
        Assert.Equal(128, CoverageBuildInput.Read(files.ManifestPath).Samples[0].Palette.Length);
        using var oversized = CoveragePackedFixture.Files(jointCount: 129);
        CoveragePackedFixture.Pack(oversized);
        var outer = Assert.Throws<CoverageBuildDataException>(() => CoverageBuildInput.Read(oversized.ManifestPath));
        var error = Assert.IsType<GltfImportStateException>(outer.InnerException);
        Assert.Equal(GltfImportRefusal.SkinJointLimitExceeded, error.Reason); Assert.Equal(129, error.JointCount);
    }
}
