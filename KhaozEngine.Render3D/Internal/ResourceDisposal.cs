using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

// Teardown-local collection. Callers use a false release result to retain dependent ownership.
internal sealed class ResourceDisposal
{
    ImmutableArray<GpuDiagnosticFailure>.Builder? _failures;
    int _ordinal;
    internal bool Release(string kind, Action? dispose)
    {
        int ordinal = _ordinal++;
        try { dispose?.Invoke(); return true; }
        catch (Exception error)
        {
            _failures ??= ImmutableArray.CreateBuilder<GpuDiagnosticFailure>();
            if (error is GpuDiagnosticFailureException transport) _failures.AddRange(transport.Failures);
            else _failures.Add(new("renderer.session.dispose.scene." + kind + "." + ordinal, error));
            return false;
        }
    }
    internal void ThrowIfFailed()
    {
        if (_failures is not null) throw new GpuDiagnosticFailureException(_failures.ToImmutable());
    }
}
