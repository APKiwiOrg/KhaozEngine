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
    /// frames that reach every pass with a record-time uniform write in them, and assert that no renderer rewrote
    /// uniform bytes a draw recorded in between had already bound. That is the shape the three engine-owned native
    /// backends collapse onto the last write, silently, and the audit's whole finding is that a code sweep of
    /// 30-odd call sites is not a thing anyone will redo.
    ///
    /// <para><b>IT WOULD HAVE CAUGHT THE ONE HAZARD THE AUDIT FOUND.</b> A frame that queues BOTH a shadow blob in
    /// Blob mode and a ground decal runs <c>GroundDecalRenderer.Draw</c> twice, and before 17.39.0 the two passes
    /// wrote the same 80 bytes at offset 0 with the blob pass's draws between them, differing in the
    /// dynamic-geometry reject lane. That pair is queued below deliberately, so the guard is not merely watching
    /// for a hypothetical.</para>
    ///
    /// <para><b>AND IT MUST NOT CATCH THE SANCTIONED PATTERN.</b> The fix for that hazard, and
    /// <c>SpriteBatch.ViewProj.cs</c> before it, packs one slot of a CPU mirror and uploads the mirror WHOLE. Two
    /// passes of that DO write different bytes to one overlapping range: they differ in the slot the other pass
    /// owns, which no draw recorded before the second write ever binds. So the frames below advance
    /// <see cref="Scene3D.EffectTimeSeconds"/> the way a real host does, which is what makes the two decal uploads
    /// differ, and the guard stays green only because the audit compares bytes inside BOUND WINDOWS rather than
    /// across the whole overlap.</para>
    ///
    /// <para><b>TWO CONFIGURATIONS, BOTH DELIBERATELY GREEDY.</b> Every queue that gates a pass is filled: meshes,
    /// a SKINNED mesh, decals, blobs, water, particles, distortion, beams, trails in both blends, billboards in
    /// both blends, textured billboards, overlay meshes, depth-tested and always-on-top debug lines, a filled quad,
    /// and a background. The first configuration takes the blob-shadow tier over a sky, which is the pair the one
    /// confirmed hazard lived in. The second takes the shadow-map tier over a starfield with GPU skinning on,
    /// which is the only way to reach the cascade light UBO, both skinned per-draw slot buffers and the starfield
    /// UBO at all. A pass that records nothing cannot be audited, so anything still outside these two frames is
    /// listed in <c>docs/design/RECORD-TIME-UNIFORM-REWRITE-AUDIT-2026-08-22.md</c> section 4 instead.</para>
    ///
    /// <para><b>WHY IT NEEDS A DEVICE.</b> The renderers build real pipelines and real resource sets, and a frame
    /// recorded against something that does not is not the frame that ships. The audit itself is device-free
    /// arithmetic over the recording (<see cref="UniformRewriteAudit"/>, pinned both ways with no device at all by
    /// <see cref="UniformRewriteAuditTests"/>), so what the device buys is a real frame rather than a real
    /// answer.</para>
    ///
    /// <para><b>EACH CONFIGURATION PRIMES, THEN ASSERTS.</b> The first frame creates every lazily-created buffer
    /// and grows every slot array, so what is asserted is the steady state a player sits in rather than a
    /// first-frame shape nothing repeats.</para>
    /// </summary>
    public sealed class UniformRewriteGuardGpuTests
    {
        const int W = 128, H = 96;

        readonly ITestOutputHelper _out;

        public UniformRewriteGuardGpuTests(ITestOutputHelper output) => _out = output;

        [GpuFact]
        public void No_pass_rewrites_uniform_bytes_a_draw_between_the_two_writes_had_bound()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var tracker = new UniformBufferTrackingGpuDevice(gpu.GpuDevice);
            IGpuResourceFactory f = tracker.Factory;

            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(tracker, finalFB.Outputs);
            scene.Camera.Frame(new Vector3(0f, 0.5f, 0f), new Vector3(7f, 5f, 7f));

            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(12f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
            SkinnedGltfMesh tube = Tube();
            SkinnedMeshHandle skinned = scene.LoadSkinnedMesh(tube);
            Scene3D.TextureHandle sprite = scene.LoadTexture(WhitePixel, 1, 1);

            using IGpuCommandList real = f.CreateCommandList();
            var rec = new RecordingGpuCommandList(real)
            {
                CapturePayloads = true,
                UniformWindowsOfSet = tracker.WindowsOf,
            };

            // ONE clock across every frame of both configurations, the way a host's total-seconds is. A decal
            // pass's slot therefore carries a different time from the other pass's, which is exactly the
            // whole-mirror difference a range-wide comparison would misread as a collapse.
            int tick = 0;
            var everWritten = new HashSet<IGpuBuffer>();
            foreach ((string Name, Action<Scene3D> Configure) config in Configurations)
            {
                config.Configure(scene);
                for (int frame = 0; frame < 2; frame++)
                {
                    scene.EffectTimeSeconds = ++tick * 0.5f;
                    scene.Begin();
                    QueueEverything(scene, floor, box, skinned, tube.RestPose, sprite);
                    scene.PrepareFrame();
                    rec.Clear();
                    rec.Begin();
                    scene.RenderInternal(rec, W, H, finalFB);
                    rec.End();
                    tracker.Submit(real);
                    tracker.WaitForIdle();
                }

                AssertClean(config.Name, rec, tracker, everWritten);
            }

            // The union, which is the number the design doc's blind-spot section quotes. Printed rather than
            // asserted: a buffer count moves with any renderer change, and a stale assertion here would fail for
            // reasons that have nothing to do with a rewrite.
            _out.WriteLine($"across both configurations the guard writes {everWritten.Count} of the "
                + $"{tracker.UniformBufferCount} uniform buffers this scene ever created.");
        }

        // The two frames, in the order they are rendered. Each is a full configuration: whatever the previous one
        // set is either overwritten here or deliberately kept.
        static readonly (string Name, Action<Scene3D> Configure)[] Configurations =
        [
            ("blob shadows over a sky", BlobShadowsOverSky),
            ("shadow map over a starfield, GPU skinning on", ShadowMapOverStarfield),
        ];

        static void BlobShadowsOverSky(Scene3D scene)
        {
            scene.Post.Background = BackgroundMode.Sky;
            // Blob mode is what puts the EARLY decal pass in the frame beside the main one, which is the pair the
            // audit's one confirmed hazard lived in.
            scene.Post.Quality.Shadows.Mode = ShadowMode.Blob;
            scene.Post.Outline = true;
            scene.Post.Quantize = true;
            scene.Post.Hdr.Enabled = true;
            scene.Post.Bloom.Enabled = true;
            scene.UseGpuSkinning = false;
        }

        static void ShadowMapOverStarfield(Scene3D scene)
        {
            // The cascade light UBO and the starfield UBO have no other way in, and the two skinned per-draw slot
            // buffers (the main one and the shadow one) exist only on the opt-in GPU skinning path.
            scene.Post.Background = BackgroundMode.Starfield;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.UseGpuSkinning = true;
        }

        // Everything the guard checks about its own inputs before it believes an empty hazard list, then the scan.
        void AssertClean(string config, RecordingGpuCommandList rec, UniformBufferTrackingGpuDevice tracker,
            HashSet<IGpuBuffer> everWritten)
        {
            Assert.True(tracker.UniformBufferCount > 0,
                $"[{config}] no uniform buffer was created through the tracking device, so the scan had nothing "
                + "to recognise");
            Assert.True(rec.DrawCount > 0, $"[{config}] the asserted frame recorded no draws at all");
            Assert.Equal(0, tracker.UnresolvedResourceSets);

            var written = new HashSet<IGpuBuffer>();
            var uniformUploads = 0;
            foreach (RecordingGpuCommandList.Upload u in rec.Uploads)
                if (tracker.IsUniform(u.Buffer)) { uniformUploads++; written.Add(u.Buffer); everWritten.Add(u.Buffer); }
            Assert.True(uniformUploads > 10,
                $"[{config}] the asserted frame recorded only {uniformUploads} uniform uploads, which is too few "
                + "for this frame to be reaching the passes it queues - the guard would pass vacuously");

            // THE VACUITY TRAP THE WINDOW RULE OPENS. With no recorded reads every rewrite falls outside every
            // window and the scan comes back empty whatever the frame did, so the buffers that are actually
            // rewritten have to be shown to be bound by something.
            var read = new HashSet<IGpuBuffer>();
            foreach (RecordingGpuCommandList.BoundRead r in rec.Reads) read.Add(r.Buffer);
            Assert.True(rec.Reads.Count > 0,
                $"[{config}] no draw recorded a bound uniform window, so the audit's window rule had nothing to "
                + "compare against and would report any rewrite as safe");
            var rewrittenAndRead = 0;
            foreach (IGpuBuffer b in Rewritten(rec, tracker)) if (read.Contains(b)) rewrittenAndRead++;
            Assert.True(rewrittenAndRead > 0,
                $"[{config}] no uniform buffer that the frame wrote more than once was ever bound by a draw, so "
                + "the window rule is not being exercised by this frame at all");

            _out.WriteLine($"[{config}] {rec.Uploads.Count} uploads ({uniformUploads} to {written.Count} of "
                + $"{tracker.UniformBufferCount} uniform buffers) across {rec.DrawCount} draws and dispatches, "
                + $"{rec.Reads.Count} bound uniform windows over {read.Count} buffers, {rewrittenAndRead} buffer(s) "
                + "both rewritten and bound.");

            List<UniformRewriteAudit.Hazard> hazards =
                UniformRewriteAudit.Scan(rec.Uploads, tracker.IsUniform, rec.Reads);

            Assert.True(hazards.Count == 0,
                $"[{config}] {hazards.Count} record-time uniform rewrite(s) whose changed bytes a draw in between "
                + "had bound:" + UniformRewriteAudit.Describe(hazards)
                + "\nOn the native Direct3D 11, Vulkan and Metal backends a record-time UpdateBuffer to a uniform "
                + "buffer is a memcpy into that frame's ring segment and is NOT ordered against the draws, so every "
                + "draw of the frame reads the LAST value written (RecordTimeUniformRewriteGpuTests measures it). "
                + "Give each pass its own slot and select it with a dynamic offset, the way OverlayMeshRenderer, "
                + "WaterRenderer, SpriteBatch and GroundDecalRenderer do, or re-upload a whole CPU mirror whose "
                + "already-recorded slots keep the bytes they had.");
        }

        // The uniform buffers this recording wrote more than once, which are the only ones the window rule can
        // have an opinion about.
        static IEnumerable<IGpuBuffer> Rewritten(RecordingGpuCommandList rec, UniformBufferTrackingGpuDevice tracker)
        {
            var seen = new Dictionary<IGpuBuffer, int>();
            foreach (RecordingGpuCommandList.Upload u in rec.Uploads)
            {
                if (!tracker.IsUniform(u.Buffer)) continue;
                seen.TryGetValue(u.Buffer, out int n);
                seen[u.Buffer] = n + 1;
            }

            foreach (KeyValuePair<IGpuBuffer, int> kv in seen) if (kv.Value > 1) yield return kv.Key;
        }

        static readonly byte[] WhitePixel = { 255, 255, 255, 255 };

        // A short skinned tube, the shape SkinnedFrustumCullingGpuTests and Render3DGpuSkinningGpuTests already
        // use. Queued at rest, because what is audited is the uploads a skinned draw records and not its pose.
        static SkinnedGltfMesh Tube() => SkinnedMeshBuilder.BuildTube(0.35f, 1.6f, 6, 8, 3, Axis.Y);

        // Every queue that gates a pass, filled. The values are arbitrary: nothing here is asserted on as a
        // picture, so what matters is only that each pass records the uploads and draws it would in a real frame.
        static void QueueEverything(Scene3D scene, MeshHandle floor, MeshHandle box, SkinnedMeshHandle skinned,
            Matrix4x4[] restPose, Scene3D.TextureHandle sprite)
        {
            scene.Draw(floor, Matrix4x4.Identity);
            scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f, 0.7f, -0.4f));
            scene.DrawOverlayMesh(box, Matrix4x4.CreateTranslation(1.6f, 0.7f, 1.1f));

            // The skinned draw is what the blob-vs-main decal split exists for: the blob pass runs BEFORE it, on a
            // depth-only resolve, which is why the two passes disagree about the dynamic reject at all. It is also
            // the only thing that fills the skinned per-draw slot buffers, the other home of the whole-mirror
            // pattern the window rule has to keep green.
            scene.DrawSkinned(skinned, restPose, Matrix4x4.CreateTranslation(0.2f, 0.8f, -1.6f),
                new Color(0.2f, 0.8f, 0.35f, 1f));

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
