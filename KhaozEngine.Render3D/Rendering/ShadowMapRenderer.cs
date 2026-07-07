using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The key-light directional shadow map: a depth-only pass that renders the instanced casters into an
    /// orthographic light-space depth texture (a single R32F colour target, so the receivers sample it as a plain
    /// texture2D and manual-PCF depth-compare - no depth-sampling / comparison-sampler seam). The receivers
    /// (<see cref="ModelRenderer"/>'s model + splat fragments) bind this map + a clamp sampler and shadow the key
    /// light through the shared lighting block.
    /// </summary>
    /// <remarks>
    /// The depth map is allocated once at the requested resolution (default 2048, quality-scaled) and reused every
    /// frame; a resolution change reallocates it. It is created up front (even before the tier is switched on) so the
    /// material resource sets can bind a STABLE texture handle - the shader gates on <c>ShadowParams.w</c> (strength),
    /// so an inactive frame never taps it and stays byte-identical to ShadowMode.Off. The depth pass reuses the model
    /// pass's instance buffer (no second upload): the light UBO holds only the world-&gt;light-clip matrix; each
    /// caster draws with its own instance slice.
    /// </remarks>
    internal sealed class ShadowMapRenderer : IDisposable
    {
        /// <summary>Default shadow-map resolution per axis (a game may scale it down via
        /// <see cref="ShadowSettings.ShadowMapResolution"/>).</summary>
        public const int DefaultResolution = 2048;
        const int MinResolution = 256;

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuBuffer _lightUbo;          // 64 bytes: the light ViewProj matrix
        readonly IGpuResourceSet _set;
        readonly IGpuSampler _sampler;          // clamp/linear sampler the RECEIVERS use to PCF-sample the map (owned)
        IGpuPipeline _pipeline = null!;

        IGpuTexture _depthColor = null!;        // R32F: the caster's light-space depth (the map the receivers sample)
        IGpuTexture _depthStencil = null!;      // depth-test buffer for the depth pass (never sampled)
        IGpuFramebuffer _fb = null!;
        int _resolution;

        /// <summary>The shadow-map texture the receivers sample (R32F light-space depth). Stable handle across
        /// frames; only reallocated on a resolution change (see <see cref="EnsureResolution"/>).</summary>
        public IGpuTexture ShadowTexture => _depthColor;

        /// <summary>The clamp/linear sampler the receivers PCF-sample the map with (owned here).</summary>
        public IGpuSampler ShadowSampler => _sampler;

        /// <summary>The current allocated resolution per axis.</summary>
        public int Resolution => _resolution;

        public ShadowMapRenderer(IGpuDevice gd, int resolution)
        {
            _gd = gd;
            var f = gd.Factory;

            _shaders = f.CreateShadersFromSpirv(ShaderSources.ShadowDepthVert, ShaderSources.ShadowDepthFrag);
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            _lightUbo = f.CreateBuffer(new GpuBufferDescription(64, GpuBufferUsage.UniformBuffer));
            _set = f.CreateResourceSet(new GpuResourceSetDescription(_layout, _lightUbo));

            // Clamp addressing so a PCF tap off the map edge reads the border (never wraps into the far side); linear
            // so the 3x3 taps blend smoothly. Clamp-to-edge keeps an out-of-footprint receiver reading a valid texel
            // (the shader also range-checks the UV and early-outs, so the exact border value is not load-bearing).
            _sampler = f.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.MinLinearMagLinearMipLinear,
                GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp, GpuSamplerAddress.Clamp));

            _resolution = 0;
            EnsureResolution(resolution);
        }

        /// <summary>(Re)allocate the depth targets + pipeline for <paramref name="resolution"/> (clamped to a sane
        /// minimum) if it changed. The map handle changes on a realloc, so callers that bound the old texture into a
        /// resource set must rebuild it (ModelRenderer rebuilds its material sets' shadow binding lazily).</summary>
        public void EnsureResolution(int resolution)
        {
            int res = Math.Max(MinResolution, resolution);
            if (res == _resolution) return;
            _gd.WaitForIdle();   // a prior frame's pass may still reference the old targets; a resolution change is rare
            _pipeline?.Dispose();
            _fb?.Dispose();
            _depthColor?.Dispose();
            _depthStencil?.Dispose();

            _resolution = res;
            uint u = (uint)res;
            var f = _gd.Factory;
            _depthColor = f.CreateTexture(GpuTextureDescription.Texture2D(
                u, u, GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _depthStencil = f.CreateTexture(GpuTextureDescription.Texture2D(
                u, u, GpuPixelFormat.D32FloatS8UInt, GpuTextureUsage.DepthStencil));
            _fb = f.CreateFramebuffer(_depthStencil, _depthColor);

            _pipeline = BuildPipeline(f, _fb.Outputs);
        }

        IGpuPipeline BuildPipeline(IGpuResourceFactory f, GpuOutputDescription outputs)
        {
            // Slot 0: per-vertex geometry (locations 0..4) - same layout the model pass uses, so the shared model
            // vertex buffer binds unchanged (only Position is read).
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
            // Slot 1: the model pass's per-instance stream (locations 5..11), reused verbatim - no second upload.
            var instanceLayout = new GpuVertexLayoutDescription(
                stride: ModelRenderer.InstanceData.SizeInBytes,
                instanceStepRate: 1,
                elements: new[]
                {
                    new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ITint", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IEmissive", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ISpecParams", GpuVertexElementFormat.Float4),
                });

            return f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                // Cull FRONT faces (draw back faces) so the stored depth is the caster's FAR side. This is the classic
                // second-depth trick that lets the constant/slope bias defeat self-shadow acne on the lit front faces
                // without peter-panning. Falls back gracefully for open/non-manifold meshes (they simply store their
                // single face). depthClip on so nothing past the light far plane writes.
                Rasterizer = new GpuRasterizerState(GpuFaceCull.Front, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = outputs,
            });
        }

        /// <summary>Begin the depth pass: bind + clear the shadow framebuffer, upload the light matrix, bind the
        /// pipeline. Clear the R32F map to 1.0 (the far plane) so an unwritten texel reads "nothing in front", i.e.
        /// unshadowed. <paramref name="lightViewProj"/> is the GPU-clip-corrected world-&gt;light-clip matrix.</summary>
        public void BeginDepthPass(IGpuCommandList cl, Matrix4x4 lightViewProj)
        {
            cl.UpdateBuffer(_lightUbo, 0, in lightViewProj);
            cl.SetFramebuffer(_fb);
            cl.ClearColorTarget(0, new Color(1f, 1f, 1f, 1f));  // 1.0 = far plane = no caster
            cl.ClearDepthStencil(1f);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
        }

        /// <summary>Draw one caster run into the shadow map: <paramref name="instanceCount"/> instances from
        /// <paramref name="instanceStart"/> of the shared model instance buffer (<paramref name="instanceBuffer"/>).
        /// The mesh's own vertex + index buffers supply geometry.</summary>
        public void DrawCasterRun(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            GpuIndexFormat indexFormat, IGpuBuffer instanceBuffer, uint instanceStart, uint instanceCount)
        {
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, instanceBuffer);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Draw one CPU-skinned caster into the shadow map: its deformed vertices live at
        /// <paramref name="baseVertex"/>.. in the shared skinned vertex buffer, and its instance data is element
        /// <paramref name="drawIndex"/> of the skinned instance buffer (both supplied by the model pass).</summary>
        public void DrawSkinnedCaster(IGpuCommandList cl, IGpuBuffer skinnedVb, IGpuBuffer skinnedInstanceBuffer,
            IGpuBuffer ib, int indexCount, GpuIndexFormat indexFormat, int baseVertex, uint drawIndex)
        {
            cl.SetVertexBuffer(0, skinnedVb);
            cl.SetVertexBuffer(1, skinnedInstanceBuffer);
            cl.SetIndexBuffer(ib, indexFormat);
            cl.DrawIndexed((uint)indexCount, 1, 0, baseVertex, drawIndex);
        }

        public void Dispose()
        {
            _pipeline?.Dispose();
            _fb?.Dispose();
            _depthColor?.Dispose();
            _depthStencil?.Dispose();
            _set.Dispose();
            _lightUbo.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            _sampler.Dispose();
        }
    }
}
