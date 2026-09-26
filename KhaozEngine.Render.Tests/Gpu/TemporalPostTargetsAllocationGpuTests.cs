using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A STEADY FRAME OF THE DISPLAY POST CHAIN ALLOCATES NOTHING. Each frame ensures the display targets, flips the
    /// history pair, binds, uploads, takes the opaque copy and runs the chain over the history colour, with the
    /// outline, the distortion apply and FXAA reading through the display targets. Only the recording window is
    /// measured, and the submit and drain run outside it, as in <see cref="MetalRecordingAllocationGpuTests"/>.
    /// </summary>
    [Collection("AllocSensitive")]
    public sealed class TemporalPostTargetsAllocationGpuTests(ITestOutputHelper output)
    {
        const int WarmFrames = 6, MeasuredFrames = 24;
        const int DisplayW = 48, DisplayH = 27, InternalW = 32, InternalH = 18;

        [GpuFact]
        public void A_steady_display_chain_frame_allocates_nothing()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using var res = new RenderResources(gd, InternalW, InternalH, hdrColor: true);
            res.EnsureDistortion(true, 2);
            var history = new TemporalHistory();
            using var targets = new TemporalPostTargets(gd);
            using var swapchain = new PostChainTargetsGpuTests.Output(gd, DisplayW, DisplayH);
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            try
            {
                history.EnsureTargets(gd, DisplayW, DisplayH, InternalW, InternalH);
                targets.Ensure(res, history, DisplayW, DisplayH, bloomEnabled: false);
                using var post = new PixelPostProcess(gd, targets.PingAFB.Outputs, swapchain.Framebuffer.Outputs);
                var s = new PixelPostProcessSettings { Outline = true };
                var cam = new CameraDepth(true, 0.1f, 100f);
                long frame = 0;

                Frames(WarmFrames);
                long first = Frames(MeasuredFrames);
                long retry = first == 0 ? 0 : Frames(MeasuredFrames);

                output.WriteLine($"{MeasuredFrames} steady display chain frames: {first} bytes, retry {retry}");
                Assert.True(retry == 0,
                    $"{MeasuredFrames} steady display chain frames allocated {first} bytes and {retry} on the retry");
                Assert.True(frame > 1, "the history pair never flipped, so the second source slot was never read");

                long Frames(int count)
                {
                    long allocated = 0;
                    for (int i = 0; i < count; i++)
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        targets.Ensure(res, history, DisplayW, DisplayH, bloomEnabled: false);
                        history.BeginResolve(frame++);
                        post.BindTargets(targets);
                        using (GpuRecording.Open(gd, cl, nameof(TemporalPostTargetsAllocationGpuTests)))
                        {
                            post.PrepareUniforms(cl, targets, s, cam, runFxaa: true, distortionActive: true);
                            targets.CopyOpaque(cl);
                            post.Run(cl, targets, swapchain.Framebuffer, s, runFxaa: true, distortionActive: true);
                        }
                        allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                        gd.Submit(cl);
                        gd.WaitForIdle();
                    }
                    return allocated;
                }
            }
            finally
            {
                history.ReleaseTargets();
            }
        }
    }
}
