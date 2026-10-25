using System;
using System.Collections.Immutable;

namespace KhaozEngine.Gpu;

/// <summary>An adopted context or the actual ordered creation and rejected-cleanup failures.</summary>
public sealed record GpuDeviceCreationAttempt(GpuDeviceContext? Context, ImmutableArray<GpuDiagnosticFailure> Failures);

public sealed partial class GpuDeviceContext
{
    /// <summary>Resolve a backend's immutable settings without creating a native device.</summary>
    public static IGpuHeadlessDiagnosticSettings ResolveHeadlessDiagnosticSettings(GpuBackendKind backend,
        GpuPipelineDiagnosticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreflightProvider(new(backend, GpuBackendSource.UserPreference, null), false, out var provider);
        return provider is IGpuHeadlessDiagnosticProvider diagnostics
            ? diagnostics.ResolveHeadlessSettings(request)
            : throw new NotSupportedException($"The {backend} provider has no explicit diagnostic settings route.");
    }

    /// <summary>Create exactly the requested backend through the ordinary lifecycle and adoption owners.</summary>
    public static GpuDeviceCreationAttempt CreateHeadlessAttempt(GpuBackendKind backend,
        IGpuHeadlessDiagnosticSettings settings)
    {
        var failures = ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
        string stage = "gpu.context.provider";
        try
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (settings.Backend != backend) throw new ArgumentException("Settings name a different backend.", nameof(settings));
            var selection = new GpuBackendSelection(backend, GpuBackendSource.UserPreference, null);
            PreflightProvider(selection, false, out var provider);
            if (provider is not IGpuHeadlessDiagnosticProvider diagnostics)
                throw new NotSupportedException($"The {backend} provider has no explicit diagnostic settings route.");
            lock (_lifecycleGate)
            {
                var created = diagnostics.CreateHeadless(settings);
                stage = "gpu.context.adopt";
                return new(Adopt(created, selection, failures), []);
            }
        }
        catch (Exception error)
        {
            // Adopt records its own event before rejected cleanup. Provider throws have not been owned yet.
            if (failures.Count == 0)
            {
                if (error is GpuDiagnosticFailureException transport) failures.AddRange(transport.Failures);
                else failures.Add(new(stage, error));
            }
            return new(null, failures.ToImmutable());
        }
    }
}
