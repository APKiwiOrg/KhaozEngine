using System;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal sealed partial class D3D11GpuDevice : IGpuResourceMemoryDiagnostics, ID3D11InvocationDiagnostics
{
    readonly D3D11MemoryDiagnostics _memoryDiagnostics = new();
    readonly D3D11InvocationDiagnostics _invocations;

    public GpuResourceMemorySnapshot ReadMemorySnapshot() => _memoryDiagnostics.Snapshot(_tornDown || _liveness.IsDead);

    public D3D11InvocationBlock CaptureInvocations(Action recordAndSubmit) => _invocations.CaptureInvocations(recordAndSubmit);
}
