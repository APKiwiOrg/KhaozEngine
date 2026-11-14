using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11DynamicBindingRangeTests
{
    public static IEnumerable<object[]> Refusals()
    {
        foreach (bool deferred in new[] { false, true })
        foreach (bool compute in new[] { false, true })
        {
            yield return new object[] { deferred, compute, 128u, GpuDynamicBindingRefusal.OffsetMisaligned, 2432UL };
            yield return new object[] { deferred, compute, 255u, GpuDynamicBindingRefusal.OffsetMisaligned, 2559UL };
            yield return new object[] { deferred, compute, 768u, GpuDynamicBindingRefusal.WindowOutsideSegment, 0UL };
            yield return new object[] { deferred, compute, 0xffffff00u, GpuDynamicBindingRefusal.WindowOutsideSegment, 0UL };
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AnInvalidWindow_IsRefusedAtTheBindCall(bool deferred, bool compute,
        uint offset, GpuDynamicBindingRefusal reason, ulong composedOffset)
    {
        // #1689. A missing guard used to round an offset or read into the next ring segment.
        using var ring = new D3D11RingHarness(1024, 3);
        ring.Allocator.BeginFrame();
        ring.Allocator.BeginFrame();
        var harness = new D3D11BindFixtures.Harness(rings: ring.Allocator);
        using D3D11ResourceLayout layout = D3D11BindFixtures.Layout(
            D3D11BindFixtures.S("Sampler", compute ? GpuShaderStages.Compute : GpuShaderStages.Vertex),
            D3D11BindFixtures.U("Uniform", compute ? GpuShaderStages.Compute : GpuShaderStages.Vertex, dynamic: true));
        using D3D11ResourceSet set = D3D11BindFixtures.Set(layout, new TraceSampler(),
            new GpuBufferRange(new FakeRingBackedBuffer(ring.Ring), 256, 256));
        using IGpuCommandList list = D3D11CommandDrivers.Create(
            deferred ? D3D11RecordMode.Deferred : D3D11RecordMode.Immediate, harness.Emitter);
        list.Begin();
        void Bind(uint dynamicOffset)
        {
            if (compute) list.SetComputeResourceSet(2, set, dynamicOffset);
            else list.SetGraphicsResourceSet(2, set, dynamicOffset);
        }

        Bind(0);
        Bind(256);
        Bind(512); // The window ends exactly at 1024, including its nonzero range offset.
        D3D11CommandStream? stream = deferred
            ? ((D3D11CommandRecorder<D3D11StreamEmitter>)list).Emitter.Stream : null;
        int? before = stream?.Count;

        var refused = Assert.Throws<GpuDynamicBindingRangeException>(() => Bind(offset));

        Assert.Equal(reason, refused.Reason);
        Assert.Equal(2u, refused.Set);
        Assert.Equal(1u, refused.Binding);
        Assert.Equal(256UL, refused.RangeOffset);
        Assert.Equal((ulong)offset, refused.DynamicOffset);
        Assert.Equal(256UL, refused.RangeBytes);
        Assert.Equal(1024UL, refused.SegmentStride);
        if (reason == GpuDynamicBindingRefusal.OffsetMisaligned)
        {
            Assert.Equal(2048UL, refused.RingBase);
            Assert.Equal(composedOffset, refused.ComposedOffset);
            Assert.Equal(256UL, refused.AlignmentBytes);
        }
        else Assert.Equal("GpuBufferRange", refused.RecoveryOperation);
        if (stream is not null) Assert.Equal(before, stream.Count);

        list.End();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlignmentChecksTheComposedOffset_WithoutRequiringARing(bool deferred)
    {
        // #1689. Two unaligned terms may sum to a valid 256-byte native offset.
        var harness = new D3D11BindFixtures.Harness();
        using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
        using D3D11ResourceSet set = D3D11BindFixtures.Set(layout,
            new GpuBufferRange(new FakeBuffer(1024), 1, 256));
        using IGpuCommandList list = D3D11CommandDrivers.Create(
            deferred ? D3D11RecordMode.Deferred : D3D11RecordMode.Immediate, harness.Emitter);
        list.Begin();
        list.SetGraphicsResourceSet(0, set, 255);

        var refused = Assert.Throws<GpuDynamicBindingRangeException>(
            () => list.SetGraphicsResourceSet(0, set, 254));

        Assert.Equal(GpuDynamicBindingRefusal.OffsetMisaligned, refused.Reason);
        Assert.Equal(0u, refused.Set);
        Assert.Equal(0u, refused.Binding);
        Assert.Equal(255UL, refused.ComposedOffset);
        Assert.Equal(0UL, refused.RingBase);
        Assert.Equal(1UL, refused.RangeOffset);
        Assert.Equal(254UL, refused.DynamicOffset);
        Assert.Equal(256UL, refused.RangeBytes);
        Assert.Equal(256UL, refused.AlignmentBytes);
        Assert.Null(refused.SegmentStride);

        var zero = Assert.Throws<GpuDynamicBindingRangeException>(
            () => list.SetGraphicsResourceSet(0, set, 0));
        Assert.Equal(GpuDynamicBindingRefusal.OffsetMisaligned, zero.Reason);
        Assert.Equal(1UL, zero.ComposedOffset);
        Assert.Equal(0UL, zero.DynamicOffset);

        var withoutOffset = Assert.Throws<GpuDynamicBindingRangeException>(
            () => list.SetGraphicsResourceSet(0, set));
        Assert.Equal(GpuDynamicBindingRefusal.OffsetMisaligned, withoutOffset.Reason);
        Assert.Equal(1UL, withoutOffset.ComposedOffset);
        Assert.Equal(0UL, withoutOffset.DynamicOffset);
        list.End();
    }
}
