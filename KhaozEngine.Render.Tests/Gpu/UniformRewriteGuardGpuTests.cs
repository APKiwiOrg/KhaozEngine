using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE STANDING GUARD FROM <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/483">#483</see>: record
    /// ONE frame that reaches every pass with a record-time uniform write in it, and assert that no renderer
    /// rewrote a range of a uniform buffer it had already written with a draw recorded in between and different
    /// bytes. That is the shape the three engine-owned native backends collapse onto the last write, silently, and
    /// the audit's whole finding is that a code sweep of 30-odd call sites is not a thing anyone will redo.
    ///
    /// <para><b>IT WOULD HAVE CAUGHT THE ONE HAZARD THE AUDIT FOUND.</b> A frame that queues BOTH a shadow blob in
    /// Blob mode and a ground decal runs <c>GroundDecalRenderer.Draw</c> twice, and before 17.39.0 the two passes
    /// wrote the same 80 bytes at offset 0 with the blob pass's draws between them, differing in the
    /// dynamic-geometry reject lane. That pair is queued below deliberately, so the guard is not merely watching
    /// for a hypothetical.</para>
    ///
    /// <para><b>THE FRAME IS DELIBERATELY GREEDY.</b> Every queue that gates a pass on being non-empty is filled:
    /// meshes, skinned meshes, decals, blobs, water, particles, distortion, beams, trails in both blends,
    /// billboards in both blends, textured billboards, overlay meshes, depth-tested and always-on-top debug lines,
    /// a filled quad, and the sky. A pass that records nothing cannot be audited, so anything left out of this
    /// list is outside the guard's reach and is covered by the written site table in
    /// <c>docs/design/RECORD-TIME-UNIFORM-REWRITE-AUDIT-2026-08-22.md</c> instead.</para>
    ///
    /// <para><b>WHY IT NEEDS A DEVICE.</b> The renderers build real pipelines and real resource sets, and a frame
    /// recorded against something that does not is not the frame that ships. The audit itself is device-free
    /// arithmetic over the recording (<see cref="UniformRewriteAudit"/>), so what the device buys is a real
    /// frame rather than a real answer.</para>
    ///
    /// <para><b>TWO FRAMES, THE SECOND ASSERTED.</b> The first primes every lazily-created buffer and every
    /// grow-on-demand slot array, so what is asserted is the steady state a player sits in rather than a
    /// first-frame shape nothing repeats.</para>
    /// </summary>
    public sealed class UniformRewriteGuardGpuTests
    {
        const int W = 128, H = 96;

        readonly ITestOutputHelper _out;

        public UniformRewriteGuardGpuTests(ITestOutputHelper output) => _out = output;

        [GpuFact]
        public void No_pass_rewrites_a_uniform_range_it_already_wrote_with_a_draw_in_between()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var tracker = new UniformBufferTrackingGpuDevice(gpu.GpuDevice);
            IGpuResourceFactory f = tracker.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(tracker, finalFB.Outputs);
            Configure(scene);

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(12f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
            Scene3D.TextureHandle sprite = scene.LoadTexture(WhitePixel, 1, 1);

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real) { CapturePayloads = true };

            for (int frame = 0; frame < 2; frame++)
            {
                scene.Begin();
                QueueEverything(scene, floor, box, sprite);
                scene.PrepareFrame();
                rec.Clear();
                rec.Begin();
                scene.RenderInternal(rec, W, H, finalFB);
                rec.End();
                tracker.Submit(real);
                tracker.WaitForIdle();
            }

            // A scan that recognised nothing as a uniform buffer would also come back empty, so both halves of the
            // input are checked before the empty answer is believed.
            Assert.True(tracker.UniformBufferCount > 0,
                "no uniform buffer was created through the tracking device, so the scan had nothing to recognise");
            Assert.True(rec.DrawCount > 0, "the asserted frame recorded no draws at all");

            var uniformUploads = 0;
            foreach (RecordingGpuCommandList.Upload u in rec.Uploads)
                if (tracker.IsUniform(u.Buffer)) uniformUploads++;
            Assert.True(uniformUploads > 10,
                $"the asserted frame recorded only {uniformUploads} uniform uploads, which is too few for this "
                + "frame to be reaching the passes it queues - the guard would pass vacuously");

            _out.WriteLine($"{rec.Uploads.Count} uploads ({uniformUploads} to uniform buffers) across "
                + $"{rec.DrawCount} draws and dispatches, {tracker.UniformBufferCount} uniform buffers live.");

            List<UniformRewriteAudit.Hazard> hazards = UniformRewriteAudit.Scan(rec.Uploads, tracker.IsUniform);

            Assert.True(hazards.Count == 0,
                $"{hazards.Count} record-time uniform rewrite(s) with a draw in between:"
                + UniformRewriteAudit.Describe(hazards)
                + "\nOn the native Direct3D 11, Vulkan and Metal backends a record-time UpdateBuffer to a uniform "
                + "buffer is a memcpy into that frame's ring segment and is NOT ordered against the draws, so every "
                + "draw of the frame reads the LAST value written (RecordTimeUniformRewriteGpuTests measures it). "
                + "Give each pass its own slot and select it with a dynamic offset, the way OverlayMeshRenderer, "
                + "WaterRenderer, SpriteBatch and GroundDecalRenderer do, or re-upload a whole CPU mirror whose "
                + "already-recorded slots keep the bytes they had.");
        }

        static readonly byte[] WhitePixel = { 255, 255, 255, 255 };

        static void Configure(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Background = BackgroundMode.Sky;
            // Blob mode is what puts the EARLY decal pass in the frame beside the main one, which is the pair the
            // audit's one confirmed hazard lived in.
            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
            scene.Post.Outline = true;
            scene.Post.Quantize = true;
            scene.Post.Hdr.Enabled = true;
            scene.Post.Bloom.Enabled = true;
            scene.Camera.Frame(new Vector3(0f, 0.5f, 0f), new Vector3(7f, 5f, 7f));
        }

        // Every queue that gates a pass, filled. The values are arbitrary: nothing here is asserted on as a
        // picture, so what matters is only that each pass records the uploads and draws it would in a real frame.
        static void QueueEverything(Scene3D scene, MeshHandle floor, MeshHandle box, Scene3D.TextureHandle sprite)
        {
            scene.Draw(floor, Matrix4x4.Identity);
            scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f));
            scene.DrawOverlayMesh(box, Matrix4x4.CreateTranslation(1.6f, 0.7f, 1.1f));

            scene.AddShadowBlob(new ShadowBlob(new Vector3(-1.2f, 0f, -0.4f), groundY: 0f, radius: 1.0f));
            scene.DrawGroundDecal(Circle(1.4f, 0.6f, 1.1f));
            scene.DrawGroundDecal(Circle(-2.1f, 1.4f, 0.8f, DecalBlend.Additive));

            scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: -0.2f, centerZ: 3.5f, halfExtentX: 3f));

            scene.DrawParticle(new ParticleSprite
            {
                Position = new Vector3(0.4f, 1.4f, 0.2f), Size = 0.5f, Color = new Color(1f, 0.8f, 0.4f, 1f),
                Shape = ParticleShape.SoftGlow, ShapeParam = 0.3f, LifeNorm = 0.4f, Seed = 0.21f,
                Blend = BillboardBlend.Additive,
            });
            scene.DrawDistortion(new DistortionSprite
            {
                Position = new Vector3(-0.6f, 1.2f, 0.8f), Size = 1.2f,
                Shape = DistortionShape.Ripple, ShapeParam = 0.25f, Strength = 1.8f, Seed = 0.37f,
            });

            scene.DrawBeam(new Vector3(-2.5f, 1.1f, 0f), new Vector3(2.5f, 1.1f, 0f), 0.35f,
                new Color(1f, 0.3f, 0.9f, 1f));

            // BOTH blends, because TrailRenderer records one draw per blend behind a single frame-uniform write and
            // the pair is exactly the shape a per-draw write would have turned into a hazard.
            scene.DrawTrail(Ribbon(0.9f), TrailStyle.Default with { Blend = TrailBlend.Additive });
            scene.DrawTrail(Ribbon(-0.9f), TrailStyle.Default with { Blend = TrailBlend.Alpha });

            // Likewise for the untextured billboards, which share ONE OverlayRenderer instance across the two
            // blends and therefore write its view-projection UBO twice per frame with a draw between them.
            scene.DrawBillboard(new Vector3(2.2f, 1.5f, -1.4f), 0.6f, new Color(1f, 1f, 1f, 1f),
                BillboardBlend.Additive);
            scene.DrawBillboard(new Vector3(-2.2f, 1.5f, 1.4f), 0.6f, new Color(1f, 0.5f, 0.5f, 0.7f),
                BillboardBlend.Alpha);
            scene.DrawBillboard(sprite, new Vector3(0f, 2.1f, 0f), 0.7f, new Color(1f, 1f, 1f, 1f));

            scene.DebugBox(new Vector3(0f, 0.6f, 2.2f), new Vector3(1f, 1f, 1f), new Color(0.2f, 1f, 0.3f, 1f));
            scene.DebugWireSphere(new Vector3(2.4f, 0.8f, 2.0f), 0.7f, new Color(1f, 1f, 0.2f, 1f));
            scene.DebugFilledQuad(new Vector3(-2.4f, 0.05f, -2.0f), Vector3.UnitY, Vector3.UnitX,
                new Vector2(0.8f, 0.8f), new Color(0.3f, 0.4f, 1f, 0.5f));
        }

        static GroundDecal Circle(float cx, float cz, float radius, DecalBlend blend = DecalBlend.Alpha) => new()
        {
            Shape = DecalShape.Circle, Center = new Vector3(cx, 0f, cz),
            Size = new Vector4(radius, 0f, 0f, 0f),
            FillColor = new Color(1f, 0.2f, 0.1f, 0.6f), OutlineColor = new Color(1f, 0.9f, 0.2f, 0.9f),
            EdgeThickness = 0.08f, FillFraction = 1f, Blend = blend,
            YTolerance = 0.3f, MaxStep = 0.4f,
        };

        static TrailSample[] Ribbon(float z) =>
        [
            new(new Vector3(-2f, 1.6f, z), 0.12f, 0.2f),
            new(new Vector3(-1f, 1.7f, z), 0.16f, 0.6f),
            new(new Vector3(0f, 1.8f, z), 0.2f, 1f),
            new(new Vector3(1f, 1.7f, z), 0.16f, 0.6f),
        ];
    }
}
