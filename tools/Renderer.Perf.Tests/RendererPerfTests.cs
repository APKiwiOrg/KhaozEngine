using System;
using KhaozEngine.Tests.Gpu.RendererPerf;
using Xunit;

namespace Renderer.Perf.Tests;

public sealed class RendererPerfTests
{
    [AuthorizedRendererFact]
    [Trait("Category", "RendererPerformance")]
    public void Measure_authorized_unit() => RendererPerfEntrypoint.Run(
        Environment.GetEnvironmentVariable("KE_RENDERER_PERF_UNIT")!,
        Environment.GetEnvironmentVariable("KE_RENDERER_PERF_REPORTS")
            ?? throw new InvalidOperationException("KE_RENDERER_PERF_REPORTS must be explicit."));
}

// Discovery reads only the opt-in token. It never probes a device, clock or manifest.
public sealed class AuthorizedRendererFactAttribute : FactAttribute
{
    public AuthorizedRendererFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KE_RENDERER_PERF_UNIT")))
            Skip = "Only a separately approved hosted renderer unit may execute this fact.";
    }
}
