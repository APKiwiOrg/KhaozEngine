using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The claim a headless test cannot make: that swapping <see cref="WaterSettings.Bathymetry"/> for another
    /// field of the SAME resolution actually changes the picture the shaders draw.
    /// <para>
    /// <b>Why it needs one scene rather than three</b>
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/645">#645</see>). Every water capture in this
    /// assembly used to build its own <see cref="Scene3D"/>, so every capture also got a fresh depth texture and
    /// the FIRST field a map ever saw is the only one it was ever asked to upload. That is what hid the defect for
    /// four releases: <c>WaterBathymetryMap</c> compared <see cref="WaterBathymetry.Revision"/>, a PER-INSTANCE
    /// counter, across DIFFERENT fields, and a replacement that did not change the resolution was ignored. The
    /// #639 lane found it by trying to share a scene, and measured the shoaled capture coming back byte-identical
    /// to the deep one it should have replaced. This renders that sequence deliberately.
    /// </para>
    /// <para>
    /// <b>Statistical, and no golden.</b> Both claims are made by comparing two renders of the SAME scene that
    /// differ in one assignment, so nothing here depends on a baked reference, on the camera, or on where the
    /// shore lands in the frame. The deep field is the control the class next door already leans on: 400 m of
    /// water everywhere puts <c>tanh(k d)</c> at 1 and the break line far below the seabed, so it draws the
    /// no-field surface and the sloped field has to move it.
    /// </para>
    /// <para>
    /// <b>What it measures on Metal.</b> Before the fix the swap moved the render by 0.00000 mean and 0.00000
    /// worst: byte-identical, the #639 lane's number. After it, 0.01428 mean and 0.29195 worst against a golden
    /// tolerance of 0.06. Swapping back to the deep field returns to the FIRST deep capture at 0.00000 on both,
    /// which is asserted against the tolerance rather than byte-for-byte because two software rasterizers also
    /// have to pass it.
    /// </para>
    /// </summary>
    public sealed class WaterBathymetrySwapGpuTests
    {
        const int W = 480, H = 320;
        const int Frames = 2;

        // The beach WaterShoreGpuTests measured its differences against: ground at -4 at the origin, rising 0.12
        // per metre, so the water is 4 m deep at x = 0 and dry from x = 33 on.
        const float GroundAtOrigin = -4f;
        const float Slope = 0.12f;
        const float PlaneHalfExtent = 70f;
        const int BeachTiles = 26;
        const float BeachTileSize = 8f;
        const int FieldResolution = 128;

        static float GroundY(float x) => GroundAtOrigin + Slope * x;

        static WaterBathymetry SlopedField()
        {
            var field = new WaterBathymetry(FieldResolution, centerX: 0f, centerZ: 0f, halfExtentX: PlaneHalfExtent);
            field.FillFromGround((x, _) => GroundY(x), surfaceY: 0f);
            return field;
        }

        static WaterBathymetry UniformlyDeepField()
        {
            var field = new WaterBathymetry(FieldResolution, centerX: 0f, centerZ: 0f, halfExtentX: PlaneHalfExtent);
            Array.Fill(field.Depths, 400f);
            field.MarkChanged();
            return field;
        }

        [GpuFact]
        public void ReplacingTheDepthFieldOnOneSceneRendersTheNewShore()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            Assert.True(gd.Capabilities.SupportsCompute,
                $"{gd.Backend} reports no compute support, so the surface would fall back to the procedural path, " +
                "where the whole depth-driven group is inert by design and this would pass vacuously");

            IGpuResourceFactory factory = gd.Factory;
            using IGpuTexture target = factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)W, (uint)H, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer framebuffer = factory.CreateFramebuffer(null, target);
            // Declared so the reverse-order dispose runs commands, scene, framebuffer, target, device: the order
            // OceanFocusScene tears its own down in, and only ever after a capture's WaitForIdle.
            using var scene = new Scene3D(gd, framebuffer.Outputs, null);
            using IGpuCommandList commands = factory.CreateCommandList();
            MeshHandle tile = Setup(scene);

            WaterBathymetry deep = UniformlyDeepField();
            WaterBathymetry sloped = SlopedField();
            // The exact trap, stated before it is rendered: same resolution, same revision, different depths.
            Assert.Equal(deep.Resolution, sloped.Resolution);
            Assert.Equal(deep.Revision, sloped.Revision);

            float[] first = Capture(gd, scene, commands, framebuffer, target, tile, deep);
            float[] swapped = Capture(gd, scene, commands, framebuffer, target, tile, sloped);
            float[] back = Capture(gd, scene, commands, framebuffer, target, tile, deep);

            (float Mean, float Worst) moved = Difference(first, swapped);
            Assert.True(moved.Worst > GoldenCompare.Tolerance,
                $"assigning a sloped depth field over a uniformly deep one of the same resolution moved the render " +
                $"by {moved.Worst:F5} at worst (mean {moved.Mean:F5}), which is inside the golden tolerance of " +
                $"{GoldenCompare.Tolerance:F2}. The shallows should be calming and the surf breaking on them, so " +
                "this is the previous field's depths still sitting on the GPU (#645).");

            (float Mean, float Worst) restored = Difference(first, back);
            Assert.True(restored.Worst < GoldenCompare.Tolerance,
                $"swapping back to the deep field left the render {restored.Worst:F5} from the first deep capture " +
                $"(mean {restored.Mean:F5}). The same field through the same scene has to draw the same sea, so " +
                "either the second swap never uploaded or the scene is carrying state across a configuration " +
                "change.");
            Assert.True(restored.Worst < moved.Worst,
                $"the round trip back to the deep field ({restored.Worst:F5}) moved the picture at least as much " +
                $"as the swap to the sloped one did ({moved.Worst:F5}), so the difference being measured is not " +
                "the depth field.");
        }

        /// <summary>Assign <paramref name="field"/> and render the shared scene, exactly as a game swapping a
        /// streamed depth field would: nothing is rebuilt, nothing is marked changed, the property is set.</summary>
        static float[] Capture(IGpuDevice gd, Scene3D scene, IGpuCommandList commands, IGpuFramebuffer framebuffer,
            IGpuTexture target, MeshHandle tile, WaterBathymetry field)
        {
            scene.Post.Water.Bathymetry = field;
            for (int i = 0; i < Frames; i++)
            {
                scene.Begin();
                DrawFrame(scene, tile);
                // PrepareFrame between the queue being filled and the frame's list being opened, the ordering
                // Render3DSnapshot.Capture uses and the one D3D11's immediate-context mode requires (#423).
                scene.PrepareFrame();
                using (GpuRecording.Open(gd, commands, "WaterBathymetrySwapGpuTests.Capture"))
                    scene.RenderInternal(commands, W, H, framebuffer);
                gd.Submit(commands);
            }
            gd.WaitForIdle();
            return GoldenCompare.Downsample(GpuReadback.ToRgba(gd, target, W, H), W, H);
        }

        static MeshHandle Setup(Scene3D scene)
        {
            MeshHandle tile = scene.LoadMesh(MeshPrimitives.Tile(BeachTileSize, 1f));
            scene.Post.Starfield = false;
            scene.Post.Sky.Enabled = true;
            scene.Post.Sky.Anchor = SunAnchor.StylizedBackdrop;
            scene.Post.LightDirection = new Vector3(-0.45f, -0.75f, -0.4f);
            scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
            WaterSeaState sea = scene.Post.Water.SeaState;
            sea.Seed = 20260728;
            sea.CascadeCount = 2;
            sea.CascadeResolution = 64;
            scene.Post.Water.SeaState = sea;
            scene.Camera.Frame(Vector3.Zero, new Vector3(46f, 30f, 46f));
            // Frozen: every capture is the same instant of wave time, so the only thing that can move the picture
            // is the depth field.
            scene.EffectTimeSeconds = 3f;
            return tile;
        }

        static void DrawFrame(Scene3D scene, MeshHandle tile)
        {
            float angle = MathF.Atan(Slope);
            for (int gz = 0; gz < BeachTiles; gz++)
            {
                for (int gx = 0; gx < BeachTiles; gx++)
                {
                    float x = (gx - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                    float z = (gz - (BeachTiles - 1) * 0.5f) * BeachTileSize;
                    scene.Draw(tile, Matrix4x4.CreateRotationZ(angle) * Matrix4x4.CreateTranslation(x, GroundY(x), z),
                        new Color(0.42f, 0.38f, 0.30f, 1f));
                }
            }
            scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: PlaneHalfExtent));
        }

        static (float Mean, float Worst) Difference(float[] a, float[] b)
        {
            double sum = 0;
            float worst = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                float d = MathF.Abs(a[i] - b[i]);
                sum += d;
                worst = MathF.Max(worst, d);
            }
            return ((float)(sum / a.Length), worst);
        }
    }
}
