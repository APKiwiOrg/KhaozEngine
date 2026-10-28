using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// The History, Disocclusion, Reactive and Shimmer debug views (<see cref="ShaderSources.TemporalDebugFrag"/>): one
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
            SceneDebugView.History, SceneDebugView.Disocclusion, SceneDebugView.Reactive, SceneDebugView.Shimmer,
        };

        readonly IGpuResourceLayout _modeLayout;
        readonly IGpuBuffer[] _modeBuffers = new IGpuBuffer[4];
        readonly IGpuResourceSet[] _modeSets = new IGpuResourceSet[4];
        readonly TemporalVisibilityPipelinePair _programs;

        public TemporalDebugViewPass(IGpuDevice gd, IGpuResourceLayout resolveLayout,
            IGpuResourceLayout fullyLitLayout, GpuOutputDescription targetOutput, Func<string, string>? rewrite = null)
        {
            IGpuResourceFactory f = gd.Factory;
            _modeLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("DebugView", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            for (int i = 0; i < Views.Length; i++)
            {
                _modeBuffers[i] = f.CreateBuffer(new GpuBufferDescription(DebugViewBufferBytes,
                    GpuBufferUsage.UniformBuffer));
                gd.UpdateBuffer(_modeBuffers[i], 0, new DebugViewUbo { Mode = new Vector4((int)Views[i], 0f, 0f, 0f) });
                _modeSets[i] = f.CreateResourceSet(new GpuResourceSetDescription(_modeLayout, _modeBuffers[i]));
            }
            _programs = new TemporalVisibilityPipelinePair(gd,
                ShaderSources.TemporalDebugFragment(TemporalResolvePrecisionPolicy.For(gd)),
                [resolveLayout, _modeLayout], [fullyLitLayout, _modeLayout], targetOutput, rewrite);
        }

        /// <summary>Replace <paramref name="target"/>'s image with <paramref name="view"/>. Any other view draws
        /// nothing.</summary>
        public void Draw(IGpuCommandList cl, SceneDebugView view, IGpuResourceSet resolveSet, IGpuFramebuffer target, bool withoutVisibility = false)
        {
            int slot = view switch
            {
                SceneDebugView.History => 0,
                SceneDebugView.Disocclusion => 1,
                SceneDebugView.Reactive => 2,
                SceneDebugView.Shimmer => 3,
                _ => -1,
            };
            if (slot < 0) return;
            cl.SetFramebuffer(target);
            cl.SetPipeline(_programs.Select(withoutVisibility));
            cl.SetGraphicsResourceSet(0, resolveSet);
            cl.SetGraphicsResourceSet(1, _modeSets[slot]);
            cl.Draw(3);
        }

        public void Dispose()
        {
            _programs.Dispose();
            for (int i = 0; i < _modeSets.Length; i++) { _modeSets[i].Dispose(); _modeBuffers[i].Dispose(); }
            _modeLayout.Dispose();
        }
    }
}
