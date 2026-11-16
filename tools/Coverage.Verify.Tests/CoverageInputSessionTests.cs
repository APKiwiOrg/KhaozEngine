using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageInputSessionTests
{
    [Theory]
    [InlineData("control")]
    [InlineData("sources")]
    [InlineData("interfaces")]
    public void Campaign_metadata_cannot_change_through_source_or_collection_storage(string mutation)
    {
        using var files = CoveragePackedFixture.Files();
        files.Manifest["domains"]![0]!["blendPathIds"] = new JsonArray("path-a");
        files.Manifest["samples"]![0]!["blendPathId"] = "path-a";
        files.Write();
        var input = CoverageBuildInput.Read(files.ManifestPath);
        var resident = CoverageCampaign.FromSnapshot(input);
        var frames = new List<string>(resident.Domains[0].AuthoredFrameIds);
        var paths = new List<string>(resident.Domains[0].BlendPathIds);
        var rows = new List<CoverageDenseRowInventory>(resident.Domains[0].DenseRows);
        var domain = resident.Domains[0] with { AuthoredFrameIds = frames, BlendPathIds = paths, DenseRows = rows };
        var domains = new[] { domain };
        var samples = resident.Samples.ToArray();
        var candidates = resident.CandidateSampleIds.ToArray();
        var sources = resident.SourcePaths.ToArray();
        var materializedSources = resident.SourcePaths.ToArray();
        var campaign = new CoverageCampaign(input.Topology, input.InverseBind, domains, samples, candidates, input.Identity,
            input.InputSha256, sources, i => new(input.Samples[i]), (d, r) => new(input.Domains[d].DenseRows[r]), () => { }, materializedSources);
        if (mutation == "sources")
        {
            frames[0] = "changed"; paths[0] = "changed"; rows[0] = rows[0] with { DenseIndex = 99 };
            domains[0] = domain with { Id = "changed" }; samples[0] = samples[0] with { Id = "changed", StateKey = 99 };
            candidates[0] = "changed"; sources[0] = "changed"; materializedSources[0] = "changed";
        }
        if (mutation == "interfaces")
        {
            var admitted = campaign.Domains[0];
            TryReplaceStorage(campaign.Domains, admitted with { Id = "changed" });
            TryReplaceStorage(campaign.Samples, campaign.Samples[0] with { Id = "changed", StateKey = 99 });
            TryReplaceStorage(campaign.CandidateSampleIds, "changed"); TryReplaceStorage(campaign.SourcePaths, "changed");
            TryReplaceStorage(admitted.AuthoredFrameIds, "changed"); TryReplaceStorage(admitted.BlendPathIds, "changed");
            TryReplaceStorage(admitted.DenseRows, admitted.DenseRows[0] with { DenseIndex = 99 });
        }
        Assert.Equal(new[] { "clip" }, campaign.Domains.Select(d => d.Id));
        Assert.Equal(new[] { "frame-a", "frame-b" }, campaign.Domains[0].AuthoredFrameIds);
        Assert.Equal(new[] { "path-a" }, campaign.Domains[0].BlendPathIds);
        Assert.Equal(new[] { "a", "b" }, campaign.CandidateSampleIds);
        Assert.Equal(input.SourcePaths, campaign.SourcePaths);
        Assert.Equal(input.SourcePaths, campaign.Materialize().SourcePaths);
        Assert.Equal(2, campaign.Samples.Count);
        for (int i = 0; i < campaign.Samples.Count; i++)
        {
            using var lease = campaign.ReadSample(i);
            var pose = lease.Value;
            Assert.Equal(new CoverageSampleInventory(pose.Id, pose.DomainId, pose.AuthoredFrameId, pose.BlendPathId, pose.StateKey.Value), campaign.Samples[i]);
            Assert.Equal(CoveragePackedFixture.SampleBytes(input.Samples[i]), CoveragePackedFixture.SampleBytes(pose));
        }
        Assert.Equal(5, campaign.Domains[0].DenseRows.Count);
        for (int i = 0; i < campaign.Domains[0].DenseRows.Count; i++)
        {
            using var lease = campaign.ReadDenseRow(0, i);
            var pose = lease.Value; var inventory = campaign.Domains[0].DenseRows[i];
            Assert.Equal((pose.DenseIndex, pose.PrecedingPhaseTick, pose.ResolvedStateKey, pose.Looped, pose.Clamped),
                (inventory.DenseIndex, inventory.PrecedingPhaseTick, inventory.ResolvedStateKey, inventory.Looped, inventory.Clamped));
            Assert.Equal(new CoverageSampleInventory(pose.Sample.Id, pose.Sample.DomainId, pose.Sample.AuthoredFrameId,
                pose.Sample.BlendPathId, pose.Sample.StateKey.Value), inventory.Sample);
        }
        var selectedIds = new[] { "b" };
        var view = campaign.WithCandidates(selectedIds); selectedIds[0] = "a";
        if (mutation == "interfaces") TryReplaceStorage(view.CandidateSampleIds, "changed");
        Assert.Equal(new[] { "b" }, view.CandidateSampleIds);
        Assert.Equal(new[] { "a", "b" }, campaign.CandidateSampleIds);
        Assert.Equal(input.SourcePaths, view.SourcePaths);
        Assert.Equal(input.InputSha256, view.InputSha256);
    }

    static void TryReplaceStorage<T>(IReadOnlyList<T> items, T replacement)
    {
        Assert.NotEmpty(items);
        void Replace(object storage)
        {
            if (storage is IList<T> generic)
                try { generic[0] = replacement; } catch (NotSupportedException) { }
            if (storage is IList nongeneric)
                try { nongeneric[0] = replacement; } catch (NotSupportedException) { }
        }
        Replace(items);
        if (items is ICollection collection)
        {
            object storage;
            try { storage = collection.SyncRoot; } catch (NotSupportedException) { return; }
            Replace(storage);
        }
    }

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
        using (var sample = campaign.ReadSample(0))
            Assert.Equal(CoveragePackedFixture.SampleBytes(expected.Samples[0]), CoveragePackedFixture.SampleBytes(sample.Value));
        using (var row = campaign.ReadDenseRow(0, 4))
            Assert.Equal(CoveragePackedFixture.SampleBytes(expected.Domains[0].DenseRows[4].Sample), CoveragePackedFixture.SampleBytes(row.Value.Sample));
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
        using (var sample = selected.ReadSample(1))
            Assert.Equal(CoveragePackedFixture.SampleBytes(snapshot.Samples[1]), CoveragePackedFixture.SampleBytes(sample.Value));
        using (var row = selected.ReadDenseRow(0, 4))
            Assert.Equal(CoveragePackedFixture.SampleBytes(snapshot.Domains[0].DenseRows[4].Sample), CoveragePackedFixture.SampleBytes(row.Value.Sample));
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
