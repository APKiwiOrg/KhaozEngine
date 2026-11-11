using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tools.Coverage;
using Xunit;

namespace KhaozEngine.Tests.Tools.Coverage;

public sealed class CoverageReplayTests
{
    [Fact]
    public void Dense_replay_uses_preceding_phase_and_current_consumer_blend_bins()
    {
        var input = DenseInput();
        var table = CoverageTableBuilder.Build(input).Table;
        var replay = CoverageReplay.Verify(input, table);
        Assert.Equal(5, replay.PoseCount);
        Assert.Equal(new ulong[] { 1, 1, 1, 1, 2 }, replay.Poses.Select(p => p.ResolvedStateKey));
        Assert.Equal(new[] { 0, 0, 0, 0, 1 }, replay.Poses.Select(p => p.PrecedingPhaseTick));
        Assert.Equal(0, replay.HoleCount);
        Assert.Equal(0, replay.SelectedFailures);
        Assert.Equal(0d, replay.ChangedRecordObservedMm);
        Assert.Equal(0d, replay.ChangedRecordUpperEstimateMm);
        Assert.Equal(0, replay.UnresolvedChangedRecordPoses);
        var currentBlend = DenseInput(blendAtThree: true);
        var blended = CoverageReplay.Verify(currentBlend, table);
        Assert.Equal(2ul, blended.Poses[3].ResolvedStateKey);
        Assert.Equal(0, blended.Poses[3].PrecedingPhaseTick);
        Assert.Equal(blended.Poses[4].EntryIndex, blended.Poses[3].EntryIndex);
        Assert.True(blended.Accepted);
    }

    [Fact]
    public void Holes_measure_the_current_raw_pose_and_finish_every_row()
    {
        var input = DenseInput();
        var table = CoverageTableBuilder.Build(input).Table;
        var hole = CoverageRecessionTable.Create(table.Identity, table.Representatives,
            Enumerable.Range(0, table.EntryCount).SelectMany(i => table.Entry(i).ToArray()).ToArray(),
            table.States.ToArray().Select(s => s.Key.Value == 1 ? s with { EntryIndex = -1 } : s).ToArray());
        var replay = CoverageReplay.Verify(input, hole);
        Assert.Equal(5, replay.PoseCount);
        Assert.Equal(4, replay.HoleCount);
        Assert.All(replay.Poses.Take(4), p =>
        {
            Assert.Equal(-1, p.EntryIndex);
            Assert.Equal(0, p.ChangedTriangleCount);
            Assert.Equal(input.Topology.TriangleCount, p.InheritedTriangleCount);
        });
        Assert.True(replay.Poses[0].Inherited.ObservedMm > 0);
        Assert.Equal(0, replay.Poses[0].ChangedRecord.ObservedMm);
        Assert.False(replay.Accepted);
        var refusal = Assert.Throws<CoverageVerificationException>(replay.EnsureAccepted);
        Assert.Equal(CoverageVerificationRefusal.ReplayHole, refusal.Reason);
        Assert.Equal("clip", refusal.DomainId);
        Assert.Equal(0, refusal.DenseIndex);
        Assert.Equal(1ul, refusal.StateKey);
    }

    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public void Explicit_loop_and_endpoint_clamp_preserve_the_supplied_pose_and_key(bool looped, bool clamped, int tick)
    {
        var original = DenseInput();
        var rows = original.Domains[0].DenseRows.ToArray();
        rows[4] = rows[4] with { PrecedingPhaseTick = tick, Looped = looped, Clamped = clamped };
        var input = WithRows(original, rows);
        var replay = CoverageReplay.Verify(input, CoverageTableBuilder.Build(input).Table);
        Assert.Equal(5, replay.PoseCount);
        Assert.Equal(rows[4], replay.Poses[4].Input);
        Assert.True(replay.Accepted);
    }

    [Theory]
    [InlineData("missing", CoverageVerificationRefusal.MissingDenseRows)]
    [InlineData("gap", CoverageVerificationRefusal.DenseSequence)]
    [InlineData("phase", CoverageVerificationRefusal.PrecedingPhaseTick)]
    [InlineData("policy", CoverageVerificationRefusal.ReplayPolicy)]
    public void Missing_or_implicit_replay_work_refuses_with_domain_and_dense_identifiers(string defect, CoverageVerificationRefusal reason)
    {
        var original = DenseInput();
        var rows = original.Domains[0].DenseRows.ToArray();
        if (defect == "missing") rows = [];
        if (defect == "gap") rows[2] = rows[2] with { DenseIndex = 3 };
        if (defect == "phase") rows[2] = rows[2] with { PrecedingPhaseTick = 1 };
        if (defect == "policy") rows[2] = rows[2] with { Looped = true, Clamped = true };
        var input = WithRows(original, rows);
        var refusal = Assert.Throws<CoverageVerificationException>(() => CoverageReplay.Verify(input, CoverageTableBuilder.Build(input).Table));
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal("clip", refusal.DomainId);
        Assert.True(CoverageReplay.Verify(original, CoverageTableBuilder.Build(original).Table).Accepted);
    }

