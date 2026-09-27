using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The finishing members on the resolve, starting with the debug views. Each evaluates the resolve's own
    /// per-pixel function over the set the resolve bound this frame and is built on first use, so a scene that asks
    /// for none of them owns none of them.</summary>
    internal sealed partial class TemporalResolveRenderer
    {
        TemporalDebugViewPass? _debugView;

        /// <summary>Whether the debug view pass has been built. For tests.</summary>
        internal bool DebugViewBuilt => _debugView != null;

        /// <summary>Replace <paramref name="target"/>'s image with <paramref name="view"/>, reading this frame's
        /// resolve inputs through <see cref="CurrentSet"/>. Does nothing before the resolve has bound a set. The
        /// caller calls it only on a render that ran <see cref="Run"/>, so the set and the uniforms are that
        /// render's.</summary>
        internal void DrawDebugView(IGpuCommandList cl, SceneDebugView view, IGpuFramebuffer target,
            GpuOutputDescription targetOutput)
        {
            if (CurrentSet is not { } set) return;
            (_debugView ??= new TemporalDebugViewPass(_gd, ResolveLayout, targetOutput)).Draw(cl, view, set, target);
        }

        void DisposeFinish()
        {
            _debugView?.Dispose();
            _debugView = null;
        }
    }
}
