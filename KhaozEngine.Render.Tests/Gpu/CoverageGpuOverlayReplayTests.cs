using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;
using CoverageReferenceSupport = KhaozEngine.Render3D.CoverageReferenceSupport;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageGpuOverlayReplayTests
{
    public static IEnumerable<object[]> Cases() => CoverageOverlayTapeReference.Cases()
        .Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Partition_replays_the_serial_sample_words_and_failure_prefix_in_both_completion_orders(string name)
    {
        var item = CoverageOverlayTapeReference.Cases().Single(c => c.Name == name);
        var expected = CoverageOverlayTapeReference.Serial(item);
        foreach (bool reverse in new[] { false, true })
        {
            var actual = CoverageOverlayTapeReference.Striped(item, reverse);
            Assert.Equal(expected.Ok, actual.Ok);
            Assert.Equal(expected.Maximum, actual.Maximum);
            Assert.Equal(expected.Samples, actual.Samples);
        }
    }

    [Fact]
    public void Tape_model_matches_the_unchanged_serial_reference_for_every_existing_overlay_probe_row()
    {
        var supports = new Dictionary<uint, CoverageReferenceSupport>();
        var data = CoverageGpuSupportProbe.Data((words, support) =>
        {
            uint offset = checked((uint)words.Count);
            supports.Add(offset, support);
            words.AddRange(CoverageGpuUpload.Support(support.Support).Words);
            return offset;
        });
        int compared = 0;
        for (int row = 0; row < data.Inputs[0]; row++)
        {
            int p = 1 + row * CoverageGpuSupportProbe.InputWords;
            if (data.Inputs[p] != 2) continue;
            var support = supports[data.Inputs[p + 1]];
            Vector3 Point(int k) => new(BitConverter.UInt32BitsToSingle(data.Inputs[p + k]),
                BitConverter.UInt32BitsToSingle(data.Inputs[p + k + 1]), BitConverter.UInt32BitsToSingle(data.Inputs[p + k + 2]));
            var item = new CoverageOverlayTapeReference.Case("existing-probe-" + row, [Point(2), Point(5), Point(8)],
                support.Triangles.Select(t => new[] { support.Positions[t.A], support.Positions[t.B], support.Positions[t.C] }).ToArray());
            bool ok = support.TryOverlayMaximum(new(), item.Host, out float maximum);
            var actual = CoverageOverlayTapeReference.Striped(item);
            Assert.Equal(ok, actual.Ok);
            Assert.Equal(BitConverter.SingleToUInt32Bits(maximum), actual.Maximum);
            compared++;
        }
        Assert.Equal(13, compared);
    }

    [Fact]
    public void Failure_keeps_accepted_prefix_and_ignores_later_larger_candidates_and_speculative_failures()
    {
        var item = CoverageOverlayTapeReference.Cases().Single(c => c.Name == "early-failure-later-larger");
        var actual = CoverageOverlayTapeReference.Striped(item, true);
        Assert.False(actual.Ok);
        Assert.Equal(0x3e800000u, actual.Maximum);
        Assert.Contains(actual.Pairs[2].Take(15), word => word == 0x41000000u);
        Assert.Equal(0u, actual.Pairs[1][16]);
        Assert.Equal(0u, actual.Pairs[4][16]);
        var prefix = CoverageOverlayTapeReference.Striped(CoverageOverlayTapeReference.Cases().Single(c => c.Name == "failure-inside-pair-prefix"));
        Assert.False(prefix.Ok);
        Assert.NotEmpty(prefix.Samples);
        Assert.NotEqual(0u, prefix.Pairs[0][15]);
    }

    [Fact]
    public void Signed_zero_ties_choose_positive_zero_in_both_pair_orders()
    {
        foreach (var item in CoverageOverlayTapeReference.Cases().Where(c => c.Name.StartsWith("zero-", StringComparison.Ordinal)))
        {
            var actual = CoverageOverlayTapeReference.Striped(item);
            Assert.True(actual.Ok);
            Assert.Equal(0u, actual.Maximum);
            Assert.Contains(0u, actual.Samples);
            Assert.Contains(0x80000000u, actual.Samples);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    public void Layout_covers_each_face_pair_once_and_checks_the_entire_byte_reservation(int supports)
    {
        var fixture = CoverageFixture.CurvedPanel();
        var host = CoverageGpuUpload.Host(CoverageRefinement.Build(fixture.Source, fixture.Map), 2);
        uint[] header = CoverageGpuLayout.WorkHeader(host, supports);
        Assert.Equal((uint)supports, header[48]);
        Assert.Equal(checked((uint)(host.TriangleCount * supports)), header[45]);
        Assert.Equal(header[44] + header[45] * 17, header[46]);
        Assert.Equal(header[46] + header[47], CoverageGpuLayout.WorkWords(host, supports));
        Assert.Equal(CoverageGpuLayout.WorkWords(host) + (uint)(17 * host.TriangleCount * supports), CoverageGpuLayout.WorkWords(host, supports));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.WorkWords(host, int.MaxValue));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.WorkHeader(host, int.MaxValue));
        Assert.Throws<OverflowException>(() => CoverageGpuLayout.CreateReservation(0, 0, 0,
            uint.MaxValue / 4 - CoverageGpuLayout.WorkWords(host), 0, 0, host, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoverageGpuLayout.WorkWords(host, -1));
        Assert.Equal(4u, CoverageGpuPipeline.DispatchDepth(CoverageGpuPhase.CorrectFacesOverlay));
        Assert.Equal(1u, CoverageGpuPipeline.DispatchDepth(CoverageGpuPhase.CorrectFacesOverlayReplay));
    }
}
