using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageGpuOverlayLayoutTests
{
    [Fact]
    public void Empty_support_still_reserves_four_host_guard_results_per_face()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var host = CoverageGpuUpload.Host(CoverageRefinement.Build(fixture.Source, fixture.Map), 2);
        uint[] header = CoverageGpuLayout.WorkHeader(host);
        Assert.Equal(checked((uint)host.TriangleCount * 4), header[47]);
        Assert.Equal(header[46] + header[47], CoverageGpuLayout.WorkWords(host));
        Assert.Equal(0u, header[48]);
    }

    [Fact]
    public void Replay_is_a_separate_dispatch_between_overlay_and_finish()
    {
        string[] names = Enum.GetNames<CoverageGpuPhase>();
        int overlay = Array.IndexOf(names, "CorrectFacesOverlay");
        Assert.Equal("CorrectFacesOverlayReplay", names[overlay + 1]);
        Assert.Equal("CorrectFacesFinish", names[overlay + 2]);
    }

    [Fact]
    public void Support_replacement_reserves_the_largest_sheet_and_rebuilds_the_header_atomically()
    {
        using var fixture = new CoverageSceneFixture();
        Vector3[] points = [Vector3.Zero, .. Enumerable.Range(0, 10).Select(i => new Vector3(1, i, 0))];
        uint[] indices = Enumerable.Range(0, 9).SelectMany(i => new uint[] { 0, (uint)i + 1, (uint)i + 2 }).ToArray();
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("overlay-capacity", points, indices),
            [new(0, 0, 0, 9)], Matrix4x4.Identity);
        var host = CoverageGpuUpload.Host(fixture.Conformer.Topology, 2);
        foreach (var supports in new[] { new[] { support, fixture.Support }, new[] { fixture.Support } })
        {
            fixture.Conformer.ReserveSupports(supports);
            var batch = fixture.Scene.CoverageGpuBatchForTests!;
            uint[] words = MemoryMarshal.Cast<byte, uint>(fixture.Capture.Uploads[batch.Work]).ToArray();
            int capacity = supports.Max(s => s.InwardTriangles.Count);
            Assert.Equal((uint)capacity, words[48]);
            Assert.Equal(CoverageGpuLayout.Bytes(CoverageGpuLayout.WorkWords(host, capacity)), batch.Work.SizeInBytes);
            Assert.Equal(CoverageGpuLayout.WorkHeader(host, capacity), words[..64]);
        }
    }
}
