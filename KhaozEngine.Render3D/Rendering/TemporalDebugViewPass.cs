using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The History, Disocclusion and Reactive debug views (<see cref="ShaderSources.TemporalDebugFrag"/>): one
    /// fullscreen pass over the final target, bound to the set the resolve used this frame plus an immutable mode
    /// block per view. The mode blocks are written once at construction through the device-level
    /// <c>IGpuDevice.UpdateBuffer</c>, which reaches every copy of a ring-backed uniform buffer, so drawing a view
    /// uploads nothing and needs no pass closed for it. The only other uniform buffer it reads is the resolve's own, written earlier in the same
    /// recording.
    /// </summary>
    internal sealed class TemporalDebugViewPass : IDisposable
    {
        internal struct DebugViewUbo { public Vector4 Mode; }   // .x = the SceneDebugView value
        internal const uint DebugViewBufferBytes = 16;

        static readonly SceneDebugView[] Views =
        {
            SceneDebugView.History, SceneDebugView.Disocclusion, SceneDebugView.Reactive,
        };

        readonly IGpuShaderSet _shaders;
        readonly IGpuResourceLayout _modeLayout;
        readonly IGpuBuffer[] _modeBuffers = new IGpuBuffer[3];
        readonly IGpuResourceSet[] _modeSets = new IGpuResourceSet[3];
        readonly IGpuPipeline _pipeline;

        public TemporalDebugViewPass(IGpuDevice gd, IGpuResourceLayout resolveLayout, GpuOutputDescription targetOutput)
        {
            IGpuResourceFactory f = gd.Factory;
            _shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalDebugFrag);
            _modeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DebugView", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            for (int i = 0; i < Views.Length; i++)
            {
                _modeBuffers[i] = f.CreateBuffer(new GpuBufferDescription(DebugViewBufferBytes,
                    GpuBufferUsage.UniformBuffer));
                gd.UpdateBuffer(_modeBuffers[i], 0, new DebugViewUbo { Mode = new Vector4((int)Views[i], 0f, 0f, 0f) });
                _modeSets[i] = f.CreateResourceSet(new GpuResourceSetDescription(_modeLayout, _modeBuffers[i]));
            }
            _pipeline = f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },   // replaces the final image
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { resolveLayout, _modeLayout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),   // fullscreen triangle from gl_VertexIndex
                Outputs = targetOutput,
            });
        }

        /// <summary>Replace <paramref name="target"/>'s image with <paramref name="view"/>. Any other view draws
        /// nothing.</summary>
        public void Draw(IGpuCommandList cl, SceneDebugView view, IGpuResourceSet resolveSet, IGpuFramebuffer target)
        {
            int slot = view switch
            {
                SceneDebugView.History => 0,
                SceneDebugView.Disocclusion => 1,
                SceneDebugView.Reactive => 2,
                _ => -1,
            };
            if (slot < 0) return;
            cl.SetFramebuffer(target);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, resolveSet);
            cl.SetGraphicsResourceSet(1, _modeSets[slot]);
            cl.Draw(3);
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            for (int i = 0; i < _modeSets.Length; i++) { _modeSets[i].Dispose(); _modeBuffers[i].Dispose(); }
            _modeLayout.Dispose();
            _shaders.Dispose();
        }
    }
}
