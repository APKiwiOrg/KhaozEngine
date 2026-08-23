using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Regression for the two teardown-order crashes (a resource wrapper outliving its GpuDeviceContext):
    // mode 3, the wrapper's drain hit a destroyed device (the loader aborts vkQueueWaitIdle,
    // VUID-vkQueueWaitIdle-queue-parameter), and mode 4, the wrapper's Dispose forwarded to the Veldrid
    // resource destroy against the destroyed device (the loader aborts vkDestroyImage,
    // VUID-vkDestroyImage-device-parameter). GpuDeviceContext.Dispose now flips a shared DeviceLiveness
    // token inside the lifecycle gate: WaitForIdle after device death is a safe no-op, and every
    // resource wrapper skips its underlying destroy once the device is dead (device destruction already
    // freed all child objects). The spy sits ABOVE the latch, so it records that the drain was attempted
    // while the latch keeps it from reaching the dead device.
    public sealed class DeviceDisposedLatchTests
    {
        static readonly byte[] Pixel = new byte[] { 255, 255, 255, 255 };   // 1x1 RGBA8

        [GpuFact]
        public void TextureDisposedAfterDeviceContext_DrainIsALatchedNoOp()
        {
            GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var spy = new SpyGpuDevice(gpu.GpuDevice);
            IGpuTexture handle = spy.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            spy.UpdateTexture(handle, Pixel, 0, 0, 1, 1);
            var tex = new Texture2D(spy, handle, 1, 1, ownsHandle: true);

            gpu.Dispose();                       // the device dies first: the teardown-order hazard
            int before = spy.WaitForIdleCalls;

            tex.Dispose();                       // must not throw: the drain reaches the wrapper and no-ops

            Assert.Equal(before + 1, spy.WaitForIdleCalls);
        }

        [GpuFact]
        public void WaitForIdle_AfterDeviceContextDisposed_IsANoOp()
        {
            GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            gpu.Dispose();
            gd.WaitForIdle();   // must not throw or abort (latched inside the wrapper)
        }

        [GpuFact]
        public void BufferDisposedAfterDeviceContext_IsANoOp()
        {
            GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuBuffer buffer = gpu.GpuDevice.Factory.CreateBuffer(
                new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));
            gpu.Dispose();
            buffer.Dispose();   // must not throw: the wrapper skips destroying a child of the dead device
        }
    }
}
