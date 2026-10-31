using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class PointShadowAttributionArenaTests(ITestOutputHelper output)
{
    static readonly Guid Epoch = new("559315ea-5e99-44ed-925e-4ecaa03291f2");
    internal static PointAttributionLimits Small => new(4, 4, 8, 8, 16, 8, 4);
    static PointAttributionCaster Caster(uint word = 0)
    {
        var caster = new PointAttributionCaster { MeshIndex = 1, MeshGeneration = 2, CastKind = 1, MotionKey = 7 };
        caster.Matrix[12] = word;
        return caster;
    }

    [Fact]
    public void Exact_words_and_order_survive_forced_lookup_collisions_and_later_frames()
    {
        var capture = new PointShadowAttributionCapture(Epoch, Small, 0);
        Assert.True(capture.BeginFrame(Epoch, 1, 19));
        capture.BeginSpan();
        Assert.Equal(0, capture.AddContributor(Caster(), 3, 1));
        Assert.Equal(1, capture.AddContributor(Caster(0x80000000), 4, 1));
        int first = capture.EndSpan(2);
        capture.AddRow(new() { Span = first, Slot = 2 });
        var range = capture.EndFrame(PointAttributionAvailability.Available);
        Assert.True(range.Complete);
        Assert.Equal(2, range.CasterCount);
        Assert.True(capture.BeginFrame(Epoch, 2, 20));
        capture.BeginSpan();
        Assert.Equal(0, capture.AddContributor(Caster(), 3, 1));
        Assert.Equal(1, capture.AddContributor(Caster(0x80000000), 4, 1));
        Assert.Equal(first, capture.EndSpan(2));
        capture.AddRow(new() { Span = first, Slot = 7 });
        capture.EndFrame(PointAttributionAvailability.Available);
        capture.Freeze();
        Assert.Equal(2, capture.CasterCount);
        Assert.Equal(1, capture.SpanCount);
        Assert.Equal(2, capture.ReferenceCount);
        Assert.Equal(2, capture.GetRow(range.RowStart).Slot);
        Assert.Equal(0x80000000u, capture.GetCaster(1).Matrix[12]);
        Assert.False(capture.BeginFrame(Epoch, 3, 21));
    }

    [Theory]
    [InlineData(PointAttributionCapacity.Callbacks)]
    [InlineData(PointAttributionCapacity.Requests)]
    [InlineData(PointAttributionCapacity.Rows)]
    [InlineData(PointAttributionCapacity.Casters)]
    [InlineData(PointAttributionCapacity.References)]
    [InlineData(PointAttributionCapacity.Spans)]
    [InlineData(PointAttributionCapacity.Contributors)]
    public void Exhaustion_latches_the_exact_capacity_without_wrapping(PointAttributionCapacity capacity)
    {
        var limits = Small;
        limits = capacity switch
        {
            PointAttributionCapacity.Callbacks => limits with { Callbacks = 1 },
            PointAttributionCapacity.Requests => limits with { Requests = 1 },
            PointAttributionCapacity.Rows => limits with { Rows = 1 },
            PointAttributionCapacity.Casters => limits with { Casters = 1 },
            PointAttributionCapacity.References => limits with { References = 1 },
            PointAttributionCapacity.Spans => limits with { Spans = 1 },
            _ => limits with { Contributors = 1 },
        };
        var capture = new PointShadowAttributionCapture(Epoch, limits);
        Assert.True(capture.BeginFrame(Epoch, 1, 1));
        capture.AddRow(new() { Slot = 11 });
        capture.BeginSpan(); capture.AddContributor(Caster(), 0, 0); capture.EndSpan(1);
        if (capacity is PointAttributionCapacity.Rows or PointAttributionCapacity.Requests) capture.AddRow(default);
        else if (capacity != PointAttributionCapacity.Callbacks)
        {
            capture.BeginSpan(); capture.AddContributor(Caster(), 0, 0);
            capture.AddContributor(Caster(1), 1, 0); capture.EndSpan(2);
        }
        capture.EndFrame(PointAttributionAvailability.Available);
        if (capacity == PointAttributionCapacity.Callbacks) capture.BeginFrame(Epoch, 2, 2);
        Assert.Equal(capacity, capture.Overflow);
        Assert.Equal(capacity == PointAttributionCapacity.Callbacks ? 2 : 1, capture.OverflowSequence);
        Assert.Equal(11, capture.GetRow(0).Slot);
    }

    [Fact]
    public void Wrong_epoch_duplicate_sequence_and_cancellation_cannot_mutate_published_slices()
    {
        var capture = new PointShadowAttributionCapture(Epoch, Small);
        Assert.False(capture.BeginFrame(Guid.NewGuid(), 1, 1));
        Assert.True(capture.BeginFrame(Epoch, 1, 1));
        capture.AddRow(new() { Key = 71 });
        var first = capture.EndFrame(PointAttributionAvailability.Empty);
        Assert.False(capture.BeginFrame(Epoch, 1, 2));
        Assert.True(capture.BeginFrame(Epoch, 2, 2));
        capture.AddRow(new() { Key = 93 });
        capture.UpdateRow(0, new() { Key = 999 });
        capture.Freeze();
        Assert.Equal(71, capture.GetRow(first.RowStart).Key);
        Assert.False(capture.GetFrame(1).Complete);
        Assert.Equal(PointAttributionAvailability.Abandoned, capture.GetFrame(1).Availability);
    }

    [Fact]
    public void Exact_header_pool_reuses_inputs_and_refuses_explicit_input_overflow()
    {
        var c = new PointShadowAttributionCapture(Epoch, Small with { Inputs = 1 }, 0);
        Assert.True(c.BeginFrame(Epoch, 1, 1));
        var input = new PointAttributionInput(); input.Header[1] = ulong.MaxValue;
        Assert.Equal(0, c.InternInput(input)); Assert.Equal(0, c.InternInput(input));
        input.Raw[0] = 0x80000000;
        Assert.Equal(-1, c.InternInput(input));
        Assert.Equal(PointAttributionCapacity.Inputs, c.Overflow);
        Assert.Equal(0u, c.GetInput(0).Raw[0]);
    }

    [Fact]
    public void Default_owned_storage_including_lookup_and_scratch_is_below_64_MiB()
    {
        new PointShadowAttributionCapture(Epoch, Small).Freeze();
        PointShadowAttributionCapture capture;
        long bytes;
        using (AllocationRegion.Start("complete attribution arena", 96 * 1024 * 1024))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            capture = new PointShadowAttributionCapture(Epoch);
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.InRange(capture.AllocatedBytes, 1, 64 * 1024 * 1024 - 1);
        Assert.InRange(bytes, 1, capture.AllocatedBytes);
        output.WriteLine($"Actual arena allocation={bytes}, conservative owned budget={capture.AllocatedBytes}, row={Unsafe.SizeOf<PointAttributionRow>()}, caster={Unsafe.SizeOf<PointAttributionCaster>()}, input={Unsafe.SizeOf<PointAttributionInput>()}, reference={Unsafe.SizeOf<PointAttributionContributor>()}, frame={Unsafe.SizeOf<PointAttributionFrame>()}.");
    }
}
