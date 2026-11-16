using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoveragePackedParityTests
{
    [Theory]
    [InlineData("panel")]
    [InlineData("duplicate")]
    [InlineData("reversed")]
    [InlineData("greedy")]
    public void Packed_and_inline_builds_have_byte_identical_kert_and_pair_decisions(string variant)
    {
        using var files = CoverageCampaignFixture.Files();
        if (variant == "duplicate")
        {
            var duplicate = files.Manifest["samples"]![0]!.DeepClone();
            duplicate["id"] = "c";
            files.Manifest["samples"]!.AsArray().Add(duplicate);
            files.Manifest["candidateSampleIds"]!.AsArray().Add("c");
        }
        if (variant == "reversed") files.Manifest["candidateSampleIds"] = new JsonArray("b", "a");
        if (variant == "greedy")
        {
            var matrix = CoverageBuildFiles.Matrix(System.Numerics.Matrix4x4.CreateScale(1, 1, 1.1f));
            files.Manifest["samples"]![1]!["commands"]![0]!["equipmentWorld"] = matrix.DeepClone();
            files.Manifest["samples"]![1]!["commands"]![0]!["coverageWorld"] = matrix.DeepClone();
        }
        var inline = CoverageCampaignFixture.Inline(files);
        var expected = CoverageTableBuilder.Build(inline);
        CoverageCampaignFixture.Pack(files);
        using var session = CoverageInputSession.Open(files.ManifestPath, files.Folder);
        var actual = CoverageTableBuilder.Build(session.Campaign);
        SameBuild(expected, actual);
        Assert.NotEqual(expected.Evidence.InputSha256, actual.Evidence.InputSha256);
        Assert.Equal(variant == "duplicate" ? 6 : 4, actual.Evidence.PairResults.Count);
        Assert.Equal(variant == "greedy" ? 1 : 2, actual.Table.EntryCount);
        Assert.Empty(actual.Evidence.Holes);
        Assert.All(actual.Evidence.SelectedClaims, c => Assert.True(c.Valid));
        SameEvidence(CoverageSampleVerifier.Verify(inline, expected.Table), CoverageSampleVerifier.Verify(session.Campaign, actual.Table));
        Assert.Equal(CoverageSampleVerifier.Pairs(inline, expected.Table), CoverageSampleVerifier.Pairs(session.Campaign, actual.Table));
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("hole")]
    [InlineData("penetration")]
    [InlineData("blend")]
    [InlineData("loop")]
    [InlineData("clamp")]
    [InlineData("policy")]
    [InlineData("phase")]
    [InlineData("budget")]
    public void Packed_and_inline_refusals_and_dense_outcomes_match(string variant)
    {
        using var files = CoverageCampaignFixture.Files();
        var rows = files.Manifest["domains"]![0]!["denseRows"]!;
        if (variant == "hole") files.Manifest["samples"]![1]!["stateKey"] = 1;
        if (variant == "penetration" || variant == "budget") rows[2]!["sample"]!["commands"]![0]!["transitionWeight"] = .25f;
        if (variant == "blend")
        {
            rows[3]!["sample"] = rows[4]!["sample"]!.DeepClone();
            rows[3]!["resolvedStateKey"] = 2;
        }
        if (variant == "loop" || variant == "clamp" || variant == "policy")
        {
            rows[4]!["precedingPhaseTick"] = 0;
            rows[4]!["looped"] = variant != "clamp";
            rows[4]!["clamped"] = variant != "loop";
        }
        if (variant == "phase") rows[2]!["precedingPhaseTick"] = 1;
        var inline = CoverageCampaignFixture.Inline(files);
        var expected = CoverageTableBuilder.Build(inline);
        CoverageCampaignFixture.Pack(files);
        using var session = CoverageInputSession.Open(files.ManifestPath, files.Folder);
        var actual = CoverageTableBuilder.Build(session.Campaign);
        SameBuild(expected, actual);
        if (variant == "policy" || variant == "phase")
        {
            SameRefusal(Assert.Throws<CoverageVerificationException>(() => CoverageReplay.Verify(inline, expected.Table)),
                Assert.Throws<CoverageVerificationException>(() => CoverageReplay.Verify(session.Campaign, actual.Table)));
            return;
        }
        int budget = variant == "budget" ? 1 : CoverageWholeSkinDepth.DefaultQueryBudget;
        var left = CoverageReplay.Verify(inline, expected.Table, budget);
        var right = CoverageReplay.Verify(session.Campaign, actual.Table, budget);
        Assert.Equal(5, right.PoseCount);
        expected.Evidence.IncludeReplay(left); actual.Evidence.IncludeReplay(right);
        SameEvidence(expected.Evidence, actual.Evidence);
        for (int i = 0; i < left.PoseCount; i++)
        {
            Assert.Equal(CoveragePackedFixture.SampleBytes(left.Poses[i].Input.Sample), CoveragePackedFixture.SampleBytes(right.Poses[i].Input.Sample));
            Assert.Equal(left.Poses[i].Equipment, right.Poses[i].Equipment);
        }
        if (variant is "hole" or "penetration" or "budget")
        {
            Assert.False(right.Accepted);
            var refusal = Assert.Throws<CoverageVerificationException>(right.EnsureAccepted);
            SameRefusal(Assert.Throws<CoverageVerificationException>(left.EnsureAccepted), refusal);
            Assert.Equal(variant == "hole" ? CoverageVerificationRefusal.ReplayHole : variant == "budget"
                ? CoverageVerificationRefusal.UnresolvedChangedRecords : CoverageVerificationRefusal.ChangedRecordPenetration, refusal.Reason);
            if (variant == "penetration") { Assert.Equal(23u, refusal.CommandId); Assert.Equal(17ul, refusal.EquipmentInstanceId); }
        }
        else { Assert.True(right.Accepted); right.EnsureAccepted(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Packed_whole_skin_replay_keeps_cross_command_intrusion_and_its_clear_control(bool crossing)
    {
        using var files = CoverageCampaignFixture.Files();
        var fixture = new CoverageCrossCommandFixture();
        fixture.Write(files, crossing);
        var inline = CoverageCampaignFixture.Inline(files);
        var table = CoverageCrossCommandFixture.Table(inline);
        var left = CoverageReplay.Verify(inline, table);
        CoverageCampaignFixture.Pack(files);
        using var session = CoverageInputSession.Open(files.ManifestPath, files.Folder);
        var right = CoverageReplay.Verify(session.Campaign, table);
        Assert.Equal(!crossing, right.Accepted);
        Assert.Equal(left.Poses[0].Equipment, right.Poses[0].Equipment);
        Assert.Equal(left.ChangedRecordObservedMm, right.ChangedRecordObservedMm);
        if (crossing) SameRefusal(Assert.Throws<CoverageVerificationException>(left.EnsureAccepted),
            Assert.Throws<CoverageVerificationException>(right.EnsureAccepted));
        else right.EnsureAccepted();
    }

    static void SameBuild(CoverageBuildResult left, CoverageBuildResult right)
    {
        Assert.Equal(CoverageRecessionTableBinary.Write(left.Table), CoverageRecessionTableBinary.Write(right.Table));
        Assert.Equal(left.Evidence.PairResults, right.Evidence.PairResults);
        Assert.Equal(left.Evidence.Entries, right.Evidence.Entries);
        Assert.Equal(left.Evidence.SelectedClaims, right.Evidence.SelectedClaims);
        SameEvidence(left.Evidence, right.Evidence);
    }
    internal static void SameEvidence(CoverageEvidenceManifest left, CoverageEvidenceManifest right)
    {
        var expected = JsonNode.Parse(left.Write())!.AsObject(); expected.Remove("inputSha256");
        var actual = JsonNode.Parse(right.Write())!.AsObject(); actual.Remove("inputSha256");
        Assert.True(JsonNode.DeepEquals(expected, actual));
    }
    static void SameRefusal(CoverageVerificationException left, CoverageVerificationException right)
    {
        Assert.Equal(left.Reason, right.Reason); Assert.Equal(left.DomainId, right.DomainId);
        Assert.Equal(left.DenseIndex, right.DenseIndex);
        Assert.Equal(left.SampleId, right.SampleId); Assert.Equal(left.StateKey, right.StateKey);
        Assert.Equal(left.EntryIndex, right.EntryIndex); Assert.Equal(left.CommandId, right.CommandId);
        Assert.Equal(left.EquipmentInstanceId, right.EquipmentInstanceId); Assert.Equal(left.Failure, right.Failure);
    }
}

internal static class CoverageCampaignFixture
{
    internal static string ScratchRoot => Path.Combine(Path.GetTempPath(), "khaozengine-coverage-packed-input", "task-3");
    internal static CoverageBuildFiles Files(int jointCount = 2, int sampleCount = 2, int denseCount = 5)
    {
        var files = new CoverageBuildFiles(scratchDirectory: ScratchRoot, jointCount: jointCount);
        for (int i = 2; i < sampleCount; i++)
        {
            var sample = files.Manifest["samples"]![0]!.DeepClone(); sample["id"] = "sample-" + i;
            files.Manifest["samples"]!.AsArray().Add(sample);
        }
        var rows = new JsonArray();
        for (int i = 0; i < denseCount; i++)
        {
            var sample = files.Manifest["samples"]![i == denseCount - 1 ? 1 : 0]!.DeepClone().AsObject(); sample.Remove("stateKey");
            rows.Add(new JsonObject { ["denseIndex"] = i, ["precedingPhaseTick"] = i / 4,
                ["resolvedStateKey"] = i == denseCount - 1 ? 2 : 1, ["sample"] = sample, ["looped"] = false, ["clamped"] = false });
        }
        files.Manifest["domains"]![0]!["denseRows"] = rows; files.Write(); return files;
    }
    internal static CoverageBuildInput Inline(CoverageBuildFiles files)
    {
        string path = Path.Combine(files.Folder, "inline.json");
        File.WriteAllText(path, files.Manifest.ToJsonString()); return CoverageBuildInput.Read(path);
    }
    internal static void Pack(CoverageBuildFiles files, int recordsPerChunk = 3)
    {
        var samples = files.Manifest["samples"]!.AsArray().Concat(files.Manifest["domains"]!.AsArray()
            .SelectMany(d => d!["denseRows"]!.AsArray()).Select(r => r!["sample"])).Select(s => s!.AsObject()).ToArray();
        var descriptors = new JsonArray();
        for (int start = 0; start < samples.Length; start += recordsPerChunk)
        {
            var poses = samples.Skip(start).Take(recordsPerChunk).Select(CoveragePackedFixture.Numeric).ToArray();
            var records = poses.Select(CoveragePackedFixture.RecordBytes).ToArray();
            byte[] chunk = new byte[32 + records.Sum(r => r.Length)];
            uint[] header = [0x5050454b, 1, 32, 0x01020304, (uint)poses[0].Palette.Length, (uint)poses[0].Commands.Length, (uint)records.Length, (uint)records[0].Length];
            for (int i = 0; i < header.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4 * i), header[i]);
            int offset = 32;
            for (int i = 0; i < records.Length; i++)
            {
                records[i].CopyTo(chunk, offset); offset += records[i].Length;
                var sample = samples[start + i]; sample.Remove("actorModel"); sample.Remove("palette"); sample.Remove("commands");
                sample["poseRef"] = new JsonObject { ["chunk"] = descriptors.Count, ["record"] = i };
            }
            var descriptor = files.Save("poses-" + descriptors.Count + ".kepp", chunk);
            descriptor["byteLength"] = chunk.Length; descriptor["recordCount"] = records.Length; descriptors.Add(descriptor);
        }
        files.Manifest["formatVersion"] = 2; files.Manifest["poseChunks"] = descriptors; files.Write();
    }
}
