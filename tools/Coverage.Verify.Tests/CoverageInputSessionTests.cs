using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageInputSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verified_snapshot_is_unchanged_when_the_original_chunk_is_replaced(bool replace)
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files);
        var expected = CoverageBuildInput.Read(files.ManifestPath);
        string scratch = Path.Combine(files.Folder, "scratch");
        Directory.CreateDirectory(scratch);
        string sentinel = Path.Combine(scratch, "keep.txt");
        File.WriteAllText(sentinel, "owned by caller");
        using var session = CoverageInputSession.Open(files.ManifestPath, scratch);
        var campaign = session.Campaign;
        string owned = Assert.Single(Directory.GetDirectories(scratch));
        string chunk = Path.Combine(files.Folder, "poses.kepp");
        byte[] replacement = File.ReadAllBytes(chunk);
        if (replace)
        {
            for (int i = 0; i < 7; i++) BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(32 + i * 336 + 48), 0x3e800001);
            File.Delete(chunk);
            File.WriteAllBytes(chunk, replacement);
        }
        Assert.Equal(CoveragePackedFixture.SampleBytes(expected.Samples[0]), CoveragePackedFixture.SampleBytes(campaign.ReadSample(0)));
        Assert.Equal(CoveragePackedFixture.SampleBytes(expected.Domains[0].DenseRows[4].Sample), CoveragePackedFixture.SampleBytes(campaign.ReadDenseRow(0, 4).Sample));
        var materialized = campaign.Materialize();
        Assert.Equal(expected.InputSha256, materialized.InputSha256);
        Assert.Equal(expected.Identity, materialized.Identity);
        Assert.Contains(chunk, campaign.SourcePaths);
        Assert.Contains(campaign.SourcePaths, p => p.StartsWith(owned + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(replacement.Length, session.StorageForTests.SnapshotBytes);
        Assert.InRange(session.StorageForTests.MaximumChunkBufferBytes, 1, CoverageBuildInput.MaxPackedChunkBytes);
        Assert.InRange(session.StorageForTests.MaximumLiveNumericPoseBytes, 336, 2 * 336);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(owned));
        session.Dispose(); session.Dispose();
        Assert.False(Directory.Exists(owned)); Assert.True(File.Exists(sentinel));
        Assert.Equal(replacement, File.ReadAllBytes(chunk));
        Assert.Throws<ObjectDisposedException>(() => campaign.ReadSample(0));
        Assert.Throws<ObjectDisposedException>(() => campaign.ReadDenseRow(0, 0));
        Assert.Equal(CoveragePackedFixture.SampleBytes(expected.Samples[0]), CoveragePackedFixture.SampleBytes(materialized.Samples[0]));
        Assert.DoesNotContain(materialized.SourcePaths, p => p.StartsWith(owned, StringComparison.Ordinal));
    }

    [Fact]
    public void Resident_adapter_and_candidate_views_preserve_poses_without_temporary_ownership()
    {
        using var files = CoveragePackedFixture.Files();
        var snapshot = CoverageBuildInput.Read(files.ManifestPath);
        var campaign = CoverageCampaign.FromSnapshot(snapshot);
        var selected = campaign.WithCandidates(new[] { "b" });
        Assert.Equal(new[] { "a", "b" }, campaign.CandidateSampleIds);
        Assert.Equal(new[] { "b" }, selected.CandidateSampleIds);
        Assert.Equal(snapshot.Identity, selected.Identity);
        Assert.Equal(snapshot.InputSha256, selected.InputSha256);
        Assert.Equal(snapshot.Samples.Select(s => s.Id), selected.Samples.Select(s => s.Id));
        Assert.Equal(CoveragePackedFixture.SampleBytes(snapshot.Samples[1]), CoveragePackedFixture.SampleBytes(selected.ReadSample(1)));
        Assert.Equal(CoveragePackedFixture.SampleBytes(snapshot.Domains[0].DenseRows[4].Sample), CoveragePackedFixture.SampleBytes(selected.ReadDenseRow(0, 4).Sample));
        Assert.Equal(CoverageBuildInputRefusal.UnknownCandidate,
            Assert.Throws<CoverageBuildDataException>(() => campaign.WithCandidates(new[] { "missing" })).Reason);
        Assert.Equal(new[] { "b" }, selected.Materialize().CandidateSampleIds);
    }

    [Fact]
    public void Refused_session_removes_only_its_owned_scratch()
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files);
        string scratch = Path.Combine(files.Folder, "scratch");
        Directory.CreateDirectory(scratch);
        string keep = Path.Combine(scratch, "keep"); Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "sentinel"), "caller");
        files.Manifest["poseChunks"]![0]!["sha256"] = new string('b', 64); files.Write();
        Assert.Equal(CoverageBuildInputRefusal.PackedChunkHashMismatch,
            Assert.Throws<CoverageBuildDataException>(() => CoverageInputSession.Open(files.ManifestPath, scratch)).Reason);
        Assert.Equal(new[] { keep }, Directory.GetDirectories(scratch));
        Assert.Equal("caller", File.ReadAllText(Path.Combine(keep, "sentinel")));
        files.Manifest["poseChunks"]![0]!["sha256"] = CoverageBuildInputJson.Hash(File.ReadAllBytes(Path.Combine(files.Folder, "poses.kepp"))); files.Write();
        using (var control = CoverageInputSession.Open(files.ManifestPath, scratch)) Assert.Equal(2, control.Campaign.Samples.Count);
        Assert.Equal(new[] { keep }, Directory.GetDirectories(scratch));
    }
}
