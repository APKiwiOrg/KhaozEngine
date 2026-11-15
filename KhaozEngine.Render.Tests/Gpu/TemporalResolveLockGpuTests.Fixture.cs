using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalResolveLockGpuTests
{
    readonly ITestOutputHelper _output;
    readonly TemporalKeyedLineCache _keyed;

    public TemporalResolveLockGpuTests(TemporalKeyedLineCache keyed, ITestOutputHelper output)
    {
        _keyed = keyed;
        _output = output;
    }

    (LineRun[] Runs, string Message) RunKeyed(TemporalUpscale preset, TemporalHistoryDetail detail) =>
        _keyed.Get(preset, detail, () => CaptureKeyed(preset, detail));
}

/// <summary>One set of keyed-line trajectories supplies both the trail and contrast contracts.</summary>
public sealed class TemporalKeyedLineCache
{
    readonly Dictionary<(TemporalUpscale, TemporalHistoryDetail),
        (TemporalResolveLockGpuTests.LineRun[] Runs, string Message)> _runs = new();

    internal (TemporalResolveLockGpuTests.LineRun[] Runs, string Message) Get(TemporalUpscale preset,
        TemporalHistoryDetail detail,
        Func<(TemporalResolveLockGpuTests.LineRun[] Runs, string Message)> capture)
    {
        var key = (preset, detail);
        if (!_runs.TryGetValue(key, out var measured)) _runs.Add(key, measured = capture());
        return measured;
    }
}
