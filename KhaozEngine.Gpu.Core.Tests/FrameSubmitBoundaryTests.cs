using System;
using System.Linq;
using KhaozEngine.Gpu.Core.Frame;
using KhaozEngine.Gpu.Core.Interop;
using Xunit;

namespace KhaozEngine.Tests.GpuCore;

[Collection("CoreBackend")]
public sealed class FrameSubmitBoundaryTests
{
    [NativeFact]
    public void Submit_returns_keys_sorted_and_complete()
    {
        var records = FrameWorkload.Create();
        FrameWorkload.ShuffleRandom(records, new Random(FrameWorkload.Seed));
        ulong[] expected = records.Select(record => record.sort_key).Order().ToArray();

        using var boundary = new FrameSubmitBoundary(FrameWorkload.RecordCount);
        boundary.Submit(records);

        var keys = new ulong[records.Length];
        Assert.Equal(records.Length, boundary.CopySortedKeys(keys));
        Assert.Equal(expected, keys);
    }

    [NativeFact]
    public void Submit_rejects_malformed_input_and_keeps_the_previous_frame()
    {
        var records = FrameWorkload.Create();
        ulong[] expected = records.Select(record => record.sort_key).Order().ToArray();
        using var boundary = new FrameSubmitBoundary(FrameWorkload.RecordCount);
        boundary.Submit(records);

        var oversized = new khaoz_draw_record[FrameWorkload.RecordCount + 1];
        Assert.Throws<ArgumentException>(() => boundary.Submit(oversized));

        var keys = new ulong[records.Length];
        Assert.Equal(records.Length, boundary.CopySortedKeys(keys));
        Assert.Equal(expected, keys);
    }
}
