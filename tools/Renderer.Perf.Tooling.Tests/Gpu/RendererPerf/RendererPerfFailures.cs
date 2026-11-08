using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfFailures
{
    internal static void Add(ImmutableArray<GpuDiagnosticFailure>.Builder failures, string stage, Exception error)
    {
        if (error is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
        else failures.Add(new(stage, error));
    }
    internal static void Add(ref ImmutableArray<GpuDiagnosticFailure>.Builder? failures, string stage, Exception error)
    {
        failures ??= ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        Add(failures, stage, error);
    }
    internal static void Release(ImmutableArray<GpuDiagnosticFailure>.Builder failures, string stage, Action? release)
    {
        try { release?.Invoke(); }
        catch (Exception error) { Add(failures, stage, error); }
    }
}
