using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Shared GPU plumbing for the immediate-mode debug/overlay renderers (lines, filled triangles, billboards).
    /// Owns a 64-byte mat4 ViewProj UBO for debug lines/fills, or two 80-byte ViewProj/FogBlend buffers
    /// for fogged billboards, plus their resource sets, the shader set, and one
    /// growable vertex buffer; creates one pipeline per supplied blend attachment (index 0 is the primary one).
    /// <see cref="Draw"/> uploads a vertex span and the view-projection and draws on top of an already-rendered
    /// target with depth disabled. The concrete overlay renderers (<see cref="LineRenderer"/>,
    /// <see cref="FillRenderer"/>, <see cref="BillboardRenderer"/>) are thin configs over this, so a fourth
    /// overlay primitive is a small wrapper rather than another copy of this plumbing.
    /// </summary>
    /// <typeparam name="TVertex">The vertex struct (position + colour, optionally UV). Must be a blittable
    /// value type whose layout matches <c>vertexLayout</c> and whose size is <c>stride</c>.</typeparam>
    internal sealed class OverlayRenderer<TVertex> : IDisposable where TVertex : unmanaged
    {
        readonly IGpuDevice _gd;
        readonly AtmosphereBindings? _atmosphere;
        readonly IGpuBuffer[] _ubos;
        readonly IGpuResourceSet[] _sets;
        readonly IGpuResourceLayout _layout;
        readonly IGpuShaderSet _shaders;
        readonly IGpuPipeline[] _pipelines;    // one per blend mode; [0] is the default
        readonly uint _stride;                 // vertex size in bytes

        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        /// <summary>Build the overlay pipeline(s). One pipeline is created per entry in <paramref name="blends"/>
        /// (e.g. billboards pass alpha then additive); index 0 is the default used by <see cref="Draw"/>.</summary>
        public OverlayRenderer(IGpuDevice gd, GpuOutputDescription targetOutput, string vertSpirv, string fragSpirv,
            GpuVertexLayoutDescription vertexLayout, uint stride, GpuPrimitiveTopology topology,
            params GpuBlendAttachment[] blends)
            : this(gd, targetOutput, vertSpirv, fragSpirv, vertexLayout, stride, topology, null, blends) { }

        public OverlayRenderer(IGpuDevice gd, GpuOutputDescription targetOutput, string vertSpirv, string fragSpirv,
            GpuVertexLayoutDescription vertexLayout, uint stride, GpuPrimitiveTopology topology,
            AtmosphereBindings? atmosphere = null, params GpuBlendAttachment[] blends)
        {
            if (blends is null || blends.Length == 0)
                throw new ArgumentException("At least one blend attachment is required.", nameof(blends));

            _gd = gd;
            _stride = stride;
            var factory = gd.Factory;

            _atmosphere = atmosphere;
            var uniform = new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer,
                atmosphere is null ? GpuShaderStages.Vertex : GpuShaderStages.Vertex | GpuShaderStages.Fragment);
            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                atmosphere is null ? new[] { uniform } : AtmosphereBindings.AppendLayout(uniform)));
            // Fogged billboards use the existing alpha/additive pipeline indices, with immutable blend lanes.
            _ubos = new IGpuBuffer[atmosphere is null ? 1 : 2];
            _sets = new IGpuResourceSet[_ubos.Length];
            for (int i = 0; i < _ubos.Length; i++)
            {
                _ubos[i] = factory.CreateBuffer(new GpuBufferDescription(atmosphere is null ? 64u : 80u,
                    GpuBufferUsage.UniformBuffer));
                if (atmosphere is not null)
                {
                    var uniforms = new AtmosphereBindings.FogBlendUniforms { ViewProj = Matrix4x4.Identity, FogBlend = new Vector4(i, 0, 0, 0) };
                    gd.UpdateBuffer(_ubos[i], 0, in uniforms);
                }
                _sets[i] = factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
                    atmosphere is null ? new IGpuBindableResource[] { _ubos[i] } : atmosphere.AppendResources(_ubos[i])));
            }

            _shaders = factory.CreateShadersFromSpirv(vertSpirv, fragSpirv);

            _pipelines = new IGpuPipeline[blends.Length];
            for (int i = 0; i < blends.Length; i++)
            {
                _pipelines[i] = factory.CreateGraphicsPipeline(new GpuPipelineDescription
                {
                    BlendFactor = Vector4.Zero,
                    BlendAttachments = new[] { blends[i] },
                    DepthStencil = GpuDepthStencilState.Disabled,
                    Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                    Topology = topology,
                    ResourceLayouts = new[] { _layout },
                    ShaderSet = _shaders,
                    VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                    Outputs = targetOutput,
                });
            }
        }

        /// <summary>Draw <paramref name="verts"/> into <paramref name="target"/> (no clear; this is an overlay),
        /// transformed by <paramref name="viewProj"/>, using pipeline <paramref name="pipelineIndex"/> (0 = default).
        /// No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, Matrix4x4 viewProj, ReadOnlySpan<TVertex> verts, IGpuFramebuffer target,
            int pipelineIndex = 0)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);
            // Clip-Y derived from the live backend (identity on Metal/D3D, flips on Vulkan) - see GpuClip.
            var clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            foreach (var ubo in _ubos) cl.UpdateBuffer(ubo, 0, in clipVp);

            cl.SetFramebuffer(target);
            cl.SetPipeline(_pipelines[pipelineIndex]);
            cl.SetGraphicsResourceSet(0, _sets[_atmosphere is null ? 0 : pipelineIndex]);
            cl.SetVertexBuffer(0, _vb!);
            cl.Draw((uint)verts.Length, 1, 0, 0);
        }

        void EnsureCapacity(uint vertexCount)
        {
            if (_vb != null && _vbCapacity >= vertexCount) return;
            _vb?.Dispose();
            // Grow with a little headroom so a slowly-growing overlay doesn't recreate every frame.
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 256u : _vbCapacity * 2);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(_vbCapacity * _stride, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            foreach (var p in _pipelines) p.Dispose();
            foreach (var set in _sets) set.Dispose();
            _layout.Dispose();
            _shaders.Dispose();
            foreach (var ubo in _ubos) ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
