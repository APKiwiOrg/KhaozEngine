using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/22">#22</see>: a renderer that outgrows a GPU
    /// buffer mid-life RETIRES the old one instead of disposing it inline. The frame path carries no
    /// <c>WaitForIdle</c>, so the CPU can be several frames ahead of the GPU and a prior frame's submitted command
    /// list may still be reading the buffer a grow replaces. Freeing it at the grow is a use-after-free, which is
    /// the rule <c>ModelRenderer.EnsureInstanceCapacity</c> states in the engine's own words and every sibling
    /// renderer with a growing buffer follows.
    ///
    /// <para>The observable is WHEN a buffer is destroyed, not whether: a retired buffer survives the grow and is
    /// freed with the renderer. That is a lifetime question a picture cannot answer and a fake device can, since
    /// the harness records the moment each handle was disposed.</para>
    /// </summary>
    public sealed class GrownBufferRetirementTests
    {
        const int W = 64, H = 32;

        static DistortionSprite Sprite(float x) => new()
        {
            Position = new Vector3(x, 0f, 0f),
            Size = 1f,
            Shape = DistortionShape.Heat,
            Strength = 0.5f,
            LifeNorm = 0.5f,
        };

        /// <summary>
        /// The distortion pass's per-instance buffer. It is reachable today through the unbounded public
        /// <c>Scene3D.DrawDistortion</c> the moment a frame queues more than the initial 64 sprites, which is what
        /// made this the live half of #22.
        /// </summary>
        [Fact]
        public void TheDistortionInstanceBufferSurvivesItsGrowAndIsFreedWithTheRenderer()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            using var res = new RenderResources(device, W, H, hdrColor: false);
            res.EnsureDistortion(wanted: true, divisor: 2);

            var renderer = new DistortionRenderer(device);
            int beforeFirstDraw = factory.Buffers.Count;

            using var cl = new RecordingGpuCommandList(new NullGpuCommandList());
            Draw(renderer, cl, res, sprites: 1);      // allocates the initial 64-sprite buffer
            Assert.Equal(beforeFirstDraw + 1, factory.Buffers.Count);
            FakeBuffer grownOut = factory.Buffers[beforeFirstDraw];

            Draw(renderer, cl, res, sprites: 65);     // outgrows it: 64 -> 128
            Assert.Equal(beforeFirstDraw + 2, factory.Buffers.Count);
            Assert.False(grownOut.Disposed,
                "the replaced instance buffer was freed at the grow, while a prior frame's command list may "
                + "still be reading it");

            renderer.Dispose();
            Assert.True(grownOut.Disposed, "a retired buffer must still be freed when the renderer goes away");
        }

        static void Draw(DistortionRenderer renderer, RecordingGpuCommandList cl, RenderResources res, int sprites)
        {
            var queued = new DistortionSprite[sprites];
            for (int i = 0; i < sprites; i++) queued[i] = Sprite(i);
            renderer.Draw(cl, res, Matrix4x4.Identity, Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
                timeSeconds: 0f, softFade: 0f, DistortionQuality.Full, backgroundDepthMarker: 1f, resRatio: 2f,
                queued);
        }

        /// <summary>
        /// The water pass's per-plane UBO, AND the resource set that ranged over it. The set is the half a fix
        /// aimed only at the buffer would leave behind: it is bound by the same prior frame, so destroying it at
        /// the grow is the same use-after-free one indirection out.
        /// </summary>
        [Fact]
        public void TheWaterSlotUboAndItsSetSurviveTheirGrowAndAreFreedWithTheRenderer()
        {
            using var device = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)device.Factory;
            using var res = new RenderResources(device, W, H, hdrColor: false);

            var renderer = new WaterRenderer(device, res.ColorDepthFB.Outputs);
            int beforeFirstDraw = factory.Buffers.Count;

            using var cl = new RecordingGpuCommandList(new NullGpuCommandList());
            Draw(renderer, cl, res, planes: 1);       // allocates the initial 4-slot UBO and binds a set over it
            FakeBuffer grownOutUbo = factory.Buffers[beforeFirstDraw];
            FakeResourceSet grownOutSet = Assert.Single(factory.ResourceSets);

            Draw(renderer, cl, res, planes: 5);       // outgrows the UBO: 4 -> 8 slots, which drops the set too
            Assert.False(grownOutUbo.Disposed,
                "the replaced slot UBO was freed at the grow, while a prior frame's command list may still be "
                + "reading it");
            Assert.False(grownOutSet.Disposed,
                "the set that ranged over the replaced UBO was freed at the grow, and a prior frame bound it");
            Assert.Equal(2, factory.ResourceSets.Count);   // the grow rebound, it did not reuse

            renderer.Dispose();
            Assert.True(grownOutUbo.Disposed, "a retired buffer must still be freed when the renderer goes away");
            Assert.True(grownOutSet.Disposed, "a retired set must still be freed when the renderer goes away");
        }

        static void Draw(WaterRenderer renderer, RecordingGpuCommandList cl, RenderResources res, int planes)
        {
            var queued = new WaterPlane[planes];
            for (int i = 0; i < planes; i++)
                queued[i] = new WaterPlane(centerX: i * 100f, surfaceY: 0f, centerZ: 0f, halfExtentX: 20f);
            var settings = new WaterSettings();
            var sky = new SkySettings();

            renderer.PrepareFrame(new FramePrepare(settings, queued, timeSeconds: 0f));
            renderer.Draw(cl, res, queued, Matrix4x4.Identity, -Vector3.UnitY, Color.White,
                new Vector3(0f, 10f, 0f), settings, sky, timeSeconds: 0f);
        }
    }
}
