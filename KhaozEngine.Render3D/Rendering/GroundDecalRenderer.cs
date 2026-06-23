using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the queued <see cref="GroundDecal"/>s as a fullscreen pass per decal into the lit color attachment
    /// (ColorOnlyFB), sampling the linear depth to reconstruct each pixel's surface world position and painting the
    /// decal's analytic shape onto the ground/terrain. Runs after the model+beam passes and before the post chain,
    /// so decals are occluded by geometry (Y-band gate) and flow through quantize/blit. One draw per decal with a
    /// per-decal UBO (no per-instance vertex attributes). Two pipelines: alpha and additive.
    /// </summary>
    internal sealed class GroundDecalRenderer : IDisposable
    {
        /// <summary>160-byte UBO matching the Decal block in <see cref="ShaderSources.DecalFrag"/>
        /// (mat4 + 6 vec4; every member 16-byte aligned, so std140 needs no extra padding).</summary>
        public struct DecalUbo
        {
            public Matrix4x4 InvViewProj; // 64
            public Vector4 Center;        // xyz center, w=rotation
            public Vector4 Size;
            public Vector4 Fill;
            public Vector4 Outline;
            public Vector4 Params;        // x=edge, y=fillFraction, z=flashAdd, w=shapeIndex
            public Vector4 Gate;          // x=groundY, y=yTol, z=maxStep, w=0
        }

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _layout;
        readonly IGpuBuffer _ubo;
        readonly IGpuPipeline _alphaPipe, _additivePipe;
        IGpuResourceSet? _set;
        RenderResources? _bound;
        int _boundW, _boundH;

        public GroundDecalRenderer(IGpuDevice gd, GpuOutputDescription colorOutput)
        {
            _gd = gd;
            var f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.DecalVert, ShaderSources.DecalFrag);
            _ubo = f.CreateBuffer(new GpuBufferDescription(160, GpuBufferUsage.UniformBuffer));
            _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DepthTex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Decal", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            _alphaPipe = Pipe(f, colorOutput, GpuBlendAttachment.AlphaBlend);
            _additivePipe = Pipe(f, colorOutput, GpuBlendAttachment.Additive);
        }

        IGpuPipeline Pipe(IGpuResourceFactory f, GpuOutputDescription outputs, GpuBlendAttachment blend) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { blend },
                // Read-only depth test: the far-plane quad (DecalVert) passes Greater only where stored depth is
                // nearer than the far plane, i.e. only on scene geometry; background (cleared far) is rejected. No
                // depth write, so the scene depth is untouched for any later pass.
                DepthStencil = new GpuDepthStencilState(depthTestEnabled: true, depthWriteEnabled: false, GpuComparison.Greater),
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = outputs,
            });

        void BindTargets(RenderResources res)
        {
            if (ReferenceEquals(_bound, res) && res.Width == _boundW && res.Height == _boundH) return;
            _set?.Dispose();
            _set = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout, res.DepthColorTex, _gd.PointSampler, _ubo));
            _bound = res; _boundW = res.Width; _boundH = res.Height;
        }

        /// <summary>Pure: pack a decal + the (already clip-corrected, inverted) view-projection into the UBO.</summary>
        public static DecalUbo PackUbo(in GroundDecal d, Matrix4x4 invViewProj)
        {
            Vector4 fill = d.FillColor; Vector4 outline = d.OutlineColor;
            return new DecalUbo
            {
                InvViewProj = invViewProj,
                Center = new Vector4(d.Center, d.Rotation),
                Size = d.Size,
                Fill = fill,
                Outline = outline,
                Params = new Vector4(d.EdgeThickness, d.FillFraction, d.FlashAdd, (int)d.Shape),
                Gate = new Vector4(d.Center.Y, d.YTolerance, d.MaxStep, 0f),
            };
        }

        /// <summary>Draw all queued decals into ColorDepthFB (lit color + read-only scene depth). Caller guarantees
        /// the model pass is complete (depth written) and the framebuffer is free to rebind. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, RenderResources res, Matrix4x4 viewProj, ReadOnlySpan<GroundDecal> decals)
        {
            if (decals.Length == 0) return;
            BindTargets(res);
            Matrix4x4 clipVp = GpuClip.Correct(viewProj, _gd.Capabilities);
            Matrix4x4.Invert(clipVp, out var inv);
            for (int i = 0; i < decals.Length; i++)
            {
                var u = PackUbo(decals[i], inv);
                cl.UpdateBuffer(_ubo, 0, in u);
                cl.SetFramebuffer(res.ColorDepthFB);
                cl.SetPipeline(decals[i].Blend == DecalBlend.Additive ? _additivePipe : _alphaPipe);
                cl.SetGraphicsResourceSet(0, _set!);
                cl.Draw(3);
            }
        }

        public void Dispose()
        {
            _set?.Dispose();
            _alphaPipe.Dispose(); _additivePipe.Dispose();
            _layout.Dispose(); _shaders.Dispose(); _ubo.Dispose();
        }
    }
}
