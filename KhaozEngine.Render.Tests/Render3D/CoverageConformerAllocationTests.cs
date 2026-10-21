using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageConformerAllocationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Conformer_creation_allocates_no_per_actor_index_buffers(bool seams, bool supportsCompute)
    {
        using var h = new CoverageSceneFixture(seams: seams, supportsCompute: supportsCompute);
        Assert.Contains(h.Factory.Buffers, buffer => (buffer.Usage & GpuBufferUsage.IndexBuffer) != 0);
        int start = h.Factory.Buffers.Count;
        using var conformer = h.Scene.CreateHostConformer(h.HostMesh.Skeleton!, h.Host);
        var allocated = h.Factory.Buffers.Skip(start).ToArray();

        Assert.DoesNotContain(allocated, buffer => (buffer.Usage & GpuBufferUsage.IndexBuffer) != 0);
        Assert.Same(h.Conformer.Gpu.Host, conformer.Gpu.Host);
        Assert.Same(h.Conformer.Topology, conformer.Topology);
        if (supportsCompute) Assert.NotEmpty(allocated);
        else Assert.Empty(allocated);
    }
}
