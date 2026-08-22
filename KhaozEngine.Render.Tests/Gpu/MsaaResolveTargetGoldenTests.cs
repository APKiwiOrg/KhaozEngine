using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE INSTRUMENT THE GOLDEN FAMILY IS NOT
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/603">#603</see>): it reads the MSAA resolve
    /// DESTINATIONS back after a real <see cref="Scene3D"/> frame and asserts they carry that frame's geometry.
    ///
    /// <para><b>WHAT WAS MEASURED, AND WHY A GRID COULD NOT SEE IT.</b> <c>Golden3D_HdrMsaa</c> drives the native
    /// resolve path six times in one run, twice with a render encoder already open, which is exactly
    /// <c>RenderResources.ResolveDepthNormal</c>'s back-to-back pair. With the row 14
    /// defect in place the FIRST of that pair is silently discarded, so the single-sample depth target holds
    /// whatever it held before, and all 91 goldens stayed green: a 32x18 grid of per-cell AVERAGE RGB of the FINAL
    /// image moves by less than its own tolerance when one intermediate target is a frame stale. The final image is
    /// the wrong place to look. The destination is the thing that went wrong, so this reads the destination.</para>
    ///
    /// <para><b>THE REFERENCE IS THE ENGINE'S OWN SINGLE-SAMPLE PATH, WHICH IS WHY THERE IS NO COMMITTED GRID AND
    /// NOTHING TO BAKE.</b> At <see cref="AntiAliasing.Off"/> the same two textures ARE the MRT attachments and no
    /// resolve happens at all. So the same scene rendered both ways has to put the same depth and the same normals
    /// in the same two textures, and the MSAA run is checked against a reference the run itself produced, on the
    /// same device, in the same session. That is one path checking the other, the trick the native backends' golden
    /// families were seeded by, and it costs no per-backend bake, no new committed reference and no re-bake when a
    /// shader changes.</para>
    ///
    /// <para><b>IT IS NAMED "Golden" TO RUN EVERYWHERE.</b> The cross-platform matrix selects
    /// <c>FullyQualifiedName~Golden</c> on the push path, so a GPU test outside that substring is verified on one
    /// leg and no other (the contract is on <c>GoldenCompare</c> and in <c>docs/CROSS-PLATFORM.md</c>). This is the
    /// second flavour that contract names: a property assertion on rendered pixels rather than a committed-grid
    /// diff.</para>
    ///
    /// <para><b>AND IT SKIPS RATHER THAN GOING QUIET ON A DEVICE WITHOUT 4x MSAA.</b>
    /// <c>AntiAliasing.ResolveFor</c> downgrades an MSAA request to Fxaa below the device limit, which would leave
    /// this comparing the single-sample path against itself and passing. <c>RequiresFourSampleMsaa</c> is the named
    /// skip for that, and <see cref="Capture.SampleCount"/> is asserted on top of it so a change in the downgrade
    /// policy cannot re-open the hole.</para>
    /// </summary>
    public sealed class MsaaResolveTargetGoldenTests
    {
        const int W = 320, H = 240;

        /// <summary>
        /// How far the MSAA run's target may sit from the single-sample run's, as a fraction of the reference's
        /// OWN mean absolute deviation. Dimensionless on purpose: an absolute epsilon in NDC depth or in encoded
        /// normal units would need re-tuning per scene and per backend, while this asks the only question that
        /// matters, which is whether the two runs agree far better than a constant would.
        /// <para>
        /// MEASURED, NOT GUESSED. On an Apple M-series device the ratio comes out at 0.0003 for the depth target
        /// and 0.0009 for the normal one (the resolved interior is bit-identical and only the silhouette band
        /// averages differently), and a DROPPED resolve scores at or above 1.0, because a target nothing wrote is
        /// exactly the constant this normalises against. The threshold is parked three orders of magnitude above
        /// the measurement and four times below the failure, so a software rasterizer resolving its edges
        /// differently cannot reach it from either side.
        /// </para>
        /// <para>
        /// A DROPPED RESOLVE ACTUALLY READ BACK AS NaN, not as zero, when this was verified by removing the row 14
        /// fix: a Metal private texture nothing has written has undefined contents rather than cleared ones. NaN
        /// fails every ordered comparison, so it lands on the same side as any other wrong answer, which is the
        /// direction that has to hold and is worth writing down because the opposite convention would pass.
        /// </para>
        /// </summary>
        const double MaxRelativeDeviation = 0.25;

        /// <summary>
        /// How much structure the single-sample reference must carry before the comparison above means anything.
        /// A flat reference makes a relative test pass against any input at all, so this is the guard on the
        /// SCENE rather than on the resolve: it fails if a future edit to the fixture below (a camera change, a
        /// clip-plane change, geometry moved out of frame) quietly drains the signal the test measures against.
        /// </summary>
        const double MinStructure = 0.05;

        [GpuFact(RequiresFourSampleMsaa = true)]
        public void Golden3D_MsaaResolvedDepthAndNormalCarryTheSameFrameTheSingleSamplePathDoes()
        {
            // ONE DEVICE FOR BOTH RUNS, which is a cost decision and a correctness one. It roughly halves what this
            // adds to the golden suite (device creation dominates a run this small), and it removes the only way
            // the two captures could differ for a reason other than the resolve: they share the adapter, the
            // pipeline cache and the session.
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            using IGpuTexture finalTex = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)W, (uint)H, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = gd.Factory.CreateFramebuffer(null, finalTex);

            Capture single = CaptureResolveTargets(gd, finalFB, AntiAliasing.Off);
            Capture msaa = CaptureResolveTargets(gd, finalFB, AntiAliasing.Msaa(4));

            Assert.Equal(1, single.SampleCount);
            Assert.True(msaa.SampleCount > 1,
                $"the MSAA run allocated a {msaa.SampleCount}-sample MRT, so no resolve ran and this test "
                + "compared the single-sample path against itself. AntiAliasing.ResolveFor downgraded the "
                + "request, which RequiresFourSampleMsaa is supposed to have skipped on.");
            Assert.Equal(single.Width, msaa.Width);
            Assert.Equal(single.Height, msaa.Height);

            Agreement depth = Agreement.Between(msaa.Depth, single.Depth);
            Agreement normal = Agreement.Between(msaa.Normal, single.Normal);
            // BOTH TARGETS IN EVERY MESSAGE. The pair is resolved back to back and a backend can drop one and land
            // the other, so which of the two moved is the first thing the next reader needs.
            string ctx = $"(depth {depth}; normal {normal}; limit ratio<{MaxRelativeDeviation:0.###}, "
                + $"structure>{MinStructure:0.###})";

            // THE VACUITY GUARD FIRST. A reference with no structure would make the comparison below pass against
            // anything, which is the same "green having measured nothing" failure this whole test exists for.
            Assert.True(depth.Structure > MinStructure && normal.Structure > MinStructure,
                $"the single-sample reference is nearly flat, so this scene asserts nothing about the resolve {ctx}");

            Assert.True(depth.Ratio < MaxRelativeDeviation && normal.Ratio < MaxRelativeDeviation,
                "an MSAA resolve destination does not carry this frame. A resolve that is dropped, reordered "
                + "before its writers, or pointed at another texture leaves the destination holding the previous "
                + "frame or nothing at all, and neither moves the averaged golden grid of the final image. "
                + $"Both runs rendered the same scene on the same device in the same session {ctx}");
        }

        /// <summary>
        /// How well one run's target agrees with the other's: the mean absolute difference, the reference's own
        /// mean absolute deviation, and the ratio of the two. <see cref="Structure"/> is the score a CONSTANT would
        /// earn, which is what a target nothing wrote is, so <see cref="Ratio"/> is "how far off, in units of how
        /// much there was to get wrong".
        /// </summary>
        readonly record struct Agreement(double Difference, double Structure)
        {
            internal double Ratio => Structure > 0d ? Difference / Structure : double.PositiveInfinity;

            internal static Agreement Between(float[] got, float[] want)
            {
                Assert.Equal(want.Length, got.Length);
                return new Agreement(MeanAbsoluteDifference(got, want), MeanAbsoluteDeviation(want));
            }

            public override string ToString()
                => $"meanAbsDiff={Difference:0.######} referenceDeviation={Structure:0.######} ratio={Ratio:0.####}";
        }

        /// <summary>One run's resolve destinations, read back as floats 0..1, plus the sample count the MRT was
        /// actually allocated at.</summary>
        readonly record struct Capture(int SampleCount, int Width, int Height, float[] Depth, float[] Normal);

        /// <summary>
        /// Render the fixed scene at <paramref name="aa"/> and read back BOTH resolve destinations. The frame loop
        /// mirrors <c>Render3DSnapshot.Capture</c> exactly, and is spelled out here rather than reused because that
        /// helper disposes the scene before it returns, which disposes the very textures this needs.
        /// </summary>
        static Capture CaptureResolveTargets(IGpuDevice gd, IGpuFramebuffer finalFB, AntiAliasing aa)
        {
            IGpuResourceFactory f = gd.Factory;
            using var scene = new Scene3D(gd, finalFB.Outputs);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(14f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
            MeshHandle sphere = scene.LoadMesh(MeshPrimitives.Sphere(0.8f));
            scene.Post.Starfield = false;
            scene.Post.BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 1f);
            scene.Post.Quality.AntiAliasing = aa;
            // A tight framing of a big tilted floor: nearly every pixel is geometry, and the depth across it spans
            // a wide range, which is what gives the reference the structure the comparison normalises against.
            scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(4f, 2.4f, 4f));
            // THE CLIP PLANES ARE PULLED IN AROUND THE SCENE ON PURPOSE. The stored value is NDC depth, so with the
            // stock 0.1..200 range an orthographic camera 50 units out puts this whole scene inside a band 0.07
            // wide and the reference is nearly a constant, which is exactly the flat reference MinStructure
            // rejects. Bracketing the eye distance turns the same geometry into most of the 0..1 range.
            scene.Camera.NearPlane = 35f;
            scene.Camera.FarPlane = 65f;
            scene.EffectTimeSeconds = 0f;

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                for (int i = 0; i < 2; i++)
                {
                    // THE TWO FRAMES DIFFER ON PURPOSE: the first box rotates between them. A static pair would
                    // make frame N-1 identical to frame N, and a destination that is exactly one frame stale
                    // would read as correct. With the frames distinct, a stale destination differs from the
                    // reference wherever the rotated box's silhouette moved, so the staleness class the doc
                    // claims is genuinely exercised (a review experiment proved the static pair could not see a
                    // first-frame-only resolve).
                    scene.Begin();
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0f, 0f, 0f),
                        new Color(0.30f, 0.34f, 0.40f, 1f));
                    scene.Draw(box,
                        Matrix4x4.CreateRotationY(0.4f + (i * 0.9f))
                            * Matrix4x4.CreateTranslation(-1.3f, 0.6f, 0.4f),
                        new Color(0.35f, 0.6f, 0.85f, 1f));
                    scene.Draw(box, Matrix4x4.CreateTranslation(1.5f, 0.6f, -1.4f),
                        new Color(0.8f, 0.55f, 0.2f, 1f));
                    scene.Draw(sphere, Matrix4x4.CreateTranslation(0.9f, 0.8f, 1.5f),
                        new Color(0.85f, 0.2f, 0.25f, 1f), Material.Shiny(0.7f));
                    scene.PrepareFrame();
                    cl.Begin();
                    scene.RenderInternal(cl, W, H, finalFB);
                    cl.End();
                    gd.Submit(cl);
                }
            }
            gd.WaitForIdle();

            int tw = scene.RenderTargetWidth, th = scene.RenderTargetHeight;
            float[] depth = ReadLinearDepth(gd, scene.ResolvedDepthTarget, tw, th);
            float[] normal = ReadEncodedNormal(gd, scene.ResolvedNormalTarget, tw, th);
            return new Capture(scene.RenderTargetSampleCount, tw, th, depth, normal);
        }

        /// <summary>
        /// Read an <c>R32Float</c> target back as one float per texel.
        /// <para>
        /// IT IS HERE RATHER THAN IN <see cref="GpuReadback"/> deliberately. That type's two members answer
        /// "give me this render target as an image", which has real callers in both snapshot helpers, and a
        /// float-target read has exactly one caller: this test. Promoting it would add public engine API on the
        /// strength of a single test, which is the direction the package README's own catalogue rots from. The
        /// staging round trip below is the same one <see cref="GpuReadback.ToRgba"/> performs, with the source
        /// format kept rather than assumed, because a whole-texture copy is refused across formats.
        /// </para>
        /// </summary>
        static float[] ReadLinearDepth(IGpuDevice gd, IGpuTexture src, int w, int h)
        {
            IGpuResourceFactory f = gd.Factory;
            using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)w, (uint)h, GpuPixelFormat.R32Float, GpuTextureUsage.Staging));
            CopyForReadback(gd, src, staging);

            var texels = new float[w * h];
            MappedData map = gd.Map(staging, GpuMapMode.Read);
            unsafe
            {
                byte* data = (byte*)map.Data;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        texels[(y * w) + x] = *(float*)(data + (y * (int)map.RowPitch) + (x * 4));
            }
            gd.Unmap(staging);
            return texels;
        }

        /// <summary>Read the encoded-normal target back as its three colour channels, 0..1, alpha dropped: alpha
        /// carries the dynamic-geometry decal mask rather than any part of the normal.</summary>
        static float[] ReadEncodedNormal(IGpuDevice gd, IGpuTexture src, int w, int h)
        {
            byte[] rgba = GpuReadback.ToRgba(gd, src, w, h);
            var channels = new float[w * h * 3];
            for (int p = 0; p < w * h; p++)
            {
                channels[(p * 3) + 0] = rgba[(p * 4) + 0] / 255f;
                channels[(p * 3) + 1] = rgba[(p * 4) + 1] / 255f;
                channels[(p * 3) + 2] = rgba[(p * 4) + 2] / 255f;
            }
            return channels;
        }

        static void CopyForReadback(IGpuDevice gd, IGpuTexture src, IGpuTexture staging)
        {
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            cl.Begin();
            cl.CopyTexture(src, staging);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();
        }

        /// <summary>Mean absolute difference between two equal-length samples.</summary>
        static double MeanAbsoluteDifference(float[] got, float[] want)
        {
            Assert.Equal(want.Length, got.Length);
            double sum = 0d;
            for (int i = 0; i < want.Length; i++) sum += Math.Abs(got[i] - want[i]);
            return sum / want.Length;
        }

        /// <summary>Mean absolute deviation from the mean: how much structure a sample carries, and therefore the
        /// score a constant (a target nothing wrote) earns against it.</summary>
        static double MeanAbsoluteDeviation(float[] values)
        {
            double mean = 0d;
            for (int i = 0; i < values.Length; i++) mean += values[i];
            mean /= values.Length;

            double sum = 0d;
            for (int i = 0; i < values.Length; i++) sum += Math.Abs(values[i] - mean);
            return sum / values.Length;
        }
    }
}
