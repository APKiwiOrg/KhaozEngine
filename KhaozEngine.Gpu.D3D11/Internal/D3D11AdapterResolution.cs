using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.Direct3D;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION G2 ON A LIVE FACTORY: the policy's choice, and the adapter object it names. Shared by
    /// <see cref="D3D11GpuDevice"/> and <see cref="D3D11FeatureProbe"/>, because the probe exists to answer for the
    /// adapter the device will actually run on, and two copies of this resolution are how those two would drift.
    /// <para>
    /// Everything here is Windows glue over <see cref="D3D11AdapterSelection"/>, which holds every rule and runs on
    /// macOS. The one thing the policy cannot see is decided here: a high-performance adapter DXGI will not hand
    /// over falls back to DXGI's own pick.
    /// </para>
    /// <para>
    /// NOTHING HERE LOGS. Each member returns its warning, so the device logs it once and the probe, which runs
    /// before the device and answers a settings screen, does not put every adapter warning in the log twice.
    /// There are no fields, so loading this type off Windows resolves nothing from the interop.
    /// </para>
    /// </summary>
    internal static class D3D11AdapterResolution
    {
        /// <summary>
        /// The choice for this machine: <c>KE_D3D11_ADAPTER</c> from the environment, decided over the factory's
        /// enumeration and over whether it offers <c>IDXGIFactory6</c>. <paramref name="warning"/> is the policy's
        /// own, set when an explicit request could not be honoured.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static D3D11AdapterChoice ChooseWindows(IDXGIFactory1 factory,
            out IReadOnlyList<D3D11AdapterInfo> adapters, out string? warning)
        {
            adapters = D3D11DxgiQueries.DescribeAdaptersWindows(factory);
            return D3D11AdapterSelection.Choose(D3D11AdapterSelection.FromEnvironment(), adapters,
                D3D11DxgiQueries.SupportsGpuPreferenceWindows(factory), out warning);
        }

        /// <summary>
        /// The adapter object <paramref name="choice"/> names, or null for DXGI's own pick and for WARP, which are
        /// reached through the driver type rather than through an adapter. Null with <paramref name="warning"/>
        /// set when the named adapter could not be fetched, which falls back to DXGI's own pick exactly as every
        /// unsatisfiable request does. The caller owns the adapter and releases it.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        internal static IDXGIAdapter1? AdapterForWindows(IDXGIFactory1 factory, in D3D11AdapterChoice choice,
            out string? warning)
        {
            warning = null;
            switch (choice.Kind)
            {
                case D3D11AdapterChoiceKind.Enumerated:
                {
                    // Re-fetched at its index because the enumeration handed the policy plain descriptions and
                    // released its own objects. An adapter can be removed between the two, which is not a fault.
                    SharpGen.Runtime.Result result = factory.EnumAdapters1(choice.Index, out IDXGIAdapter1? adapter);
                    if (result.Success && adapter is not null) return adapter;

                    adapter?.Dispose();
                    warning = $"Adapter {choice.Index} was enumerated a moment ago and is no longer there, so "
                        + $"{D3D11AdapterSelection.EnvVarName} could not be honoured after all. Letting DXGI pick.";
                    return null;
                }

                case D3D11AdapterChoiceKind.HighPerformance:
                {
                    IDXGIAdapter1? preferred = D3D11DxgiQueries.HighPerformanceAdapterWindows(factory);
                    if (preferred is null) warning = D3D11AdapterSelection.HighPerformanceUnavailableWarning;
                    return preferred;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// The driver type that goes with <paramref name="adapter"/>. Direct3D requires <c>DriverType.Unknown</c>
        /// when an adapter is supplied and refuses one alongside <c>Hardware</c> or <c>Warp</c>, so the two halves
        /// are one decision rather than two arguments a caller could pair up wrongly. A choice whose adapter could
        /// not be fetched lands on <c>Hardware</c> with a null adapter, which is DXGI's own pick.
        /// </summary>
        [SupportedOSPlatform("windows")]
        internal static DriverType DriverTypeFor(in D3D11AdapterChoice choice, IDXGIAdapter1? adapter)
        {
            if (adapter is not null) return DriverType.Unknown;
            return choice.Kind == D3D11AdapterChoiceKind.WarpDriver ? DriverType.Warp : DriverType.Hardware;
        }
    }
}
