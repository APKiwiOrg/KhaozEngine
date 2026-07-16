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
        /// <see cref="ShaderSources.ParticleVert"/> (5 x vec4 = 80 bytes, every member 16-byte aligned).</summary>
        public struct ParticleInstance
        {
            public Vector4 CenterSize;    // xyz world center, w half-size
            public Vector4 VelocityRot;   // xyz world velocity, w rotation (radians)
            public Vector4 Color;         // straight rgba
            public Vector4 Shape;         // x shape id, y shape param, z life norm, w seed
            public Vector4 Extra;         // x stretch, y additivity (0 alpha / 1 additive), z orientation, w soft-fade scale
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
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundW, _boundH;

        static readonly uint InstanceStride = (uint)Unsafe.SizeOf<ParticleInstance>();

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
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
            _frameUbo = f.CreateBuffer(new GpuBufferDescription(192, GpuBufferUsage.UniformBuffer));
            _pipeline = Pipe(f, colorOutput);
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
                // One instance-rate vertex stream carrying the five per-sprite vec4 attributes (locations 0..4,
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
                        }),
                },
                Outputs = outputs,
            });

        void BindTargets(RenderResources res)
        {
            if (_set != null && ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _frameUbo, res.DepthColorTex, _gd.PointSampler));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
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
        public static ParticleInstance PackInstance(in ParticleSprite s) => new()
        {
            CenterSize = new Vector4(s.Position, s.Size),
            VelocityRot = new Vector4(s.Velocity, s.Rotation),
            Color = s.Color,
            Shape = new Vector4((int)s.Shape, s.ShapeParam, s.LifeNorm, s.Seed),
            Extra = new Vector4(s.Stretch, s.Blend == BillboardBlend.Additive ? 1f : 0f,
                (int)s.Orientation, s.SoftFadeScale <= 0f ? 1f : s.SoftFadeScale),
        };

        /// <summary>Draw the (already back-to-front sorted) sprites as one instanced call into ColorDepthFB.
        /// Returns the number of GPU draw calls issued (0 or 1). Caller guarantees the scene depth is resolved
        /// (the fragment samples <see cref="RenderResources.DepthColorTex"/> for the soft fade).</summary>
        public int Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, Vector3 eye, Vector3 right, Vector3 up,
            float timeSeconds, float softFade, ParticleQuality quality, float backgroundDepthMarker, ReadOnlySpan<ParticleSprite> sorted)
        {
            if (sorted.Length == 0) return 0;
            EnsureCapacity(sorted.Length);
            BindTargets(res);

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

            cl.SetFramebuffer(res.ColorDepthFB);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set!);
            cl.SetVertexBuffer(0, _instances!);
            cl.Draw(6, (uint)sorted.Length, 0, 0);
            return 1;
        }

        public void Dispose()
        {
            _set?.Dispose();
            _pipeline.Dispose();
            _layout.Dispose(); _shaders.Dispose();
            _frameUbo.Dispose();
            _instances?.Dispose();
            foreach (var r in _retired) r.Dispose();
            _retired.Clear();
        }
    }
}
