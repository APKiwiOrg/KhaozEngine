using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CascadeStoreIntentGpuTests
{
    // #595. The real cascade owner declares dead depth/stencil, with the old Store behavior as the control.
    [GpuFact]
    public void DiscardingWorkingDepthKeepsTheAtlasAndEveryMainAttachmentIdentical()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        byte[][] stored = PointShadowZeroNdlGpuTests.Render(context.GpuDevice, true, out _, out _, preserveStores: true);
        byte[][] discarded = PointShadowZeroNdlGpuTests.Render(context.GpuDevice, true, out _, out _);
        Assert.Equal(7, stored.Length);
        for (int i = 0; i < stored.Length; i++) Assert.Equal(stored[i], discarded[i]);
    }
}