    [Fact]
    public void Changed_record_depth_blocks_even_when_the_raw_pose_already_intruded()
    {
        var input = DenseInput();
        var rows = input.Domains[0].DenseRows.ToArray();
        var sample = rows[2].Sample;
        var commands = sample.Commands.ToArray();
        commands[0] = commands[0] with { TransitionWeight = .25f };
        rows[2] = rows[2] with
        {
            Sample = new(sample.Id, sample.DomainId, sample.AuthoredFrameId, sample.BlendPathId,
            sample.StateKey, sample.ActorModel, sample.Palette, commands)
        };
        var partial = WithRows(input, rows);
        var replay = CoverageReplay.Verify(partial, CoverageTableBuilder.Build(input).Table);
        Assert.Equal(5, replay.PoseCount);
        Assert.Equal(0, replay.SelectedFailures);
        Assert.True(replay.ChangedRecordObservedMm > 0);
        Assert.True(replay.ChangedRecordRawDepthMm > 0);
        Assert.False(replay.Accepted);
        Assert.Equal(CoverageVerificationRefusal.ChangedRecordPenetration,
            Assert.Throws<CoverageVerificationException>(replay.EnsureAccepted).Reason);
        var unresolved = CoverageReplay.Verify(partial, CoverageTableBuilder.Build(input).Table, 1);
        Assert.Equal(5, unresolved.PoseCount);
        Assert.True(unresolved.UnresolvedChangedRecordPoses > 0);
        Assert.Equal(CoverageVerificationRefusal.UnresolvedChangedRecords,
            Assert.Throws<CoverageVerificationException>(unresolved.EnsureAccepted).Reason);
    }

    [Fact]
    public void Unchanged_uncommanded_intrusion_is_reported_as_inherited_fit_work()
    {
        var panel = new ExactPanelFixture(seams: false);
        var data = panel.Data;
        var extra = new Vector3[] { new(-4, -3, .15f), new(4, -3, .15f), new(0, 6, .15f) };
        int start = data.Source.Positions.Length;
        var source = new CoverageSourceTopology("synthetic-detached-triangle", CoverageBuildFixture.Hash,
            [.. data.Source.Positions, .. extra],
            data.Source.Vertices.Concat(extra.Select((_, i) => new CoverageSourceVertex(1, 0, i, -1, 1, i))),
            data.Source.Triangles.Append(new(1, 0, 0, -1, 1, start, start + 1, start + 2)),
            [.. data.Source.SkinVertices, .. extra.Select(p => new SkinnedVertex { Position = p, Normal = Vector3.UnitZ, Color = Vector4.One })]);
        var map = new HostCoverageMap(CoverageBuildFixture.Hash,
            data.Map.Topology.Concat(extra.Select((_, i) => new CoverageTopologyEntry(1, 0, i, 1000 + i))),
            data.Map.Regions, data.Map.Refinement, data.Map.Authoring);
        var topology = CoverageRefinement.Build(source, map);
        var command = panel.Command with { Topology = topology, MaxWorldRecession = .5f };
        var sample = new CoverageSample("a", "clip", "frame-a", null, new(1), Matrix4x4.Identity, panel.Palette, [command]);
        var original = CoverageBuildFixture.Input();
        var input = new CoverageBuildInput(topology, original.InverseBind,
            [new("clip", CoverageDomainKind.AuthoredClip, ["frame-a"], [], [new(0, 0, 1, sample, false, false)])],
            [sample], ["a"], original.Identity, original.InputSha256);
        var built = CoverageTableBuilder.Build(input);
        Assert.True(built.Evidence.Complete);
        var replay = CoverageReplay.Verify(input, built.Table);
        Assert.Equal(0, replay.SelectedFailures);
        Assert.True(replay.InheritedObservedMm > 0);
        Assert.Equal(0, replay.ChangedRecordObservedMm);
        Assert.Equal(0, replay.ChangedRecordUpperEstimateMm);
        Assert.True(replay.Accepted);
    }

    internal static CoverageBuildInput DenseInput(bool blendAtThree = false)
    {
        var input = CoverageBuildFixture.Input();
        var rows = Enumerable.Range(0, 5).Select(i =>
        {
            var pose = input.Samples[i == 4 || blendAtThree && i == 3 ? 1 : 0];
            return new CoverageDenseRow(i, i / 4, pose.StateKey.Value, pose, false, false);
        }).ToArray();
        return WithRows(input, rows);
    }

    internal static CoverageBuildInput WithRows(CoverageBuildInput input, CoverageDenseRow[] rows) => new(input.Topology, input.InverseBind,
        [new("clip", CoverageDomainKind.AuthoredClip, ["frame-a", "frame-b"], [], rows)], input.Samples,
        input.CandidateSampleIds, input.Identity, input.InputSha256);
}
