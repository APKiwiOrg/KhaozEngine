using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalCpuReadProtectionTests
{
    // #1682. A later immediate-copy refusal cannot partially update earlier segments or deferred patches.
    [Fact]
    public void ACurrentPrefixOverlapRefusesEverySegmentOfADeviceUpdateAtomically()
    {
        using var h = new MetalRingHarness();
        var ring = h.NewRing(64, out var backing);
        using var previous = h.NewList(new());
        previous.Begin();
        previous.End();
        h.Submit(previous);
        int current = h.Rings.BeginRecording();
        var reads = new MetalRecordedReads();
        reads.Add(ring.CpuResource, ring.SegmentBaseBytes(current), 16);
        h.Rings.CpuReads.Protect(current, reads);
        var before = (byte[])backing.Clone();
        Assert.Equal(MetalFrameAccessFailure.OverlappingWrite,
            Assert.Throws<MetalFrameAccessException>(() => h.Rings.UpdateBuffer(ring, 0, new byte[] { 9 })).Reason);
        Assert.Equal(before, backing);
        Assert.False(ring.HasPendingPatches);
        h.Rings.UpdateBuffer(ring, 16, new byte[] { 9 });
        Assert.Equal(9, ring.ReadSegment(current, 16, 1)[0]);
    }

    [Fact]
    public void VertexAndTextureStridePaddingRemainsWritable()
    {
        var protection = new MetalCpuReadProtection(new(), () => 0, () => 0);
        var resource = new MetalCpuResource();
        var reads = new MetalRecordedReads();
        reads.AddStrided(resource, 16, 8, 32, 3);
        protection.Protect(0, reads);
        protection.RequireWritable(resource, 0, 16);
        protection.RequireWritable(resource, 24, 24);
        protection.RequireWritable(resource, 56, 24);
        protection.RequireWritable(resource, 88, 32);
        Assert.Throws<MetalFrameAccessException>(() => protection.RequireWritable(resource, 47, 2));
        Assert.Throws<MetalFrameAccessException>(() => protection.RequireWritable(resource, 87, 2));
    }

    // #1682. Both adjacency boundaries and gaps stay writable. Submission alone does not release the prefix.
    [Fact]
    public void OnlyOverlappingBytesStayImmutableUntilFinalCompletion()
    {
        ulong completed = 0;
        var protection = new MetalCpuReadProtection(new(), () => completed, () => 0);
        var resource = new MetalCpuResource();
        var other = new MetalCpuResource();
        var reads = new MetalRecordedReads();
        reads.Add(resource, 16, 16);
        reads.Add(resource, 48, 16);
        ulong frame = protection.Protect(0, reads);
        protection.RequireWritable(resource, 0, 16);
        protection.RequireWritable(resource, 32, 16);
        protection.RequireWritable(resource, 64, 16);
        protection.RequireWritable(other, 16, 16);
        var error = Assert.Throws<MetalFrameAccessException>(() => protection.RequireWritable(resource, 31, 2));
        Assert.Equal(MetalFrameAccessFailure.OverlappingWrite, error.Reason);
        Assert.Equal(resource.Id, error.ResourceId);
        Assert.Equal(31ul, error.OffsetBytes);
        Assert.Equal(2ul, error.LengthBytes);
        Assert.Equal(frame, error.FrameId);
        protection.Submitted(frame, 5);
        completed = 4;
        Assert.Throws<MetalFrameAccessException>(() => protection.RequireWritable(resource, 16, 16));
        completed = 5;
        protection.RequireWritable(resource, 16, 16);
        protection.RequireSlotAvailable(0);
    }

    [Fact]
    public void WritableMappingsCannotEnterOrSurviveAProtectedPrefix()
    {
        var protection = new MetalCpuReadProtection(new(), () => 0, () => 0);
        var resource = new MetalCpuResource();
        var reads = new MetalRecordedReads();
        reads.Add(resource, 32, 32);
        object mapping = new();
        protection.MapWritable(mapping, resource, 0, 64);
        // A nested read map and its Unmap must not drop the older writable pointer's protection.
        protection.Map(mapping, resource, 0, 64, writable: false);
        protection.Unmap(mapping);
        Assert.Equal(MetalFrameAccessFailure.MappedPrefix,
            Assert.Throws<MetalFrameAccessException>(() => protection.Protect(0, reads)).Reason);
        protection.Unmap(mapping);
        protection.Protect(0, reads);
        Assert.Equal(MetalFrameAccessFailure.WritableMapping,
            Assert.Throws<MetalFrameAccessException>(() => protection.MapWritable(mapping, resource, 0, 64)).Reason);
        protection.MapWritable(mapping, resource, 64, 32);
        protection.Unmap(mapping);
    }

    [Fact]
    public void AbandonmentDoesNotInventACompletionForTheSlotOrItsBytes()
    {
        var protection = new MetalCpuReadProtection(new(), () => ulong.MaxValue, () => 0);
        var resource = new MetalCpuResource();
        var reads = new MetalRecordedReads();
        reads.Add(resource, 0, 16);
        ulong frame = protection.Protect(2, reads);
        var error = Assert.Throws<MetalFrameAccessException>(() => protection.RequireSlotAvailable(2));
        Assert.Equal(MetalFrameAccessFailure.AbandonedFrameSlot, error.Reason);
        Assert.Equal(frame, error.FrameId);
        Assert.Equal(2, error.RingSegment);
        protection.RequireSlotAvailable(1);
        Assert.Throws<MetalFrameAccessException>(() => protection.RequireWritable(resource, 0, 16));
    }
}
