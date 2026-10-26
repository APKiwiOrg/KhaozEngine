using System;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Windowing;

internal static class RecordingWaitProbeTestData
{
    internal static readonly Guid Epoch = new("38888768-dfac-4308-b82b-26e96e056f2b");
    internal static readonly Guid Device = new("5a3988cb-a9cc-4505-b608-b68d3b2dd00a");
    internal static readonly Guid PrefixList = new("2fcbf51b-3d31-4aad-864b-6cf68aba6429");
    internal static readonly Guid MainList = new("e28baa8f-5f18-45fe-b535-e90bac4df034");
    internal static RecordingWaitProbeRequest Request(long sequence = 1) => new(Epoch, sequence, true);
    internal static FrameDiagnosticsWindowState Window(long n = 0, bool split = true) =>
        new(true, false, false, 800, 600, 0, false, true, split, split ? n : 0, split ? n : 0);
    internal static RecordingWaitBoundary Boundary(long count = 0, long ticks = 0) =>
        new(RecordingWaitBoundaryStatus.Available, new(Device, 2, 1000, count, ticks));
    internal static RecordingWaitCallbackResult Callback(RecordingWaitProbeRequest request, bool split = true, bool zero = false)
    {
        ulong n = (ulong)request.Sequence;
        RecordingWaitObservation prefix = split ? new(RecordingWaitObservationStatus.Available, n - 1,
            new(Device, PrefixList, n, n * 2 - 1, 1, 2, zero ? 0 : 1, zero ? 0 : 5, zero ? 0 : 1, 1000))
            : new(RecordingWaitObservationStatus.NotApplicable, 0, null);
        RecordingWaitObservation main = new(RecordingWaitObservationStatus.Available, n - 1,
            new(Device, MainList, n, n * 2, 0, 2, zero ? 0 : 1, zero ? 0 : 7, zero ? 0 : 1, 1000));
        return new(request, 1, split, 1, split ? 1 : 0, 1, prefix, main, false);
    }
}
