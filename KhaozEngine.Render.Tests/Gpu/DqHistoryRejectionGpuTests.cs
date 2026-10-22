using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Render3D;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class DqHistoryRejectionGpuTests(DqHistoryRejectionScene fixture, ITestOutputHelper output)
    : IClassFixture<DqHistoryRejectionScene>
{
    [GpuFact]
    public void Replaced_actor_rejects_history_without_resetting_the_other_actor()
    {
        TemporalFixture fx = fixture.Fixture;
        output.WriteLine($"backend={fx.Device.Backend} adapter={fx.Device.Capabilities.DeviceName}");
        foreach (bool gpu in new[] { true, false })
        foreach (string scenario in new[] { "source", "method", "method-back", "absent", "camera-origin" })
        {
            Scene3D scene = fx.Scene;
            scene.UseGpuSkinning = gpu;
            scene.RenderOrigin = Vector3.Zero;
            scene.Camera.Target = Vector3.Zero;
            scene.CameraCut();
            SkinnedGltfMesh mesh = DqMotionWitness.Patch(scenario == "method-back"
                ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
            SkinnedMeshHandle a = scene.LoadSkinnedMesh(mesh), b = scene.LoadSkinnedMesh(mesh);
            fx.Frame((s, _) =>
            {
                if (scenario != "absent") Queue(a, mesh, 1, -2);
                Queue(b, mesh, 2, 3);
            });
            scene.UnloadSkinnedMesh(a);
            SkinnedGltfMesh replacement = DqMotionWitness.Patch(scenario == "method"
                ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
            SkinnedMeshHandle next = scene.LoadSkinnedMesh(replacement);
            Assert.Equal(a.Index, next.Index);
            Assert.NotEqual(a.Generation, next.Generation);
            if (scenario == "camera-origin")
            {
                scene.Camera.Target += new Vector3(.125f, 0, 0);
                scene.RenderOrigin = new Vector3(128, 0, 128);
            }
            fx.Frame((s, _) => { Queue(next, replacement, 1, 0); Queue(b, mesh, 2, 3); });
            Assert.NotNull(scene.PreviousFrameView);
            IGpuTexture? mask = RejectionWitness.Texture(RejectionWitness.SceneMask(scene));
            byte[] pixels = ReadMask(fx.Device, mask, 128, 64);
            (int X, int Y) old = Site(-2), current = Site(0), other = Site(3);
            if (scenario != "absent")
                Assert.True(pixels[old.Y * 128 + old.X] == 255,
                    $"No rejection region at old actor A. Actual={pixels[old.Y * 128 + old.X]}, expected=255.");
            Assert.Equal(255, pixels[current.Y * 128 + current.X]);
            Assert.Equal(0, pixels[other.Y * 128 + other.X]);
            foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
            foreach (int scale in new[] { 1, 2 })
            {
                float[] state = Resolve(fx.Device, mask, entry, scale);
                // Upscaling's current sample weight depends on its reconstruction kernel. Read the independent
                // no-history control, then require zero extra samples at A and eight retained samples at B.
                float[] seeded = Resolve(fx.Device, null, entry, scale, historyValid: false);
                if (scenario != "absent") AssertConfidence(state, old, scale, Confidence(seeded, old, scale));
                AssertConfidence(state, current, scale, Confidence(seeded, current, scale));
                AssertConfidence(state, other, scale, Confidence(seeded, other, scale) + 8f / 15f);
            }
            byte[] retained = ReadMask(fx.Device, mask, 128, 64);
            int read = scene.TemporalHistory.ReadIndex, write = scene.TemporalHistory.WriteIndex;
            fx.RenderSecond(96, 48);
            Assert.False(scene.ResolvedLastRenderForTests);
            Assert.Equal(read, scene.TemporalHistory.ReadIndex);
            Assert.Equal(write, scene.TemporalHistory.WriteIndex);
            Assert.Equal(retained, ReadMask(fx.Device, mask, 128, 64));
            fx.Frame((s, _) => { Queue(next, replacement, 1, 0); Queue(b, mesh, 2, 3); });
            IGpuTexture? zeroSelection = RejectionWitness.Texture(RejectionWitness.SceneMask(scene));
            Assert.Null(zeroSelection);
            Assert.Equal(retained, ReadMask(fx.Device, mask, 128, 64));
            foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
            {
                float[] warm = Resolve(fx.Device, zeroSelection, entry, 1);
                AssertConfidence(warm, current, 1, 9f / 15f);
                AssertConfidence(warm, other, 1, 9f / 15f);
                float[] defaultMask = Resolve(fx.Device, null, entry, 1);
                AssertConfidence(defaultMask, current, 1, 9f / 15f);
            }
            scene.UnloadSkinnedMesh(next);
            scene.UnloadSkinnedMesh(b);
            output.WriteLine($"gpu={gpu} scenario={scenario} mask/readback, fused/split native+2x, warm reuse, zero default passed");

            void Queue(SkinnedMeshHandle handle, SkinnedGltfMesh source, ulong key, float x) =>
                DqMotionWitness.Queue(scene, source, handle, MotionKey.From(key), Matrix4x4.CreateTranslation(x, 0, 0), DqMotionWitness.Current, 0);
            (int X, int Y) Site(float x)
            {
                Vector4 clip = Vector4.Transform(new Vector4(new Vector3(x, 0, 0) - scene.CurrentFrameView.RenderOrigin, 1),
                    scene.CurrentFrameView.ViewProjection);
                return ((int)((clip.X / clip.W * .5f + .5f) * 128), (int)((.5f - clip.Y / clip.W * .5f) * 64));
            }
        }
        // Parity after the shared scene crossed every configuration, against a fresh scene at the same input.
        using var fresh = new TemporalFixture(128, 64, RejectionWitness.Configure);
        fresh.Frame((s, _) => { });
        Assert.Equal(ReadMask(fx.Device, RejectionWitness.Texture(RejectionWitness.SceneMask(fx.Scene)), 128, 64),
            ReadMask(fresh.Device, RejectionWitness.Texture(RejectionWitness.SceneMask(fresh.Scene)), 128, 64));
    }

    // Evidence, not a red test. Freeze the pre-S3.10 accumulation entry at 3230025369e8a55590590b9fb29322ab1854d6b3.
    // The shared preparation/helpers have no diff from that commit. Only the frozen entry omits mask rejection.
    [GpuFact]
    public void Zero_mask_matches_pre_change_color_and_confidence_bits_for_both_entries()
    {
        IGpuDevice device = fixture.Fixture.Device;
        output.WriteLine($"backend={device.Backend} adapter={device.Capabilities.DeviceName}");
        foreach (TemporalResolveEntry entry in new[] { TemporalResolveEntry.Fused, TemporalResolveEntry.Split })
        foreach (int scale in new[] { 1, 2 }) Compare(entry, scale);

        void Compare(TemporalResolveEntry entry, int scale)
        {
            const int w = 32, h = 16, frames = 4;
            var history = new TemporalHistory();
            history.EnsureTargets(device, w * scale, h * scale, w, h);
            using var renderer = new TemporalResolveRenderer(device);
            using var retired = GpuRetireQueue.Create(device);
            renderer.SelectEntry(entry, retired);
            using var scene = Input(GpuPixelFormat.R16G16B16A16Float);
            using var depth = Input(GpuPixelFormat.R32Float);
            using var motion = Input(GpuPixelFormat.R16G16Float);
            TemporalTextureIo.Upload(device, depth, new float[w * h]);
            TemporalTextureIo.Upload(device, motion, Enumerable.Repeat(MotionMath.Sentinel, w * h * 2).ToArray());
            TemporalTextureIo.Upload(device, history.PreviousDepth(0), Enumerable.Repeat(1e30f, w * h).ToArray());
            TemporalTextureIo.Upload(device, history.Color(0), Colors(w * scale, h * scale, -1));
            TemporalTextureIo.Upload(device, history.Confidence(0), Enumerable.Range(0, w * h * scale * scale * 2)
                .Select(i => i % 2 == 0 ? (2 + i / 2 % 9) / 15f : 0f).ToArray());
            renderer.BindInputs(new TemporalResolveInputs(scene, scene, depth, motion), history);
            var precision = TemporalResolvePrecisionPolicy.For(device);
            string live = entry == TemporalResolveEntry.Fused ? ShaderSources.TemporalResolveFragment(precision)
                : ShaderSources.TemporalAccumulateFragment(precision, upscales: scale > 1);
            int entryStart = ShaderSources.TemporalAccumulateGlsl.IndexOf("TemporalPixel temporalAccumulatePixel(", StringComparison.Ordinal);
            string helpers = ShaderSources.TemporalAccumulateGlsl[ShaderSources.HistoryRejectionReadGlsl.Length..entryStart];
            string frozen = live.Replace(ShaderSources.TemporalAccumulateGlsl, helpers + PreRejectionAccumulatePixel,
                    StringComparison.Ordinal)
                .Replace($"layout(set=0, binding={(entry == TemporalResolveEntry.Fused ? 9 : 12)}) uniform texture2D HistoryRejection;\n",
                    "", StringComparison.Ordinal);
            using var shaders = device.Factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, frozen);
            // The frozen shader owns the pre-change layout too. Metal requires an exact reflected shape.
            using var layout = device.Factory.CreateResourceLayout(entry == TemporalResolveEntry.Fused
                ? new GpuResourceLayoutDescription(T("SceneColor"), T("OpaqueColor"), T("SceneDepth"), T("MotionTex"),
                    T("PrevDepth"), T("HistoryColor"), T("HistoryConfidence"), S("LinearClamp"), U("Resolve"))
                : new GpuResourceLayoutDescription(T("SceneColor"), T("SceneDepth"), T("MotionTex"), T("PrevDepth"),
                    T("HistoryColor"), T("HistoryConfidence"), T("PreparedColour"), T("PreparedSurface"),
                    T("PreparedExpected"), T("PreparedEdge"), S("LinearClamp"), U("Resolve")));
            using var clamp = device.Factory.CreateSampler(GpuSamplerDescription.Linear);
            using var buffer = device.Factory.CreateBuffer(new GpuBufferDescription(TemporalResolveUniforms.SizeInBytes,
                GpuBufferUsage.UniformBuffer));
            IGpuTexture?[]? prepared = entry == TemporalResolveEntry.Split
                ? (IGpuTexture?[])typeof(TemporalSplitResolve).GetField("_targets", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(renderer.SplitResolveForTests)! : null;
            using var oldRead0 = FrozenSet(0);
            using var oldRead1 = FrozenSet(1);
            using var pipeline = TemporalResolveRenderer.Fullscreen(device.Factory, shaders, layout,
                new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence));
            var lens = Matrix4x4.CreateOrthographic(8, 4, .1f, 100);
            var view = new TemporalViewInput(Matrix4x4.Identity, lens);
            try
            {
                for (int frame = 0; frame < frames; frame++)
                {
                    history.BeginResolve(frame);
                    TemporalTextureIo.Upload(device, scene, Colors(w, h, frame));
                    var uniforms = TemporalResolveMath.BuildUniforms(view, view, new Vector2(.25f, -.25f), w, h,
                        w * scale, h * scale, historyValid: true, screenFixedBackground: true);
                    using var native = device.Factory.CreateCommandList();
                    using (GpuRecording.Open(device, native, "Current zero-mask accumulation"))
                    {
                        renderer.PrepareUniforms(native, uniforms, TemporalResolveMath.BuildDepthStore(lens));
                        renderer.Run(native, history);
                    }
                    device.Submit(native);
                    device.WaitForIdle();
                    float[] colour = TemporalTextureIo.Read(device, history.Color(history.WriteIndex));
                    float[] confidence = TemporalTextureIo.Read(device, history.Confidence(history.WriteIndex));
                    Assert.True(colour.Distinct().Count() > 8, "The color sequence must remain nonuniform.");
                    Assert.True(confidence.Where((_, i) => i % 2 == 0).Distinct().Count() > 1,
                        "The confidence sequence must remain nonuniform.");
                    Assert.Contains(confidence.Where((_, i) => i % 2 == 0), c => c > 2f / 15f);
                    using var old = device.Factory.CreateCommandList();
                    using (GpuRecording.Open(device, old, "Frozen pre-S3.10 accumulation on identical inputs"))
                    {
                        old.UpdateBuffer(buffer, 0, in uniforms);
                        old.SetFramebuffer(history.ResolveFramebuffer(history.WriteIndex));
                        old.SetPipeline(pipeline);
                        old.SetGraphicsResourceSet(0, history.ReadIndex == 0 ? oldRead0 : oldRead1);
                        old.Draw(3);
                    }
                    device.Submit(old);
                    device.WaitForIdle();
                    Assert.Equal(colour.Select(BitConverter.SingleToInt32Bits),
                        TemporalTextureIo.Read(device, history.Color(history.WriteIndex)).Select(BitConverter.SingleToInt32Bits));
                    Assert.Equal(confidence.Select(BitConverter.SingleToInt32Bits),
                        TemporalTextureIo.Read(device, history.Confidence(history.WriteIndex)).Select(BitConverter.SingleToInt32Bits));
                }
                output.WriteLine($"zero-mask parity entry={entry} scale={scale} frames={frames} color+confidence bits identical");
            }
            finally { renderer.ReleaseSets(retired); history.ReleaseTargets(retired); }

            IGpuResourceSet FrozenSet(int read) => device.Factory.CreateResourceSet(entry == TemporalResolveEntry.Fused
                ? new GpuResourceSetDescription(layout, scene, scene, depth, motion, history.PreviousDepth(read),
                    history.Color(read), history.Confidence(read), clamp, buffer)
                : new GpuResourceSetDescription(layout, scene, depth, motion, history.PreviousDepth(read),
                    history.Color(read), history.Confidence(read), prepared![0]!, prepared[1]!, prepared[2]!, prepared[3]!, clamp, buffer));
            IGpuTexture Input(GpuPixelFormat format) => device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(w, h, format, GpuTextureUsage.Sampled));
        }

        static float[] Colors(int width, int height, int frame) => Enumerable.Range(0, width * height * 4)
            .Select(i => i % 4 == 3 ? 1f : .2f + (i / 4 % width % 7) * .025f
                + (i / 4 / width % 5) * .017f + i % 4 * .031f + frame * .013f).ToArray();
    }

    static void AssertConfidence(float[] state, (int X, int Y) site, int scale, float expected) =>
        Assert.InRange(MathF.Abs(Confidence(state, site, scale) - expected), 0f, .001f);

    static float Confidence(float[] state, (int X, int Y) site, int scale) =>
        state[((site.Y * scale) * 128 * scale + site.X * scale) * 2];

    [GpuFact]
    public void Projected_edges_near_plane_and_unbounded_boxes_cover_the_required_pixels()
    {
        IGpuDevice device = fixture.Fixture.Device;
        output.WriteLine($"backend={device.Backend} adapter={device.Capabilities.DeviceName}");
        using var mask = new RejectionWitness.Mask(device);
        FrameView view = RejectionWitness.View();
        mask.Add(new MeshBounds(new Vector3(.2f, .2f, .2f), new Vector3(.4f, .4f, .3f)), null);
        Prepare(view);
        byte[] asymmetric = ReadMask(device, mask.Texture, 100, 100);
        Assert.True(asymmetric[35 * 100 + 65] == 255, "No rejection region in the projected positive-Y box.");
        Assert.Equal(0, asymmetric[65 * 100 + 65]);
        mask.Clear();
        mask.Add(new MeshBounds(new Vector3(.01f, -.02f, -.2f), new Vector3(.2f, .02f, .1f)), null);
        Prepare(RejectionWitness.View(Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 10)));
        byte[] near = ReadMask(device, mask.Texture, 100, 100);
        Assert.Equal(255, near[50 * 100 + 99]);
        Assert.Equal(0, near[50 * 100 + 5]);
        foreach (bool previous in new[] { false, true })
        {
            mask.Clear();
            var unbounded = new MeshBounds(new Vector3(float.NegativeInfinity), new Vector3(float.PositiveInfinity));
            mask.Add(previous ? null : unbounded, previous ? unbounded : null);
            Prepare(view);
            Assert.All(ReadMask(device, mask.Texture, 100, 100), value => Assert.Equal(255, value));
        }

        void Prepare(FrameView frame)
        {
            using var commands = device.Factory.CreateCommandList();
            using (GpuRecording.Open(device, commands, "Project actor rejection")) mask.Prepare(commands, frame, null);
            device.Submit(commands);
            device.WaitForIdle();
        }
    }

    // Flat, stationary background with eight stored samples. There is no depth, motion, shading or clipping reason
    // to lose history. At native size only the actor mask can change confidence from 9/15 to 1/15.
    static float[] Resolve(IGpuDevice device, IGpuTexture? mask, TemporalResolveEntry entry, int scale, bool historyValid = true)
    {
        const int w = 128, h = 64;
        var history = new TemporalHistory();
        try
        {
            history.EnsureTargets(device, w * scale, h * scale, w, h);
            history.BeginResolve(1);
            using var renderer = new TemporalResolveRenderer(device);
            using var retired = GpuRetireQueue.Create(device);
            renderer.SelectEntry(entry, retired);
            using var scene = Input(GpuPixelFormat.R16G16B16A16Float);
            using var depth = Input(GpuPixelFormat.R32Float);
            using var motion = Input(GpuPixelFormat.R16G16Float);
            float[] colour = Enumerable.Range(0, w * h * 4).Select(i => i % 4 == 3 ? 1f : .3f).ToArray();
            TemporalTextureIo.Upload(device, scene, colour);
            TemporalTextureIo.Upload(device, depth, new float[w * h]);
            TemporalTextureIo.Upload(device, motion, Enumerable.Repeat(MotionMath.Sentinel, w * h * 2).ToArray());
            TemporalTextureIo.Upload(device, history.Color(history.ReadIndex),
                Enumerable.Range(0, w * h * scale * scale * 4).Select(i => i % 4 == 3 ? 1f : .3f).ToArray());
            TemporalTextureIo.Upload(device, history.Confidence(history.ReadIndex),
                Enumerable.Range(0, w * h * scale * scale * 2).Select(i => i % 2 == 0 ? 8f / 15f : 0).ToArray());
            TemporalTextureIo.Upload(device, history.PreviousDepth(history.ReadIndex), Enumerable.Repeat(1e30f, w * h).ToArray());
            object?[] inputs = typeof(TemporalResolveInputs).GetConstructors().Any(c => c.GetParameters().Length == 5)
                ? [scene, scene, depth, motion, mask] : [scene, scene, depth, motion];
            renderer.BindInputs((TemporalResolveInputs)Activator.CreateInstance(typeof(TemporalResolveInputs), inputs)!, history);
            var lens = Matrix4x4.CreateOrthographic(8, 4, .1f, 100);
            var view = new TemporalViewInput(Matrix4x4.Identity, lens);
            var uniforms = TemporalResolveMath.BuildUniforms(view, view, Vector2.Zero, w, h, w * scale, h * scale, historyValid,
                screenFixedBackground: true);
            using var commands = device.Factory.CreateCommandList();
            using (GpuRecording.Open(device, commands, "DQ history rejection witness"))
            {
                renderer.PrepareUniforms(commands, uniforms, TemporalResolveMath.BuildDepthStore(lens));
                renderer.Run(commands, history);
            }
            device.Submit(commands);
            device.WaitForIdle();
            return TemporalTextureIo.Read(device, history.Confidence(history.WriteIndex));

            IGpuTexture Input(GpuPixelFormat format) => device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(w, h, format, GpuTextureUsage.Sampled));
        }
        finally { history.ReleaseTargets(); }
    }

    // Verbatim function from 3230025369e8a55590590b9fb29322ab1854d6b3, including its final newline.
    const string PreRejectionAccumulatePixel = """
TemporalPixel temporalAccumulatePixel(vec2 uv, ivec2 centreTexel, ivec2 maxTexel, TemporalNeighbourhood n,
    TemporalKernels kernels, bool ridge, TemporalSurface surface) {
    vec2 internalSize = Sizes.xy;
    vec2 displaySize = Sizes.zw;
    vec2 jitter = Jitter.xy;
    bool historyValid = Jitter.w > 0.5;
    vec3 neighbourMin = toVec3(n.neighbourMin);
    vec3 neighbourMax = toVec3(n.neighbourMax);
    float alphaMin = toFloat(n.alphaMin);
    float alphaMax = toFloat(n.alphaMax);
    float reconstructionWeight = n.reconstructionWeight;
    float sampleWeight = toFloat(n.sampleWeight);

    float reactive = clamp(toFloat(n.reactiveDifference) * ReactiveGain, 0.0, 1.0);
    vec3 mean = n.momentSum / 9.0;
    vec3 deviation = sqrt(max(n.momentSquares / 9.0 - mean * mean, vec3(0.0)));
    vec4 current = n.reconstruction / max(reconstructionWeight, 1.0e-4);
    // The negative lobes cannot ring past the neighbourhood.
    current.xyz = clamp(current.xyz, neighbourMin, neighbourMax);
    current.w = clamp(current.w, alphaMin, alphaMax);

    // Steps 1 and 3: where this pixel was last frame, by the surface its centre texel prepared, the dilated nearest or
    // beside a fast edge the centre texel's own, and the depth a static surface there had. Motion is clamped to two
    // screens and a point behind last frame's camera is written two screens away, so any such previous position falls
    // outside [0, 1] here and is rejected like any other off-screen one.
    float expectedDepth = surface.expectedDepth;
    bool depthTested = surface.depthTested;
    bool nearerMoved = surface.nearerMoved;
    bool band = surface.band;
    bool followed = surface.followed;
    bool ownReprojected = surface.ownReprojected;
    bool narrowMoving = surface.narrowMoving;
    bool movingEdge = surface.movingEdge;
    float edgeMotion = surface.edgeMotion;
    vec2 previousUv = temporalPreviousUv(uv, surface.motion, surface.background);
    bool onScreen = all(greaterThanEqual(previousUv, vec2(0.0))) && all(lessThanEqual(previousUv, vec2(1.0)));
    float motionPixels = onScreen ? length((uv - previousUv) * displaySize) : 0.0;

    // Last frame's state around the previous position, read once for steps 3 and 2: the confidence blended by the
    // four texels' bilinear weights, and the lock as the largest of those that carry weight. A bilinear lock under
    // motion is blended with unlocked neighbours every frame and ran out about three times faster than LockDecay, so
    // a moving sub-texel feature lost its hold. At a texel centre only that texel carries weight, so a still lock
    // reads back unchanged and never spreads. carriedMoved is whether any texel that carries weight stored the lock
    // of a moving surface (temporalStoreLock), carriedBand whether any stored that of a band pixel, and
    // carriedFollowed whether that was the followed surface's own.
    vec2 fetchedState = vec2(0.0);
    bool carriedMoved = false;
    bool carriedBand = false;
    bool carriedFollowed = false;
    if (historyValid && onScreen) {
        vec2 statePosition = previousUv * displaySize - 0.5;
        ivec2 stateBase = ivec2(floor(statePosition));
        vec2 stateFraction = statePosition - vec2(stateBase);
        ivec2 lastState = ivec2(displaySize) - ivec2(1);
        vec2 s00 = texelFetch(sampler2D(HistoryConfidence, LinearClamp), clamp(stateBase, ivec2(0), lastState), 0).rg;
        vec2 s10 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 0), ivec2(0), lastState), 0).rg;
        vec2 s01 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(0, 1), ivec2(0), lastState), 0).rg;
        vec2 s11 = texelFetch(sampler2D(HistoryConfidence, LinearClamp),
            clamp(stateBase + ivec2(1, 1), ivec2(0), lastState), 0).rg;
        vec2 g = 1.0 - stateFraction;
        vec4 bilinear = vec4(g.x * g.y, stateFraction.x * g.y, g.x * stateFraction.y,
            stateFraction.x * stateFraction.y);
        vec4 carried = step(vec4(1.0e-3), bilinear);   // texels whose weight is more than rounding
        // The least state a carrying texel stored: the moved, band and followed marks lie below every plain lock,
        // each below the one before, so one least value answers all three, and a footprint carrying two marks
        // answers as the lower (step 3).
        float carriedLeast = min(min(carried.x > 0.5 ? s00.y : 0.0, carried.y > 0.5 ? s10.y : 0.0),
            min(carried.z > 0.5 ? s01.y : 0.0, carried.w > 0.5 ? s11.y : 0.0));
        carriedMoved = temporalStoredMoved(carriedLeast);
        carriedBand = temporalStoredBand(carriedLeast);
        carriedFollowed = temporalStoredFollowed(carriedLeast);
        s00.y = temporalStoredLock(s00.y);
        s10.y = temporalStoredLock(s10.y);
        s01.y = temporalStoredLock(s01.y);
        s11.y = temporalStoredLock(s11.y);
        fetchedState = vec2(dot(bilinear, vec4(s00.x, s10.x, s01.x, s11.x)),
            max(max(carried.x * s00.y, carried.y * s10.y), max(carried.z * s01.y, carried.w * s11.y)));
    }

    // Step 3: disocclusion, one-sided. A pixel expects the depth of the surface it reprojected, and a stored depth
    // nearer than that means something covered it last frame. All four stored depths around the reprojected position
    // nearer drops the history. Where the expected surface shows under less than DisocclusionVisibleShare of their
    // bilinear weight, the pixel was mostly covered and drops it too, unless the nearest stored depth is narrow
    // (temporalNarrowDepth), a feature narrower than a texel, or two side by side, that the jitter missed this frame,
    // or the lock the pixel carries lies within half of LockDecay of whole. The narrow exception holds only where the
    // state the pixel carries says no moving surface showed there last frame (temporalStoreLock): a keyed line one or
    // two texels wide that moved on is as narrow as a missed still blade, and only that state tells them apart. A lock
    // within half a decay of whole was refreshed by a ridge on the last frame and has lost less than that since, as a
    // still sub-texel feature's has on the frame after the jitter showed it, and step 6 keeps its hold whole. Step 6
    // releases a lock by motion from LockMotionStartPixels on, so a feature moving more than a sixteenth of the way
    // from there to LockMotionEndPixels, about 1.2 display pixels a frame, leaves no such lock where it was. Where the
    // state says a moving surface showed there, neither exception holds, and any stored depth nearer than expected that
    // carries more than rounding of the weight drops the history: that surface's ridged pixels hold locks near whole,
    // as an avatar the camera follows keeps its own, and a history it covered in part holds its colour in that part,
    // which the clip leaves over a textured background. Where the state says a band pixel stored the history (step 1),
    // a pixel whose dilated nearest surface did not move in the world (nearerMoved) drops it, unless every stored
    // depth is farther than expected. The band followed a moving nearer surface, beside its edge or on it, and a pixel
    // of a still surface would carry that surface's colour away with it. A pixel beside or on the moving surface, and
    // a nearer surface reading the band where its edge was, keep it. A history the followed surface's own pixels
    // stored (the followed mark) drops only where the pixel moved on screen more than FollowedStillDisplayPixels and
    // one of the nine current texels around its position moves on screen otherwise than the pixel reprojected, by
    // more than FollowedHistoryMotionFraction of that motion: the followed surface still shows there while the
    // pixel's own surface passes, as the ground an avatar uncovers does under a camera that follows it. A background
    // texel, whose motion is the sentinel, is never the followed surface and is left out. A surface that stopped in
    // the world, or turned back through zero travel, reads its own pixels' history in place, whatever passes beside
    // it, or where the same surface shows moving with it under a camera that eases on after it, and keeps it. A
    // footprint whose carrying texels stored both marks, one the followed surface's and one the band's, reads as the
    // followed mark, the least of them, so its band history drops only as the followed mark's does. That takes a
    // fractional position across the outline, as under a camera that eases on. Keyed on a moving edge instead, the
    // drop never fired on ground under a perspective camera, whose neighbouring texels move apart on screen by their
    // depth step so that every ground pixel reads as a moving edge. Sparing a pixel whose 3x3 holds a surface farther
    // than its centre spared every ground pixel at a grazing angle, where each ground texel lies farther than the one
    // below it by more than the tolerance.
    // Any farther stored depth kept a sub-pixel edge from reading as revealed, and on its own it kept the ring of
    // pixels around a moving object's old place, whose footprint reaches past its edge, from ever being disoccluded. A
    // background pixel expects BackgroundLinearDepth, so anything stored nearer there was covering it. A static surface
    // on or behind last frame's camera had no history there. The footprint sits at the display pixel's previous
    // position, where history is read, while the expected depth comes from the reprojected texel's own point, the
    // dilated nearest in the 3x3 unless the pixel reprojected by its own. The same four depths tell step 6 whether a
    // pixel at a moving edge reads history a farther surface left: every one of them farther than the moving surface's
    // expected depth. That runs for a moving surface too, whose depth test is skipped, and costs no fetch beyond the
    // four a depth-tested pixel already takes.
    bool followedElsewhere = !carriedFollowed;
    if (carriedFollowed && !nearerMoved && motionPixels > FollowedStillDisplayPixels) {
        vec2 ownMotion = (uv - previousUv) * internalSize;
        ivec2 historyTexel = ivec2(floor(previousUv * internalSize + jitter));
        float apart = 0.0;
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                vec2 shown = texelFetch(sampler2D(MotionTex, LinearClamp),
                    temporalNeighbourTexel(historyTexel, x, y, maxTexel), 0).rg;
                if (!(abs(shown.x) > MotionSentinel)) apart = max(apart, length(shown * internalSize - ownMotion));
            }
        }
        followedElsewhere = apart > FollowedHistoryMotionFraction * length(ownMotion);
    }
    bool disoccluded = false;
    bool heldFromFarther = false;
    if (historyValid && onScreen && (depthTested || movingEdge)) {
        DepthFootprint footprint = temporalDepthFootprint(previousUv, internalSize, maxTexel, expectedDepth);
        disoccluded = depthTested && (!(expectedDepth > 1.0e-6)
            || footprint.farthest < expectedDepth * (1.0 - DisocclusionTolerance)
            || (carriedBand && !nearerMoved && !(expectedDepth < footprint.nearest * (1.0 - DisocclusionTolerance))
                && followedElsewhere)
            || (carriedMoved ? footprint.visibleShare < 1.0 - 1.0e-3
                : footprint.visibleShare < DisocclusionVisibleShare
                    && fetchedState.y <= 1.0 - 0.5 * LockDecay
                    && !temporalNarrowDepth(footprint.nearestTexel, footprint.nearest, maxTexel, false)));
        heldFromFarther = movingEdge && expectedDepth < footprint.nearest * (1.0 - DisocclusionTolerance);
    }

    // Step 2: Catmull-Rom history at the reprojected position. A NaN or an infinity never reaches the output, because
    // a comparison with either is false, which survives fast math where isnan may not.
    bool useHistory = historyValid && onScreen && !disoccluded;
    vec4 history = vec4(0.0);
    vec2 historyState = vec2(0.0);
    if (useHistory) {
        vec4 fetched = sampleHistoryCatmullRom(previousUv, displaySize);
        bool finite = all(greaterThan(fetched, vec4(-HalfMax))) && all(lessThan(fetched, vec4(HalfMax)))
            && all(greaterThan(fetchedState, vec2(-HalfMax))) && all(lessThan(fetchedState, vec2(HalfMax)));
        if (finite) {
            history = max(fetched, vec4(0.0));
            historyState = clamp(fetchedState, vec2(0.0), vec2(1.0));
        } else {
            useHistory = false;
        }
    }

    // Step 8, first half: the accumulated sample weight, capped lower as motion grows and cut by reactive content.
    float motionCap = mix(MaxAccumulation, MovingAccumulation,
        clamp(motionPixels / MotionAccumulationPixels, 0.0, 1.0));
    float accumulated = useHistory
        ? min(historyState.x * MaxAccumulation, motionCap) * (1.0 - reactive * ReactiveStrength)
        : 0.0;

    // Step 6: thin features. A ridge through the centre texel refreshes the lock, which decays by LockDecay a frame
    // whatever the preset. Large motion and reactive content release it, and so does motion at an edge, where the pixel
    // holds a moving feature's history, not its own. A pixel that reprojected by its own motion and kept its history
    // holds its own, because where a moving surface showed last frame step 3 keeps a history only where no stored depth
    // carrying weight lies nearer than the pixel's own surface. So a still sub-texel feature beside a fast surface
    // keeps its lock, while one that restarted lets go of a ridge the moving surface's colour may have given it. At a
    // moving edge whose history a farther surface left, the lock read with that history belongs to that surface, and a
    // nearer surface crossing a held thin feature would carry it onward, so it is dropped before a ridge can refresh
    // it. A thin feature taking a ridge keeps its own lock, and so does one beside a still nearer surface,
    // which is no moving edge. The hold on the clip stays whole while the lock is at least 1 / LockHoldGain, so a
    // sub-texel feature missed for a few frames keeps its luma, and it lets go over the rest of the lock.
    float lockValue = useHistory && !heldFromFarther ? max(historyState.y - LockDecay, 0.0) : 0.0;
    if (ridge) lockValue = 1.0;
    float motionRelease = clamp((motionPixels - LockMotionStartPixels)
        / (LockMotionEndPixels - LockMotionStartPixels), 0.0, 1.0);
    lockValue *= (1.0 - motionRelease) * (1.0 - clamp(reactive * LockReactiveRelease, 0.0, 1.0))
        * (1.0 - clamp((ownReprojected && useHistory ? 0.0 : edgeMotion) * LockEdgeRelease, 0.0, 1.0));
    float hold = clamp(lockValue * LockHoldGain, 0.0, 1.0);

    // Step 5: variance clipping in luma-weighted YCoCg, from the box centre towards the history, which keeps its hue.
    // Gamma is wide for a still pixel and narrows as it moves. A locked thin feature's luma neither drives the clip
    // nor loses its share of it, in proportion to its hold.
    float gamma = mix(GammaStill, GammaMoving, clamp(motionPixels / GammaMotionPixels, 0.0, 1.0));
    vec3 boxMin = mean - gamma * deviation;
    vec3 boxMax = mean + gamma * deviation;
    vec3 historyYcc = rgbToYCoCg(toWeighted(history.rgb));
    vec3 boxCentre = 0.5 * (boxMin + boxMax);
    vec3 boxExtent = 0.5 * (boxMax - boxMin) + vec3(1.0e-5);
    vec3 fromCentre = historyYcc - boxCentre;
    vec3 excess = abs(fromCentre) / boxExtent;

    // Steps 6 and 8 where a pixel's shading changed: a light or a shadow sliding over a surface, still or moving. A
    // ridged texel refreshes its lock every frame and the hold keeps the old luma out of the clip, so only the floor
    // of the current weight, about one in sixteen, would move it, and the lag behind the edge would grow with the
    // edge's travel a frame. A clip alone leaves up to the box's half-width behind the edge to fade at that floor. So
    // such a pixel lets go of its hold and its accumulated weight for the frame and shows this frame's reconstruction.
    // The tests read the history where the pixel reprojected, so a moving pixel takes them as a still one does, and
    // one moving past LockMotionEndPixels, whose lock is gone, drops the accumulated weight a moving surface carries
    // into a shadow. Three things must hold. The preset's internal texel is under ShadingMaxUpscale display pixels.
    // The history luma lies past the box by more than ShadingResetExtents of the half-width. And the pixel ridged this
    // frame, or its box's half-width exceeds ridgeStep, or it carries a lock with no ridge now over a box wider than
    // ShadingLockFlatShare of ridgeStep. A flat 3x3 under a lockless pixel missed a feature beside it, and under a
    // carried lock it is a thin feature on flat ground the jitter missed, so both keep their hold. Then its history
    // luma lies from this frame's reconstruction by more than ShadingChangeExtents half-widths for each display pixel
    // an internal texel spans, or last frame's ring around it lies more than ShadingRingExtents half-widths from this
    // frame's eight texels around the centre, on the history's side (temporalHistoryRingShift): either at Native, and
    // both where the frame upscales. There the jitter aliases texture finer than an internal texel, so a display
    // pixel's accumulated detail lies past this frame's box and apart from its reconstruction with nothing changed,
    // and either test alone released it. A carried lock with no ridge now, and a pixel moving at LockMotionStartPixels
    // or more, lie apart from their reconstruction with nothing changed: a missed thin feature by nature and a moving
    // one by its resampled history. So for them the reconstruction never decides alone, and the ring must lie
    // ShadingStrictRingExtents half-widths away, and for a carried lock also ShadingLockRingShare of the history's own
    // distance from its reconstruction. A carried lock also requires both opposite-corner pair means to lie
    // ShadingLockPairExtents away: a diagonal missed feature crosses one pair, and texture noise can carry the whole
    // ring's mean past the share while the other pair still agrees. A shading change must move both pairs. A thin
    // feature's history spills into the ring by a small share of the feature. Exempt, a carried lock kept its whole hold
    // while its neighbours let go, which read as speckle behind a fast edge. A stable peak one texel across lies past
    // its own box, one bright texel of nine moving the mean little, but its reconstruction shows it and its ring
    // agrees, so it keeps what it held. The half-width here is at least ridgeStep, the ridge threshold
    // (LockRidgeAbsolute, LockRidgeRelative) taken at the 3x3's mean luma rather than at the centre texel's luma as
    // temporalRidge takes it, since the box it stands in for is the neighbourhood's. A released pixel shows this
    // frame's internal reconstruction as a fresh pixel does, with no share of the display-sized one
    // (temporalDisplayShare), which a converged pixel takes in at the current weight's floor. The stored lock is left
    // as it is: the next frame's ridge decides it, and a store that differed here moved other pixels' locks by a
    // rounding step between the two entry points.
    float ridgeStep = max(LockRidgeAbsolute, LockRidgeRelative * mean.x);
    float releaseExtent = max(boxExtent.x, ridgeStep);
    bool released = false;
    bool upscaled = Jitter.z > 1.0;
    bool carriedLock = !ridge && lockValue > 0.0;
    bool strict = carriedLock || !(motionPixels < LockMotionStartPixels);
    if (useHistory && Jitter.z < ShadingMaxUpscale
        && (ridge || boxExtent.x > (carriedLock ? ShadingLockFlatShare : 1.0) * ridgeStep)
        && abs(fromCentre.x) > (1.0 + ShadingResetExtents) * releaseExtent) {
        float ownShift = abs(historyYcc.x - current.x);
        bool ownChanged = ownShift > ShadingChangeExtents * Jitter.z * releaseExtent;
        released = ownChanged && !upscaled && !strict;
        if (!released && (ownChanged || !upscaled)) {
            vec2 ringShift = temporalHistoryRingShift(previousUv, displaySize,
                (n.momentSum.x - n.centreLuma) / 8.0, fromCentre.x);
            released = ringShift.x > (strict ? max(ShadingStrictRingExtents * releaseExtent,
                carriedLock ? ShadingLockRingShare * ownShift : 0.0) : ShadingRingExtents * releaseExtent)
                && (!carriedLock || ringShift.y > ShadingLockPairExtents * releaseExtent);
        }
    }
    if (released) {
        hold = 0.0;
        accumulated = 0.0;
    }
    excess.x *= 1.0 - hold;
    float clipScale = max(excess.x, max(excess.y, excess.z));
    vec3 clipped = clipScale > 1.0 ? boxCentre + fromCentre / clipScale : historyYcc;
    clipped.x = mix(clipped.x, historyYcc.x, hold);
    float historyAlpha = clamp(history.a, alphaMin, alphaMax);

    // Step 5 beside a fast narrow moving feature: where the pixel reprojected by its own motion, its history holds its
    // own surface alone, and the feature this frame's reconstruction also saw is missing from it. So the feature's
    // share of the reconstruction weight, on the texels nearer than the pixel's own surface, takes the feature's
    // current colour in the history, save the share the pixel's lock holds. The pixel then shows its current coverage
    // while its own surface stays accumulated, and it stores MovingShareConfidence, since that colour holds for this
    // frame alone. A pixel whose hold is whole keeps its own history and its confidence: it holds a still sub-texel
    // feature, which the next frame needs whole on the frames the jitter misses it. A surface wider than two texels
    // keeps its own history at its edge, where the same colour flickered with the jitter. The narrow test reads the run
    // through the nearest texel alone, so a wide object is narrow where it is one or two texels across in this frame's
    // depth, at a corner's tip or on a face seen at a grazing angle, and its colour is taken there too. The nearer
    // texels are summed here, where they are needed, rather than in the 3x3 every pixel runs: the sum is the same, and
    // five more accumulators carried through that loop made the whole program spill for every pixel.
    float movingShare = 0.0;
    if (narrowMoving) {
        float ownDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), centreTexel, 0).rg,
            texelFetch(sampler2D(SceneDepth, LinearClamp), centreTexel, 0).r);
        vec4 nearerSum = vec4(0.0);
        float nearerWeight = 0.0;
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec2 texel = temporalNeighbourTexel(centreTexel, x, y, maxTexel);
                vec4 sceneColor = texelFetch(sampler2D(SceneColor, LinearClamp), texel, 0);
                vec3 ycc = rgbToYCoCg(temporalWeighted(sceneColor.rgb));
                float lanczosWeight = toFloat(temporalLanczosWeight(kernels, x + 1, y + 1));
                float viewDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), texel, 0).rg,
                    texelFetch(sampler2D(SceneDepth, LinearClamp), texel, 0).r);
                if (ownDepth > viewDepth * (1.0 + DisocclusionTolerance)) {
                    nearerSum += vec4(ycc, sceneColor.a) * lanczosWeight;
                    nearerWeight += lanczosWeight;
                }
            }
        }
        if (nearerWeight > 1.0e-4) {
            movingShare = clamp(nearerWeight / max(reconstructionWeight, 1.0e-4), 0.0, 1.0) * (1.0 - hold);
            vec4 movingColor = nearerSum / nearerWeight;
            clipped = mix(clipped, clamp(movingColor.xyz, neighbourMin, neighbourMax), movingShare);
            historyAlpha = mix(historyAlpha, clamp(movingColor.w, alphaMin, alphaMax), movingShare);
        }
    }

    // Step 4 for a converged pixel: the display-sized reconstruction by its share (temporalDisplayCurrent), none where
    // the shading release dropped the pixel's accumulated weight this frame. A program compiled for the display's own
    // size (TemporalAtDisplaySize), where the share is always none, leaves it out.
#ifndef TemporalAtDisplaySize
    float displayShare = temporalDisplayShare(useHistory && !released, historyState.x, motionPixels, reactive);
    if (displayShare > 0.0)
        current.xyz = temporalDisplayCurrent(current.xyz, n.display, displayShare, neighbourMin, neighbourMax);
#endif

    // Step 8, second half: blend in the weighted space. The current weight is 1 on a reset and falls to
    // sampleWeight / (MaxAccumulation + sampleWeight), about one in sixteen, raised wherever reactive content or a
    // disocclusion removed accumulated weight.
    float currentWeight = accumulated > 1.0e-3 ? sampleWeight / (accumulated + sampleWeight) : 1.0;
    vec3 resolvedYcc = mix(clipped, current.xyz, currentWeight);

    TemporalPixel result;
    result.color = fromWeighted(max(yCoCgToRgb(resolvedYcc), vec3(0.0)));
    result.alpha = mix(historyAlpha, current.w, currentWeight);
    result.confidence = movingShare > 0.0 ? MovingShareConfidence
        : min(accumulated + sampleWeight, motionCap) / MaxAccumulation;
    result.stability = lockValue;
    result.moved = followed ? 3.0 : band ? 2.0 : historyValid && !depthTested ? 1.0 : 0.0;
    result.disocclusion = historyValid && (!onScreen || disoccluded) ? 1.0 : 0.0;
    result.reactive = reactive;
    result.clip = useHistory && clipScale > 1.0 && length(clipped - historyYcc) > ClipFlagMinimumMove ? 1.0 : 0.0;
    return result;
}

""";

    internal static byte[] ReadMask(IGpuDevice device, IGpuTexture? mask, int width, int height)
    {
        var result = new byte[width * height];
        if (mask is null) return result;
        Assert.Equal(GpuPixelFormat.R8UNorm, mask.Format);
        Assert.Equal((uint)width, mask.Width);
        Assert.Equal((uint)height, mask.Height);
        using var staging = device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using var commands = device.Factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, "Read actor rejection")) commands.CopyTexture(mask, staging);
        device.Submit(commands);
        device.WaitForIdle();
        MappedData map = device.Map(staging, GpuMapMode.Read);
        try
        {
            for (int y = 0; y < height; y++) Marshal.Copy(map.Data + (int)(y * map.RowPitch), result, y * width, width);
        }
        finally { device.Unmap(staging); }
        return result;
    }
}

public sealed class DqHistoryRejectionScene : IDisposable
{
    TemporalFixture? _fixture;
    internal TemporalFixture Fixture => _fixture ??= new TemporalFixture(128, 64, RejectionWitness.Configure);
    public void Dispose() => _fixture?.Dispose();
}
