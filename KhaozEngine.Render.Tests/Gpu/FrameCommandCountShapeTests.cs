using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The permanent regression guard on HOW MANY command-list operations a frame records, and of which kind, for
    /// a fixed synthetic scene.
    /// <para>
    /// Why a count is worth a test. The engine's recurring performance defect is per-command CPU work paid during
    /// recording, and it is invisible to everything else the suite does: a golden image is identical whether the
    /// frame took 12 uploads or 220, and a wall-clock threshold on a shared runner is noise. On a Direct3D11
    /// driver that reports <c>DriverCommandLists=FALSE</c> the runtime emulates command lists by recording every
    /// call into a token stream and replaying it, so the per-call tax lands on every single operation and the
    /// COUNT becomes the cost. Two regressions of exactly this shape have shipped (22 partial uniform writes per
    /// frame, then per-slot skinned UBO writes) and both were caught by a tester weeks later rather than by CI.
    /// </para>
    /// <para>
    /// This runs with NO GPU, on <see cref="FakeGpuDevice"/>, so it is an ordinary <c>[Fact]</c> in the normal
    /// <c>dotnet test</c> suite on every dev machine and on the Linux CI leg, not a <c>[GpuFact]</c> that skips
    /// unless <c>KE_GPU_TESTS</c> is set. There are no timings asserted anywhere here, deliberately. Counts only.
    /// </para>
    /// <para>
    /// WHAT IS AND IS NOT COVERED. The fake reports the least capable device the engine still supports: no
    /// compute, no completion fences, single-sample, shadow maps supported. So the covered passes are the shadow
    /// depth pass, the model/geometry pass, and the fullscreen post chain that runs on that profile. NOT covered:
    /// anything gated on compute (the FFT ocean), the MSAA resolve path, and every pass whose work is driven by
    /// content this scene does not contain (terrain splat, decals, particles, trails, water, Gui). Those are
    /// still only reachable through a real device, under <c>[GpuFact]</c>.
    /// </para>
    /// <para>
    /// WHEN THIS TEST FAILS it is informative, not flaky. The counts below are frozen references, not physics. A
    /// deliberate renderer change that legitimately alters the command stream updates the constants IN THE SAME
    /// COMMIT, and the diff on those numbers is then the reviewable record of what the change cost per frame. An
    /// UNEXPLAINED rise, especially in <see cref="GpuCommandKind.UpdateBuffer"/>, is the regression this exists
    /// to catch.
    /// </para>
    /// </summary>
    public sealed class FrameCommandCountShapeTests
    {
        const int W = 128, H = 96;

        // ---------------------------------------------------------------------------------------------------
        // Frozen expected counts. Update deliberately, with the renderer change that moves them, never to make
        // a red build go green. Measured on the FakeGpuDevice capability profile described in the class remarks.
        // ---------------------------------------------------------------------------------------------------

        // A steady-state frame with shadows OFF: one floor plus one box, both instanced batches of the model
        // pass, then the fullscreen post chain.
        const int UnshadowedUpdateBuffers = 7;
        const int UnshadowedPipelineBinds = 3;
        const int UnshadowedDrawIndexed = 2;
        const int UnshadowedFullscreenDraws = 2;
        const int UnshadowedFramebufferBinds = 3;

        // The same frame with the shadow-map tier on. The DIFFERENCE against the constants above is the shadow
        // depth pass's own cost, which is how this file reaches a per-pass number without markers in the command
        // stream: one extra uniform upload (the cascade block, packed and written once), four extra pipeline
        // binds, four extra indexed draws, one extra framebuffer bind, one extra depth clear.
        const int ShadowedUpdateBuffers = 8;
        const int ShadowedPipelineBinds = 7;
        const int ShadowedResourceSetBinds = 7;
        const int ShadowedVertexBufferBinds = 12;
        const int ShadowedIndexBufferBinds = 6;
        const int ShadowedDrawIndexed = 6;
        const int ShadowedFullscreenDraws = 2;
        const int ShadowedFramebufferBinds = 4;
        const int ShadowedDepthClears = 2;

        // The same shadowed frame with GPU skinning on and one skinned caster added. THREE skinned destinations
        // since #407, each packed and uploaded ONCE for the whole frame: the main slot buffer, the shadow slot
        // buffer (17.20.0 made both once-per-pass rather than once per skinned draw, which is the regression that
        // shipped) and the shared bone palette, which is once per FRAME however many passes read it.
        const int SkinnedUpdateBuffers = 11;
        const int SkinnedPipelineBinds = 11;
        const int SkinnedDrawIndexed = 10;

        // Drawing five DISTINCT meshes instead of one grows the draw count (the scene really is bigger) while
        // leaving the per-frame uniform uploads and pipeline binds untouched. That gap is the whole invariant.
        const int DistinctMeshCount = 5;
        const int FiveDistinctMeshesDrawIndexed = 18;

        static Scene3D NewScene(IGpuDevice gd, IGpuFramebuffer fb, bool shadows, bool gpuSkinning = false)
        {
            var scene = new Scene3D(gd, fb.Outputs);
            scene.Post.Starfield = false;
            scene.Post.Quality.Shadows.Mode = shadows ? ShadowMode.ShadowMap : ShadowMode.Off;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
            scene.UseGpuSkinning = gpuSkinning;
            return scene;
        }

        static IGpuFramebuffer NewTarget(IGpuResourceFactory f)
        {
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            return f.CreateFramebuffer(null, tex);
        }

        /// <summary>
        /// Renders TWO frames of a fixed scene and returns the tally of the SECOND. The first frame primes
        /// (buffers grow to their working size, the shadow atlas is built), and the second is the steady state a
        /// player actually sits in, which is the one worth freezing. Casters move between the frames so the depth
        /// pass is genuinely dirty on frame two: a skipped depth pass would make every shadow number here vacuous,
        /// which the callers assert against explicitly.
        /// </summary>
        static GpuCommandTally SteadyStateFrame(Scene3D scene, IGpuResourceFactory f, IGpuFramebuffer fb,
            System.Action<int> drawFrame)
        {
            using IGpuCommandList sink = f.CreateCommandList();
            var cl = new CommandTallyGpuCommandList(sink);
            for (int frame = 0; frame < 2; frame++)
            {
                scene.Begin();
                drawFrame(frame);
                scene.PrepareFrame();
                cl.Clear();
                cl.Begin();
                scene.RenderInternal(cl, W, H, fb);
                cl.End();
            }
            return cl.Tally;
        }

        [Fact]
        public void An_unshadowed_frame_records_a_frozen_number_of_commands()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb, shadows: false);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.4f));

            GpuCommandTally tally = SteadyStateFrame(scene, gd.Factory, fb, frame =>
            {
                scene.Draw(floor, Matrix4x4.Identity);
                scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f + frame * 0.35f, 0.7f, -0.4f));
            });

            AssertCount(tally, GpuCommandKind.UpdateBuffer, UnshadowedUpdateBuffers);
            AssertCount(tally, GpuCommandKind.SetPipeline, UnshadowedPipelineBinds);
            AssertCount(tally, GpuCommandKind.DrawIndexed, UnshadowedDrawIndexed);
            AssertCount(tally, GpuCommandKind.Draw, UnshadowedFullscreenDraws);
            AssertCount(tally, GpuCommandKind.SetFramebuffer, UnshadowedFramebufferBinds);
        }

        [Fact]
        public void A_shadowed_frame_records_a_frozen_number_of_commands()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb, shadows: true);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.4f));

            GpuCommandTally tally = SteadyStateFrame(scene, gd.Factory, fb, frame =>
            {
                scene.Draw(floor, Matrix4x4.Identity);
                scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f + frame * 0.35f, 0.7f, -0.4f));
            });

            Assert.False(scene.ShadowPassSkippedLastFrame,
                "the asserted frame must have rendered the depth pass, otherwise every shadow count here is vacuous");

            AssertCount(tally, GpuCommandKind.UpdateBuffer, ShadowedUpdateBuffers);
            AssertCount(tally, GpuCommandKind.SetPipeline, ShadowedPipelineBinds);
            AssertCount(tally, GpuCommandKind.SetGraphicsResourceSet, ShadowedResourceSetBinds);
            AssertCount(tally, GpuCommandKind.SetVertexBuffer, ShadowedVertexBufferBinds);
            AssertCount(tally, GpuCommandKind.SetIndexBuffer, ShadowedIndexBufferBinds);
            AssertCount(tally, GpuCommandKind.DrawIndexed, ShadowedDrawIndexed);
            AssertCount(tally, GpuCommandKind.Draw, ShadowedFullscreenDraws);
            AssertCount(tally, GpuCommandKind.SetFramebuffer, ShadowedFramebufferBinds);
            AssertCount(tally, GpuCommandKind.ClearDepthStencil, ShadowedDepthClears);

            // The pass the shadow tier adds, priced by difference against the unshadowed frame above. One extra
            // uniform upload total: the whole cascade block, packed once. If the depth pass ever goes back to a
            // write per cascade this is the number that moves first.
            Assert.Equal(1, ShadowedUpdateBuffers - UnshadowedUpdateBuffers);
        }

        [Fact]
        public void A_gpu_skinned_frame_uploads_its_slot_buffers_once_per_pass()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb, shadows: true, gpuSkinning: true);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.4f));
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
            SkinnedMeshHandle caster = scene.LoadSkinnedMesh(tube);

            GpuCommandTally tally = SteadyStateFrame(scene, gd.Factory, fb, frame =>
            {
                scene.Draw(floor, Matrix4x4.Identity);
                scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f + frame * 0.35f, 0.7f, -0.4f));
                scene.DrawSkinned(caster, tube.RestPose,
                    Matrix4x4.CreateTranslation(frame * 0.05f, 0.6f, 0f), Color.White);
            });

            Assert.False(scene.ShadowPassSkippedLastFrame,
                "a skinned caster must keep the depth pass dirty, otherwise the shadow slot upload is not exercised");

            AssertCount(tally, GpuCommandKind.UpdateBuffer, SkinnedUpdateBuffers);
            AssertCount(tally, GpuCommandKind.SetPipeline, SkinnedPipelineBinds);
            AssertCount(tally, GpuCommandKind.DrawIndexed, SkinnedDrawIndexed);

            // Adding a GPU-skinned caster costs exactly three more uploads for the whole frame: the main slot
            // buffer once, the shadow slot buffer once, and the shared bone palette once. Not per skinned draw,
            // which is the shape 17.20.0 removed, and not per cascade, which is the shape #407 removed. The palette
            // is the only one of the three that a second cascade used to multiply, and it is now the one that
            // cannot: it is written before either pass and read by both.
            Assert.Equal(3, SkinnedUpdateBuffers - ShadowedUpdateBuffers);
        }

        /// <summary>
        /// The invariant that survives renderer churn: per-frame uniform uploads and pipeline binds are a property
        /// of the PASSES, not of the draws inside them. Five distinct meshes issue far more indexed draws than one
        /// does, and must still cost the same uploads and the same pipeline binds. A future change that packs a
        /// uniform per draw, or rebinds a pipeline per mesh, breaks this without needing anyone to have predicted
        /// the exact number it would land on.
        /// </summary>
        [Fact]
        public void Uniform_uploads_and_pipeline_binds_do_not_scale_with_the_draw_count()
        {
            GpuCommandTally one = DistinctMeshFrame(1);
            GpuCommandTally many = DistinctMeshFrame(DistinctMeshCount);

            Assert.Equal(ShadowedUpdateBuffers, one[GpuCommandKind.UpdateBuffer]);
            Assert.Equal(ShadowedUpdateBuffers, many[GpuCommandKind.UpdateBuffer]);
            Assert.Equal(ShadowedPipelineBinds, one[GpuCommandKind.SetPipeline]);
            Assert.Equal(ShadowedPipelineBinds, many[GpuCommandKind.SetPipeline]);

            // Proof the flatness above is not vacuous: the bigger scene really did record more draws.
            Assert.Equal(ShadowedDrawIndexed, one[GpuCommandKind.DrawIndexed]);
            Assert.Equal(FiveDistinctMeshesDrawIndexed, many[GpuCommandKind.DrawIndexed]);
            Assert.True(many[GpuCommandKind.DrawIndexed] > one[GpuCommandKind.DrawIndexed],
                "the many-mesh scene must issue more draws, or this test is asserting nothing");
        }

        /// <summary>
        /// The instancing half of the same idea: eight copies of ONE mesh must record byte-for-byte the same
        /// commands as one copy, because they collapse into a single instanced draw. Any per-instance command
        /// (an upload, a bind, a draw) shows up here immediately.
        /// </summary>
        [Fact]
        public void Extra_instances_of_one_mesh_record_no_extra_commands()
        {
            GpuCommandTally one = InstancedFrame(1);
            GpuCommandTally eight = InstancedFrame(8);

            Assert.Equal(one.ToString(), eight.ToString());
        }

        static GpuCommandTally DistinctMeshFrame(int meshes)
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb, shadows: true);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            var boxes = new MeshHandle[meshes];
            for (int i = 0; i < meshes; i++) boxes[i] = scene.LoadMesh(MeshPrimitives.Box(0.6f + i * 0.05f));

            return SteadyStateFrame(scene, gd.Factory, fb, frame =>
            {
                scene.Draw(floor, Matrix4x4.Identity);
                for (int i = 0; i < meshes; i++)
                    scene.Draw(boxes[i], Matrix4x4.CreateTranslation(-1.5f + i * 0.8f + frame * 0.05f, 0.7f, -0.4f));
            });
        }

        static GpuCommandTally InstancedFrame(int instances)
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb, shadows: true);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1.4f));

            return SteadyStateFrame(scene, gd.Factory, fb, frame =>
            {
                scene.Draw(floor, Matrix4x4.Identity);
                for (int i = 0; i < instances; i++)
                    scene.Draw(box, Matrix4x4.CreateTranslation(-1.2f + i * 0.9f + frame * 0.05f, 0.7f, -0.4f));
            });
        }

        static void AssertCount(GpuCommandTally tally, GpuCommandKind kind, int expected)
        {
            Assert.True(tally[kind] == expected,
                $"expected {expected} {kind} commands this frame, got {tally[kind]}. Whole frame: {tally}. "
                + "If a deliberate renderer change moved this, update the constant in the same commit and say "
                + "what the frame now costs. If nothing was meant to change here, a rise is the per-command "
                + "recording regression this test exists to catch.");
        }
    }
}
