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
    /// Draws the frame's queued <see cref="DistortionSprite"/>s as ONE instanced draw into the lazily allocated
    /// half/quarter-res <c>R16G16Float</c> offset field (<see cref="RenderResources.DistortFB"/>), after the scene
    /// colour resolves and before the post chain. Each sprite's parameters ride a per-instance vertex attribute
    /// stream (the Metal-safe instancing pattern the particle pass proves in production), the quad is expanded from
    /// <c>gl_VertexIndex</c> in the vertex shader, and the procedural shape + soft depth occlusion are evaluated in
    /// the fragment shader. A single pipeline with a plain additive blend accumulates overlapping offset fields. The
    /// pipeline has NO depth test (the half-res target has no depth attachment) - occlusion is done in the fragment
    /// via the particle pass's texelFetch depth recipe, scaling the half-res <c>gl_FragCoord</c> up to the full-res
    /// depth texel. The written field is re-sampled by <see cref="PixelPostProcess"/>'s apply pass, so distortion
    /// warps the scene as the FIRST post pass.
    /// </summary>
    internal sealed class DistortionRenderer : IDisposable
    {
        /// <summary>Per-instance sprite attributes, matching the <c>I*</c> inputs of
        /// <see cref="ShaderSources.DistortionVert"/> (3 x vec4 = 48 bytes, every member 16-byte aligned).</summary>
        public struct DistortionInstance
        {
            public Vector4 CenterSize;   // xyz world center, w half-size
            public Vector4 ShapeLife;    // x shape id, y shape param, z life norm, w seed
            public Vector4 Extra;        // x strength, y rotation (radians), z orientation, w soft-fade scale
        }

        /// <summary>The single per-frame uniform block, declared identically in both shader stages (ONE uniform
        /// buffer per pipeline, the Metal contract). 192 bytes. Mirrors the particle pass's Frame block, with
        /// Params.w carrying the half-res-to-full-res texel ratio (the distortion field is downscaled).</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct FrameUniforms
        {
            public Matrix4x4 ViewProj;      // GpuClip-corrected
            public Matrix4x4 InvViewProj;   // RAW (un-clip-corrected), matching Camera.ScreenToRay
            public Vector4 CamRight;
            public Vector4 CamUp;
            public Vector4 CamPosTime;      // xyz eye, w effect time seconds
            public Vector4 Params;          // x soft-fade distance (0 off), y quality (1 full / 0 reduced), z background depth marker, w half->full texel ratio
        }

        /// <summary>Byte size of the Frame UBO, exposed so UboLayoutTests can size-check the struct against it.</summary>
        internal const uint FrameBufferBytes = 192;

        // The distortion offset target is always a single R16G16Float colour attachment, no depth, single sample. It
        // is never multisampled (a half-res post-style field), so this output never changes and there is no SetOutputs.
        static readonly GpuOutputDescription OffsetOutput = new(null, GpuPixelFormat.R16G16Float);

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuPipeline _pipeline;
        readonly IGpuBuffer _frameUbo;
        IGpuBuffer? _instances;
        // Grown-out instance buffers (a prior frame's submitted command list may still be reading one), freed in
        // Dispose. The engine's buffer-lifetime rule, stated in ModelRenderer.EnsureInstanceCapacity.
        readonly List<IDisposable> _retired = new();
        int _capacity;
        DistortionInstance[] _packed = Array.Empty<DistortionInstance>();
        // One cached resource set over the resolved depth texture, rebuilt on a target rebind (generation bump).
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundGen;

        static readonly uint InstanceStride = (uint)Unsafe.SizeOf<DistortionInstance>();

        // Plain additive blend: overlapping offset fields sum. The target has no alpha lane (R16G16Float), so the
        // alpha factors are inert, the colour factors (One, One) do the accumulation.
        static GpuBlendAttachment Additive => new(
            true,
            GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFunction.Add,
            GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFunction.Add);

        public DistortionRenderer(IGpuDevice gd)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.DistortionVert, ShaderSources.DistortionFrag);
            // Binding order matches the fragment shader exactly: Frame(0), DepthTex(1), Samp(2). The sampler is point:
            // the depth is texelFetched, no filtering.
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            _frameUbo = f.CreateBuffer(new GpuBufferDescription(FrameBufferBytes, GpuBufferUsage.UniformBuffer));
            _pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { Additive },
                // No depth attachment on the offset target, so no depth test at the pipeline level. Occlusion is a
                // fragment-side offset fade.
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                // One instance-rate vertex stream carrying the three per-sprite vec4 attributes (locations 0..2, no
                // holes, every attribute consumed by the vertex stage: the D3D11 contiguous-input contract).
                VertexLayouts = new System.Collections.Generic.List<GpuVertexLayoutDescription>
                {
                    new GpuVertexLayoutDescription(
                        stride: InstanceStride,
                        instanceStepRate: 1,
                        elements: new[]
                        {
                            new GpuVertexElement("ICenterSize", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IShapeLife", GpuVertexElementFormat.Float4),
                            new GpuVertexElement("IExtra", GpuVertexElementFormat.Float4),
                        }),
                },
                Outputs = OffsetOutput,
            });
        }

        void EnsureCapacity(int spriteCount)
        {
            if (_instances != null && _capacity >= spriteCount) return;
            // Retire, never dispose inline. The frame path has no WaitForIdle, so the CPU can be several frames
            // ahead of the GPU and a prior frame's command list may still be reading the buffer this grow
            // replaces: freeing it here is a use-after-free. Geometric growth bounds how many pile up.
            if (_instances != null) _retired.Add(_instances);
            _capacity = Math.Max(spriteCount, _capacity == 0 ? 64 : _capacity * 2);
            _instances = _gd.Factory.CreateBuffer(new GpuBufferDescription((uint)(_capacity * (int)InstanceStride), GpuBufferUsage.VertexBuffer));
            if (_packed.Length < _capacity) _packed = new DistortionInstance[_capacity];
        }

        // Rebuild the cached depth resource set when the render target rebinds (generation guard, not dimensions: a
        // same-size recreate also invalidates DepthColorTex, see RenderResources.Generation - which EnsureDistortion
        // bumps on (re)allocate/free too).
        IGpuResourceSet SetFor(RenderResources res)
        {
            if (_set != null && ReferenceEquals(_bound, res) && res.Generation == _boundGen) return _set;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _frameUbo, res.DepthColorTex, _gd.PointSampler));
            _bound = res; _boundGen = res.Generation;
            return _set;
        }

        /// <summary>Pure: pack one sprite into the per-instance attribute struct. Headless-testable.</summary>
        public static DistortionInstance PackInstance(in DistortionSprite s) => new()
        {
            CenterSize = new Vector4(s.Position, s.Size),
            ShapeLife = new Vector4((int)s.Shape, s.ShapeParam, s.LifeNorm, s.Seed),
            Extra = new Vector4(s.Strength, s.Rotation, (int)s.Orientation, s.SoftFadeScale <= 0f ? 1f : s.SoftFadeScale),
        };

        /// <summary>Clear the offset field to zero then draw the queued distortion sprites as one instanced call into
        /// <see cref="RenderResources.DistortFB"/> (the clear + additive accumulation share one render pass). No
        /// ordering needed: the additive accumulation is order-independent. <paramref name="resRatio"/> is the
        /// full-res-to-offset-res texel ratio (2 for half, 4 for quarter) the fragment uses to index the full-res
        /// depth. Returns the number of GPU draw calls issued (0 or 1). Caller guarantees the scene depth is resolved
        /// (the fragment samples <see cref="RenderResources.DepthColorTex"/>).</summary>
        public int Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, Vector3 eye, Vector3 right, Vector3 up,
            float timeSeconds, float softFade, DistortionQuality quality, float backgroundDepthMarker, float resRatio,
            ReadOnlySpan<DistortionSprite> sprites)
        {
            if (sprites.Length == 0 || res.DistortFB == null) return 0;
            EnsureCapacity(sprites.Length);

            Matrix4x4.Invert(viewProj, out var inv);
            var frame = new FrameUniforms
            {
                ViewProj = GpuClip.Correct(viewProj, _gd.Capabilities),
                InvViewProj = inv,
                CamRight = new Vector4(right, 0f),
                CamUp = new Vector4(up, 0f),
                CamPosTime = new Vector4(eye, timeSeconds),
                Params = new Vector4(MathF.Max(softFade, 0f), quality == DistortionQuality.Full ? 1f : 0f, backgroundDepthMarker, resRatio),
            };
            cl.UpdateBuffer(_frameUbo, 0, in frame);

            for (int i = 0; i < sprites.Length; i++) _packed[i] = PackInstance(sprites[i]);
            cl.UpdateBuffer(_instances!, 0, ((ReadOnlySpan<DistortionInstance>)_packed).Slice(0, sprites.Length));

            cl.SetFramebuffer(res.DistortFB);
            // Clear the offset field to zero at the start of this pass (offsets accumulate additively, so a stale
            // field would smear last frame's warp). Clear + draw stay one render pass.
            cl.ClearColorTarget(0, Color.Transparent);
            cl.SetPipeline(_pipeline);
            cl.SetVertexBuffer(0, _instances!);
            cl.SetGraphicsResourceSet(0, SetFor(res));
            cl.Draw(6, (uint)sprites.Length, 0, 0);
            return 1;
        }

        public void Dispose()
        {
            _set?.Dispose();
            _pipeline.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _frameUbo.Dispose();
            _instances?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
