using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

/// <summary>A pose the packed session decoded is numerically usable only while its lease is held. A released pose is
/// overwritten, so later numerical use is refused. The live pose count covers undisposed leases and transient decoding
/// only. Resident data is never touched, and public projections hand out caller owned copies.</summary>
public sealed class CoverageReleasedPoseTests
{
    [Fact]
    public void A_session_pose_kept_past_its_lease_is_refused_while_the_same_pose_inside_its_lease_constructs()
    {
        using var files = CoveragePackedFixture.Files();
        CoveragePackedFixture.Pack(files);
        using var session = CoverageInputSession.Open(files.ManifestPath, files.Folder);
        var campaign = session.Campaign;
        var constructor = new CoverageExactConstructor(campaign.Topology, campaign.InverseBind);
        CoverageSample kept;
        using (var lease = campaign.ReadSample(0))
        {
            kept = lease.Value;
            Assert.True(Construct(constructor, campaign.Topology, kept));
        }
        // The released storage holds only nonfinite words, so exact construction refuses it.
        Assert.False(Construct(constructor, campaign.Topology, kept));
        Assert.All(Words(kept), word => Assert.False(float.IsFinite(word)));
    }

    [Fact]
    public void A_resident_pose_is_unchanged_after_its_lease_is_disposed()
    {
        var input = CoverageReplayTests.DenseInput();
        var campaign = CoverageCampaign.FromSnapshot(input);
        var sample = input.Samples[0];
        var row = input.Domains[0].DenseRows[4];
        byte[] sampleBytes = CoveragePackedFixture.SampleBytes(sample), rowBytes = CoveragePackedFixture.SampleBytes(row.Sample);
        using (var lease = campaign.ReadSample(0)) Assert.Same(sample, lease.Value);
        using (var lease = campaign.ReadDenseRow(0, 4)) Assert.Same(row, lease.Value);
        Assert.Equal(sampleBytes, CoveragePackedFixture.SampleBytes(sample));
        Assert.Equal(rowBytes, CoveragePackedFixture.SampleBytes(row.Sample));
        Assert.All(Words(sample).Concat(Words(row.Sample)), word => Assert.True(float.IsFinite(word)));
    }

    [Fact]
    public void Materialized_inputs_and_replay_poses_stay_valid_after_every_session_lease_is_released()
    {
        using var files = CoveragePackedFixture.Files();
        // Expected words come straight from the manifest JSON, independent of any session or materialized copy.
        var samples = files.Manifest["samples"]!.AsArray().Select(Expected).ToArray();
        var rows = files.Manifest["domains"]![0]!["denseRows"]!.AsArray().Select(r => Expected(r!["sample"])).ToArray();
        CoveragePackedFixture.Pack(files);
        using var session = CoverageInputSession.Open(files.ManifestPath, files.Folder);
        var materialized = session.Campaign.Materialize();
        var built = CoverageTableBuilder.Build(session.Campaign);
        var replay = CoverageReplay.Verify(session.Campaign, built.Table);
        var pose = replay.Poses[4];
        // Every later read and the session's disposal release session storage. The copies are caller owned.
        using (var again = session.Campaign.ReadDenseRow(0, 4)) Assert.NotSame(pose.Input.Sample, again.Value.Sample);
        session.Dispose();
        Assert.Equal(samples, materialized.Samples.Select(CoveragePackedFixture.SampleBytes));
        Assert.Equal(rows, materialized.Domains[0].DenseRows.Select(r => CoveragePackedFixture.SampleBytes(r.Sample)));
        Assert.Equal(rows[4], CoveragePackedFixture.SampleBytes(pose.Input.Sample));
    }

    static byte[] Expected(System.Text.Json.Nodes.JsonNode? sample) =>
        CoveragePackedFixture.RecordBytes(CoveragePackedFixture.Numeric(sample!.AsObject()));

    static bool Construct(CoverageExactConstructor constructor, CoverageRefinement topology, CoverageSample sample) =>
        constructor.TryConstruct(sample.Palette, sample.ActorModel, sample.Commands, new ModelVertex[topology.VertexCount],
            new float[topology.PositionGroupCount], new int[topology.PositionGroupCount], out _);

    static IEnumerable<float> Words(CoverageSample sample)
    {
        static IEnumerable<float> Of(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        var words = Of(sample.ActorModel).Concat(sample.Palette.ToArray().SelectMany(Of));
        foreach (var command in sample.Commands.ToArray())
            words = words.Concat(Of(command.EquipmentWorld)).Concat(Of(command.CoverageWorld)).Append(command.TransitionWeight);
        return words.ToArray();
    }
}
