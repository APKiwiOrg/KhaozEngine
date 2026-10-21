using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu;

internal static class CoveragePerfNativeHost
{
    // Read the device's effective selection, never infer it from the current environment request.
    // The narrow property lookup avoids resolving the Windows-only device and its interop off Windows.
    internal static string RecordingMode(IGpuDevice device)
        => device.Backend != GpuBackendKind.Direct3D11Native ? "unavailable"
            : device.GetType().GetProperty("RecordMode", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(device) switch
            {
                D3D11RecordMode.Immediate => "immediate",
                D3D11RecordMode.Deferred => "deferred",
                _ => "unavailable",
            };

    internal static CoveragePerfHost Open(CoveragePerfManifest manifest, CoveragePerfBlock block)
    {
        var context = GpuDeviceContext.CreateHeadless();
        return new(context.GpuDevice, CoveragePerfEquipment.Import, false, context.Dispose);
    }
}
