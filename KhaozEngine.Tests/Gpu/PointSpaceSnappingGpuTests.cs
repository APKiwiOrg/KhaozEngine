using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The batch arms device-pixel snapping ONLY inside a point-space UiViewport pass; every other Begin leaves
    // DeviceScale zero so SnapRect/SnapLength/text-origin snapping are no-ops (design/world/screen unchanged).
    // SpriteBatch needs a live device, so this is GPU-gated (skipped unless KE_GPU_TESTS=1).
    public sealed class PointSpaceSnappingGpuTests
    {
        [GpuFact]
        public void DeviceScale_is_the_dpi_only_inside_a_point_space_pass()
        {
            Vector2 uiScale = new(-1, -1), designScale = new(-1, -1), screenScale = new(-1, -1);
            Rect snappedInUi = default, snappedInDesign = default;

            Render2DSnapshot.Capture(64, 64, new Color(0, 0, 0, 1), ctx =>
            {
                var ui = new UiViewport(64, 64, 32, 32);   // 2x DPI over a 32x32 logical UI
                ctx.Batch.Begin(ui);
                uiScale = ctx.Batch.DeviceScale;
                snappedInUi = ctx.Batch.SnapRect(new Rect(10.3f, 10.3f, 20f, 20f));
                ctx.Batch.End();

                var design = new DesignViewport(32, 32, ScaleMode.Fit);
                design.Update(64, 64);                     // fractional design canvas -> must NOT snap
                ctx.Batch.Begin(design);
                designScale = ctx.Batch.DeviceScale;
                snappedInDesign = ctx.Batch.SnapRect(new Rect(10.3f, 10.3f, 20f, 20f));
                ctx.Batch.End();

                ctx.Batch.Begin();                         // screen space -> not snappable
                screenScale = ctx.Batch.DeviceScale;
                ctx.Batch.End();
            });

            Assert.Equal(new Vector2(2, 2), uiScale);
            Assert.Equal(Vector2.Zero, designScale);
            Assert.Equal(Vector2.Zero, screenScale);

            // In the point-space pass the rect left edge snapped to a device pixel (10.3*2=20.6 -> 21 -> /2 = 10.5);
            // in the design pass the rect is returned untouched.
            Assert.Equal(10.5f, snappedInUi.X, 3);
            Assert.Equal(10.3f, snappedInDesign.X, 3);
        }
    }
}
