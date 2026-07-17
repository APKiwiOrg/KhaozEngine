using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the frame's queued <see cref="ParticleSprite"/>s as ONE instanced, premultiplied-alpha draw into the
    /// lit color attachment + read-only scene depth (ColorDepthFB), after the water pass and before the post
    /// chain. Per-sprite parameters ride a per-instance vertex attribute stream (the Metal-safe instancing
    /// pattern <see cref="GroundDecalRenderer"/> proves in production), the quad is expanded from
    /// <c>gl_VertexIndex</c> in the vertex shader, and the procedural shape + soft depth fade are evaluated in
    /// the fragment shader. A single pipeline with a (One, InverseSourceAlpha) blend composites alpha and
    /// additive sprites correctly from one back-to-front sorted stream, because the fragment premultiplies and
    /// zeroes the alpha lane for additive sprites.
    /// </summary>
    internal sealed class ParticleRenderer : IDisposable
    {
        /// <summary>Per-instance sprite attributes, matching the <c>I*</c> inputs of
        /// <see cref="ShaderSources.ParticleVert"/> (6 x vec4 = 96 bytes, every member 16-byte aligned).</summary>
        public struct ParticleInstance
        {
            public Vector4 CenterSize;    // xyz world center, w half-size
            public Vector4 VelocityRot;   // xyz world velocity, w rotation (radians)
            public Vector4 Color;         // straight rgba
            public Vector4 Shape;         // x shape id, y shape param, z life norm, w seed
            public Vector4 Extra;         // x stretch, y additivity (0 alpha / 1 additive), z orientation, w soft-fade scale
            public Vector4 Flip;          // x frameA, y frameB, z blend, w packed grid+strength (0 = procedural)
        }

        /// <summary>The single per-frame uniform block, declared identically in both shader stages (ONE uniform
        /// buffer per pipeline, the Metal contract). 192 bytes.</summary>
        [StructLayout(LayoutKind.Sequential)]
        struct FrameUniforms
        {
            public Matrix4x4 ViewProj;      // GpuClip-corrected
            public Matrix4x4 InvViewProj;   // RAW (un-clip-corrected), matching Camera.ScreenToRay
            public Vector4 CamRight;
            public Vector4 CamUp;
            public Vector4 CamPosTime;      // xyz eye, w effect time seconds
            public Vector4 Params;          // x soft-fade distance (0 off), y quality (1 full / 0 reduced), z background depth marker, w reserved
        }

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        IGpuPipeline _pipeline;                    // rebuilt by SetOutputs when the target sample count changes
        readonly IGpuBuffer _frameUbo;
        readonly List<IDisposable> _retired = new();
        IGpuBuffer? _instances;
        int _capacity;
        ParticleInstance[] _packed = Array.Empty<ParticleInstance>();
        ParticleRun[] _runs = Array.Empty<ParticleRun>();
        // 1x1 stand-ins bound for runs with no real texture: white atlas + neutral motion (0.5, 0.5 => zero
        // displacement). Procedural runs sample them then discard the taps (packed grid 0), so the same pipeline
        // serves procedural and flipbook sprites with byte-identical procedural output.
        readonly IGpuTexture _dummyAtlas;
        readonly IGpuTexture _dummyMv;
        // Per-atlas-pair resource sets, keyed by (atlas list index, motion list index) with -1 for the dummy. Each
        // set also references res.DepthColorTex, so the whole cache is dropped when the render target is rebound
        // (generation bump) or a referenced texture is unloaded.
        readonly Dictionary<(int atlas, int mv), IGpuResourceSet> _sets = new();
        RenderResources? _bound;
        int _boundGen;

        static readonly uint InstanceStride = (uint)Unsafe.SizeOf<ParticleInstance>();

        /// <summary>A contiguous slice of the sorted sprite stream sharing one atlas pair, drawn as one instanced
        /// call. <see cref="AtlasIndex"/> / <see cref="MotionIndex"/> are TextureHandle list indices, or -1 for the
        /// dummy (procedural) texture.</summary>
        public readonly record struct ParticleRun(int AtlasIndex, int MotionIndex, int Start, int Count);

        /// <summary>Premultiplied-alpha compositing: out = src + dst * (1 - src.a). The fragment emits
        /// premultiplied rgb and (for additive sprites) alpha 0, so this one state serves both blend modes.</summary>
        static GpuBlendAttachment Premultiplied => new(
            true,
            GpuBlendFactor.One, GpuBlendFactor.InverseSourceAlpha, GpuBlendFunction.Add,
            GpuBlendFactor.One, GpuBlendFactor.InverseSourceAlpha, GpuBlendFunction.Add);

        public ParticleRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.ParticleVert, ShaderSources.ParticleFrag);
            // Binding order matches the fragment shader exactly: Frame(0), DepthTex(1), Samp(2), MotionTex(3),
            // AtlasTex(4), AtlasSamp(5). Motion precedes atlas so the Metal static-sample order (binding order) lets
            // the two-tap warp read the motion vectors before offsetting the atlas taps.
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("MotionTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AtlasTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("AtlasSamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            _frameUbo = f.CreateBuffer(new GpuBufferDescription(192, GpuBufferUsage.UniformBuffer));
            _dummyAtlas = MakeSolid1x1(f, gd, 255, 255, 255, 255);
            _dummyMv = MakeSolid1x1(f, gd, 128, 128, 0, 255);
            _pipeline = Pipe(f, colorOutput);
        }

        // 1x1 RGBA8 texture of a single colour, no mip chain. Backs the dummy atlas + neutral motion sheet.
        static IGpuTexture MakeSolid1x1(IGpuResourceFactory f, IGpuDevice gd, byte r, byte g, byte b, byte a)
        {
            IGpuTexture tex = f.CreateTexture(new GpuTextureDescription(1u, 1u, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, 1u));
            gd.UpdateTexture(tex, new byte[] { r, g, b, a }, 0, 0, 1, 1);
            return tex;
        }

        /// <summary>Rebuild the pipeline for a new color-target output description (e.g. the target became
        /// multisampled for MSAA). Layout/shaders/buffers are kept.</summary>
        public void SetOutputs(GpuOutputDescription colorOutput)
        {
            _pipeline.Dispose();
            _pipeline = Pipe(_gd.Factory, colorOutput);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { Premultiplied },
                // Depth test against the scene (nearer geometry occludes sprites), never write (later passes
                // still see the meshes' depth).
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.LessEqual),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                // One instance-rate vertex stream carrying the six per-sprite vec4 attributes (locations 0..5,
                // no holes, every attribute consumed by the vertex stage: the D3D11 contiguous-input contract).
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    new GpuVertexLayoutDescription(
                        stride: InstanceStride,
                        instanceStepRate: 1,
                        elements: new[]
                        {
                            new GpuVertexElement("ICenterSize", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IVelocityRot", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IColor", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IShape", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IExtra", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IFlip", GpuVertexElementFormat.Float4),
                        }),
                },
                Outputs = outputs,
            });

        // Drop the per-atlas set cache when the render target rebinds. Generation-based guard (not dimensions): a
        // same-size recreate (MSAA/bloom/HDR toggle) also invalidates DepthColorTex, see RenderResources.Generation.
        void EnsureBound(RenderResources res)
        {
            if (ReferenceEquals(_bound, res) && res.Generation == _boundGen) return;
            ClearSets();
            _bound = res; _boundGen = res.Generation;
        }

        void ClearSets()
        {
            // A cached set may still be referenced by queued draws (Scene3D calls this mid-life from
            // UnloadTexture), so drain the device before disposing.
            if (_sets.Count > 0) _gd.WaitForIdle();
            foreach (var kv in _sets) kv.Value.Dispose();
            _sets.Clear();
        }

        /// <summary>Drop every cached per-atlas resource set. Scene3D calls it when a texture the sets may reference
        /// is unloaded, so a later load reusing the freed slot index cannot bind a stale texture.</summary>
        public void InvalidateTextureSets() => ClearSets();

        // Get (creating + caching on first use) the resource set for one atlas pair. -1 indices (or a resolver that
        // returns null for a since-unloaded slot) fall back to the dummy textures. Binding order matches _layout.
        IGpuResourceSet SetFor(RenderResources res, int atlasIdx, int mvIdx, Func<int, IGpuTexture?> resolveTexture)
        {
            var key = (atlasIdx, mvIdx);
            if (_sets.TryGetValue(key, out IGpuResourceSet? set)) return set;
            IGpuTexture atlas = (atlasIdx < 0 ? null : resolveTexture(atlasIdx)) ?? _dummyAtlas;
            IGpuTexture mv = (mvIdx < 0 ? null : resolveTexture(mvIdx)) ?? _dummyMv;
            set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _layout, _frameUbo, res.DepthColorTex, _gd.PointSampler, mv, atlas, _gd.LinearSampler));
            _sets[key] = set;
            return set;
        }

        static (int atlas, int mv) KeyOf(in ParticleSprite s)
        {
            if (!s.Flipbook.IsActive) return (-1, -1);
            int mv = s.Flipbook.MotionTexture.IsValid ? s.Flipbook.MotionTexture.ListIndex : -1;
            return (s.Flipbook.Texture.ListIndex, mv);
        }

        /// <summary>Split the already back-to-front sorted stream into contiguous runs sharing one atlas pair,
        /// WITHOUT reordering (the global sort must survive). Procedural sprites carry the dummy pair (-1, -1) and
        /// merge with adjacent procedural sprites, so an all-procedural frame yields exactly one run. Writes runs
        /// into <paramref name="runs"/> (size it to at least the sprite count) and returns the run count.</summary>
        public static int BuildRuns(ReadOnlySpan<ParticleSprite> sorted, Span<ParticleRun> runs)
        {
            int count = 0;
            int i = 0;
            while (i < sorted.Length)
            {
                (int atlas, int mv) = KeyOf(sorted[i]);
                int start = i++;
                while (i < sorted.Length)
                {
                    (int a, int m) = KeyOf(sorted[i]);
                    if (a != atlas || m != mv) break;
                    i++;
                }
                runs[count++] = new ParticleRun(atlas, mv, start, i - start);
            }
            return count;
        }

        void EnsureCapacity(int spriteCount)
        {
            if (_instances != null && _capacity >= spriteCount) return;
            if (_instances != null) _retired.Add(_instances);
            _capacity = Math.Max(spriteCount, _capacity == 0 ? 64 : _capacity * 2);
            _instances = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_capacity * (int)InstanceStride), GpuBufferUsage.VertexBuffer));
            if (_packed.Length < _capacity) _packed = new ParticleInstance[_capacity];
        }

        /// <summary>Pure: pack one sprite into the per-instance attribute struct. Headless-testable.</summary>
        public static ParticleInstance PackInstance(in ParticleSprite s)
        {
            Vector4 flip = Vector4.Zero;
            if (s.Flipbook.IsActive)
            {
                (float frameA, float frameB, float blend) =
                    ResolveFrames(s.FlipbookFrame, s.Flipbook.Columns * s.Flipbook.Rows, s.Flipbook.Loop);
                flip = new Vector4(frameA, frameB, blend,
                    PackFlipGrid(s.Flipbook.Columns, s.Flipbook.Rows, s.Flipbook.MotionStrength));
            }
            return new ParticleInstance
            {
                CenterSize = new Vector4(s.Position, s.Size),
                VelocityRot = new Vector4(s.Velocity, s.Rotation),
                Color = s.Color,
                Shape = new Vector4((int)s.Shape, s.ShapeParam, s.LifeNorm, s.Seed),
                Extra = new Vector4(s.Stretch, s.Blend == BillboardBlend.Additive ? 1f : 0f,
                    (int)s.Orientation, s.SoftFadeScale <= 0f ? 1f : s.SoftFadeScale),
                Flip = flip,
            };
        }

        /// <summary>Pack the flipbook grid and quantized motion strength into one float for the shader's
        /// <c>IFlip.w</c> lane: <c>cols + rows * 256 + qstr * 65536</c> where <c>qstr = round(clamp(strength,0,4) * 64)</c>
        /// capped at 255. The cap keeps the whole packed value at or below 2^24-1 so every field stays exact in
        /// float32 (256 would push the sum past 2^24 and lose the low bits). A value above 0.5 tells the shader the
        /// sprite is a flipbook, procedural sprites pack 0. The shader decodes with the mirror mod/floor math.</summary>
        internal static float PackFlipGrid(int cols, int rows, float motionStrength)
        {
            int c = Math.Clamp(cols, 1, 255);
            int r = Math.Clamp(rows, 1, 255);
            int qstr = Math.Clamp((int)MathF.Round(Math.Clamp(motionStrength, 0f, 4f) * 64f), 0, 255);
            return c + r * 256 + qstr * 65536;
        }

        /// <summary>Pure: resolve a continuous frame position into the two integer frame indices the shader samples
        /// and the blend between them. <paramref name="loop"/> wraps the next frame across the seam (looping
        /// fire/smoke), otherwise both indices clamp on the last frame with blend 0 (one-shot explosion sheets).
        /// Headless-testable, the timing that produces <paramref name="frame"/> lives in the adapter.</summary>
        internal static (float frameA, float frameB, float blend) ResolveFrames(float frame, int frameCount, bool loop)
        {
            if (frameCount <= 1) return (0f, 0f, 0f);
            if (loop)
            {
                float wrapped = frame - MathF.Floor(frame / frameCount) * frameCount; // frame mod frameCount, in [0, frameCount)
                int fa = (int)MathF.Floor(wrapped);
                if (fa >= frameCount) fa = 0;   // guard the fp boundary where wrapped rounds up to frameCount
                float blend = wrapped - fa;
                int fb = (fa + 1) % frameCount;
                return (fa, fb, blend);
            }
            float clamped = Math.Clamp(frame, 0f, frameCount - 1);
            int a = (int)MathF.Floor(clamped);
            if (a >= frameCount - 1) return (frameCount - 1, frameCount - 1, 0f);
            return (a, a + 1, clamped - a);
        }

        /// <summary>Draw the (already back-to-front sorted) sprites into ColorDepthFB. The stream is split into
        /// contiguous runs by atlas pair and each run is one instanced call into the shared packed buffer (base
        /// instance = run start), so an all-procedural frame is still a single draw with the dummy set. Returns the
        /// number of GPU draw calls issued (one per run). <paramref name="resolveTexture"/> maps a TextureHandle list
        /// index to its GPU texture (null for a since-unloaded slot). Caller guarantees the scene depth is resolved
        /// (the fragment samples <see cref="RenderResources.DepthColorTex"/> for the soft fade).</summary>
        public int Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, Vector3 eye, Vector3 right, Vector3 up,
            float timeSeconds, float softFade, ParticleQuality quality, float backgroundDepthMarker,
            ReadOnlySpan<ParticleSprite> sorted, Func<int, IGpuTexture?> resolveTexture)
        {
            if (sorted.Length == 0) return 0;
            EnsureCapacity(sorted.Length);
            EnsureBound(res);

            Matrix4x4.Invert(viewProj, out var inv);
            var frame = new FrameUniforms
            {
                ViewProj = GpuClip.Correct(viewProj, _gd.Capabilities),
                InvViewProj = inv,
                CamRight = new Vector4(right, 0f),
                CamUp = new Vector4(up, 0f),
                CamPosTime = new Vector4(eye, timeSeconds),
                Params = new Vector4(MathF.Max(softFade, 0f), quality == ParticleQuality.Full ? 1f : 0f, backgroundDepthMarker, 0f),
            };
            cl.UpdateBuffer(_frameUbo, 0, in frame);

            for (int i = 0; i < sorted.Length; i++) _packed[i] = PackInstance(sorted[i]);
            cl.UpdateBuffer(_instances!, 0, ((ReadOnlySpan<ParticleInstance>)_packed).Slice(0, sorted.Length));

            if (_runs.Length < sorted.Length) _runs = new ParticleRun[sorted.Length];
            int runCount = BuildRuns(sorted, _runs);

            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetPipeline(_pipeline);
            cl.SetVertexBuffer(0, _instances!);
            for (int r = 0; r < runCount; r++)
            {
                ParticleRun run = _runs[r];
                cl.SetGraphicsResourceSet(0, SetFor(res, run.AtlasIndex, run.MotionIndex, resolveTexture));
                cl.Draw(6, (uint)run.Count, 0, (uint)run.Start);
            }
            return runCount;
        }

        public void Dispose()
        {
            ClearSets();
            _dummyAtlas.Dispose();
            _dummyMv.Dispose();
            _pipeline.Dispose();
            _layout.Dispose(); _shaders.Dispose();
            _frameUbo.Dispose();
            _instances?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
