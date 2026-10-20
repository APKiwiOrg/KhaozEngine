using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

internal static class CoveragePerfNativeHost
{
    internal static CoveragePerfHost Open(CoveragePerfManifest manifest, CoveragePerfBlock block)
    {
        var context = GpuDeviceContext.CreateHeadless();
        return new(context.GpuDevice, CoveragePerfEquipment.Import, false, context.Dispose);
    }
}
