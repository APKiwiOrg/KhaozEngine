using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Registers (or, with a null provider, temporarily REMOVES) a backend provider for the duration of a test and
    /// restores whatever was there before. The registry is process-wide static state, so a leaked fake would
    /// follow every later test in the run.
    /// <para>
    /// Restoring rather than simply unregistering matters because these tests name a REAL backend kind: once
    /// <c>KhaozEngine.Gpu.D3D11</c> exists, this test assembly registers a genuine provider for it at load, and a
    /// scope that unregistered on the way out would take that away from every test after it.
    /// </para>
    /// </summary>
    internal sealed class BackendProviderScope : IDisposable
    {
        readonly GpuBackendKind _backend;
        readonly IGpuBackendProvider? _previous;

        internal BackendProviderScope(GpuBackendKind backend, IGpuBackendProvider? provider)
        {
            _backend = backend;
            GpuBackendProviders.TryGet(backend, out _previous);
            if (provider is null) GpuBackendProviders.Unregister(backend);
            else GpuBackendProviders.Register(backend, provider);
        }

        public void Dispose()
        {
            if (_previous is null) GpuBackendProviders.Unregister(_backend);
            else GpuBackendProviders.Register(_backend, _previous);
        }
    }
}
