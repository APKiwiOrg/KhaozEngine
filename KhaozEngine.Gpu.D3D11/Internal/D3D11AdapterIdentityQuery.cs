using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Queries the adapter retained from the created device, including default selection and WARP.</summary>
internal static class D3D11AdapterIdentityQuery
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    internal static D3D11AdapterIdentity ReadWindows(IDXGIAdapter adapter)
        => D3D11AdapterIdentity.Read(() =>
        {
            AdapterDescription description = adapter.Description;
            return (unchecked((uint)description.VendorId), unchecked((uint)description.DeviceId));
        }, () => DriverWindows(adapter));

    // IDXGIDevice is the documented interface for the UMD version query. ID3D11Device would return unsupported.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SupportedOSPlatform("windows")]
    static long? DriverWindows(IDXGIAdapter adapter)
        => adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long version).Success ? version : null;
}
