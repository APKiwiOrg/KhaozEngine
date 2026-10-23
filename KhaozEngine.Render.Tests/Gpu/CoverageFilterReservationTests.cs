using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageFilterReservationTests
{
    [Theory]
    [InlineData(1000, false)]
    [InlineData(1024, true)]
    public void Batch_limit_admits_full_capacity_or_removes_every_filter_row(int capacity, bool fallback)
    {
        // Each actor fits alone. Only their combined reservation crosses the guaranteed range.
        var host = new CoverageGpuHostUpload(new uint[64], 0, 0, 4096, 0, 0, 0, 0) { FilterSupportCapacity = capacity };
        Assert.True(CoverageGpuLayout.WorkWords(host) < CoverageGpuFilterPolicy.MaximumWorkWords);
        var requested = new[] { host, host };
        long requestedWords = requested.Sum(h => (long)CoverageGpuLayout.WorkWords(h));
        Assert.Equal(fallback, requestedWords > 33_554_432);
        var actual = CoverageGpuFilterReservation.Fit(requested);
        using var fake = new CoverageCaptureDevice(backend: GpuBackendKind.VulkanNative);
        using var batch = new CoverageGpuBatch(fake) { MaximumFilterSupportCount = (uint)actual.Max(h => h.FilterSupportCapacity) };
        var capabilities = fake.Capabilities with
        {
            SupportsShaderFloatControls = true,
            SupportsFloat32Rte = true,
            SupportsFloat32DenormPreserve = true
        };
        using var pipeline = new CoverageGpuPipeline(new CoverageBatchDevice(fake, capabilities: capabilities));
        Assert.Equal(fallback ? CoverageGpuFilterPath.Exact : CoverageGpuFilterPath.FilteredPreserve, pipeline.PathFor(batch));
        foreach (var h in actual)
        {
            Assert.Equal(fallback ? 0 : capacity, h.FilterSupportCapacity);
            uint[] header = CoverageGpuLayout.WorkHeader(h);
            Assert.Equal(fallback ? 0u : 4096u, header[45]);
            Assert.Equal(fallback ? 0u : (uint)(4096 * capacity), header[47]);
            Assert.Equal(header[45], header[49]); Assert.Equal(header[47], header[51]);
            Assert.Equal(CoverageGpuLayout.WorkWords(h), header[50] + header[51]);
        }
    }

    [Fact]
    public void Support_replacement_reserves_new_capacity_before_publication_and_preserves_peer_ownership()
    {
        var fixture = CoverageFixture.CurvedPanel(false, 1);
        var one = CoverageSupportSurface.Compile(CoverageCommands.Solid("one", fixture.RigidPositions, fixture.RigidIndices),
            [new(0, 0, 0, 1)], System.Numerics.Matrix4x4.Identity);
        var four = CoverageCommands.Plate(fixture);
        using var fake = new CoverageCaptureDevice(backend: GpuBackendKind.VulkanNative);
        var device = new CoverageBatchDevice(fake, capabilities: fake.Capabilities with
        {
            SupportsShaderFloatControls = true,
            SupportsFloat32Rte = true,
            SupportsFloat32DenormPreserve = true
        });
        using var retired = GpuRetireQueue.Create(device);
        using var resources = new CoverageGpuResources(device, retired);
        var first = resources.Acquire(fixture.Source, fixture.Map, 2);
        var peer = resources.Acquire(fixture.Source, fixture.Map, 2);
        resources.ReserveSupports(first, [one]); resources.ReserveSupports(peer, [one]);
        var previous = resources.Batch;
        uint peerSize = first.Reservation.WorkBase == 0 ? peer.Reservation.WorkBase : 0;
        Assert.True(peerSize > 0);
        resources.ReserveSupports(first, [four]);
        Assert.NotSame(previous, resources.Batch);
        Assert.Equal(4u, resources.Batch!.MaximumFilterSupportCount);
        Assert.Same(four, Assert.Single(first.Supports));
        Assert.Same(one, Assert.Single(peer.Supports));
        Assert.True(peer.Reservation.WorkBase > peerSize);
        Assert.NotEqual(first.Reservation.WorkBase, peer.Reservation.WorkBase);
        var grown = resources.Batch;
        resources.ReserveSupports(first, [four]);
        Assert.Same(grown, resources.Batch);
        resources.ReserveSupports(first, [one]);
        Assert.Equal(1u, resources.Batch!.MaximumFilterSupportCount);
        Assert.Equal(peerSize, peer.Reservation.WorkBase);
        resources.Release(first); resources.PrepareFrame();
        Assert.Equal(0, peer.Reservation.ActorSlot);
        Assert.Equal(0u, peer.Reservation.WorkBase);
        resources.Release(peer);
        Assert.Null(resources.Batch);
    }
}
