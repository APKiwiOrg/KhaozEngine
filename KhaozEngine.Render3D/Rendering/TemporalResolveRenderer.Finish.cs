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

        TemporalCountProbe? _countProbe;
        bool _countsArmed;

        /// <summary>Whether the count probe has been built. For tests.</summary>
        internal bool CountProbeBuilt => _countProbe != null;

        /// <summary>How many times the count probe's grid has been read back. For tests.</summary>
        internal int CountReadbacks => _countProbe?.Readbacks ?? 0;

        /// <summary>Arm the probe for the next resolve this renderer records.</summary>
        internal void ArmCounts() => _countsArmed = true;

        /// <summary>The last statement of the method that records the resolve draw: runs an armed probe over the
        /// set the resolve just bound. Every pass after the resolve binds its own framebuffer first, so switching
        /// to the probe's here disturbs nothing.</summary>
        void RecordFinishProbe(IGpuCommandList cl)
        {
            if (!_countsArmed || CurrentSet is not { } set) return;
            _countsArmed = false;
            (_countProbe ??= new TemporalCountProbe(_gd, ResolveLayout)).Record(cl, set);
        }

        /// <summary>Harvest the armed frame's grid (TemporalCountProbe.TryHarvest). Drains on Metal and
        /// Vulkan.</summary>
        internal bool TryHarvestCounts(out int disoccluded, out int reactive, out int clipped)
        {
            disoccluded = reactive = clipped = 0;
            return _countProbe is { } probe && probe.TryHarvest(out disoccluded, out reactive, out clipped);
        }

        void DisposeFinish()
        {
            _debugView?.Dispose();
            _countProbe?.Dispose();
            _debugView = null;
            _countProbe = null;
        }
    }
}
