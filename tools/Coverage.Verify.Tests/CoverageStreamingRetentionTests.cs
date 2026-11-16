using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Tools.Coverage;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageStreamingRetentionTests(ITestOutputHelper output)
{
    [Fact]
    public void Streaming_evidence_and_replay_do_not_retain_a_pose_per_row()
    {
        using var files = CoverageCampaignFixture.Files(jointCount: 128, sampleCount: 12, denseCount: 9);
        CoverageCampaignFixture.Pack(files);
        using var session = CoverageInputSession.Open(files.ManifestPath, files.Folder);
        var built = CoverageTableBuilder.Build(session.Campaign);
        var replay = CoverageReplay.Verify(session.Campaign, built.Table);
        built.Evidence.IncludeReplay(replay);
        Assert.True(built.Evidence.Complete);
        string path = Path.Combine(files.Folder, "streamed.json");
        using (var output = File.Create(path))
        using (var counter = new CountingStream(output))
        {
            built.Evidence.WriteTo(counter);
            Assert.True(counter.BytesWritten > 0);
            Assert.Equal(counter.BytesWritten, output.Length);
        }
        using (var input = File.OpenRead(path))
        using (var document = JsonDocument.Parse(input))
        {
            var root = document.RootElement;
            Assert.Equal(24, root.GetProperty("pairResults").GetArrayLength());
            Assert.Equal(built.Evidence.PairResults.Select(p => (p.EntryIndex, p.SampleId)), root.GetProperty("pairResults").EnumerateArray()
                .Select(p => (p.GetProperty("entryIndex").GetInt32(), p.GetProperty("sampleId").GetString()!)));
            var rows = root.GetProperty("domains")[0].GetProperty("denseRows");
            Assert.Equal(9, rows.GetArrayLength());
            Assert.Equal(Enumerable.Range(0, 9), rows.EnumerateArray().Select(r => r.GetProperty("denseIndex").GetInt32()));
            Assert.All(rows.EnumerateArray(), r => Assert.Equal(128, r.GetProperty("sample").GetProperty("palette").GetArrayLength()));
            Assert.Equal(9, root.GetProperty("replay").GetProperty("poses").GetArrayLength());
        }
        var legacyOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        };
        // The public logical shape is an independent serialization oracle for the streaming writer.
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(built.Evidence, legacyOptions), File.ReadAllBytes(path));
        var storage = session.StorageForTests;
        output.WriteLine($"Storage reservations: snapshotBytes={storage.SnapshotBytes}, maximumChunkBufferBytes={storage.MaximumChunkBufferBytes}, maximumLiveNumericPoseBytes={storage.MaximumLiveNumericPoseBytes}");
        int poseBytes = 64 + 64 * 128 + 144;
        Assert.True(storage.SnapshotBytes > storage.MaximumChunkBufferBytes);
        Assert.InRange(storage.MaximumChunkBufferBytes, 1, 8_388_608);
        Assert.InRange(storage.MaximumLiveNumericPoseBytes, 1, 2 * poseBytes);
        Assert.Equal(8, built.Evidence.Domains[0].DenseRows[8].DenseIndex);
        Assert.Equal(8, replay.Poses[8].DenseIndex);
        session.Dispose();
        // Access after a prior read must still reconstruct, rather than keeping a cached decoded row.
        Assert.Throws<ObjectDisposedException>(() => built.Evidence.Domains[0].DenseRows[8]);
        Assert.Throws<ObjectDisposedException>(() => replay.Poses[8]);
        Assert.True(replay.Accepted);
        Assert.Equal(9, replay.PoseCount);
    }

    [Fact]
    public void Resident_results_keep_their_input_after_the_callers_local_reference_is_released()
    {
        static CoverageEvidenceManifest Run()
        {
            var input = CoverageReplayTests.DenseInput();
            var built = CoverageTableBuilder.Build(input);
            built.Evidence.IncludeReplay(CoverageReplay.Verify(input, built.Table));
            return built.Evidence;
        }
        var evidence = Run();
        Assert.True(evidence.Complete);
        Assert.Equal(5, evidence.Domains[0].DenseRows.Count);
        Assert.Equal("b", evidence.Replay!.Poses[4].Input.Sample.Id);
        using var output = new MemoryStream(); evidence.WriteTo(output);
        Assert.Equal(evidence.Write(), output.ToArray());
    }

    sealed class CountingStream(Stream destination) : Stream
    {
        internal long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override void Write(byte[] buffer, int offset, int count) { destination.Write(buffer, offset, count); BytesWritten += count; }
        public override void Write(ReadOnlySpan<byte> buffer) { destination.Write(buffer); BytesWritten += buffer.Length; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
