using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The switch to the two-pass resolve (<see cref="TemporalSplitResolve"/>). With it off, which is the
    /// default unless <see cref="TemporalResolvePath"/> selected it, the renderer builds and records nothing of
    /// it.</summary>
    internal sealed partial class TemporalResolveRenderer
    {
        TemporalSplitResolve? _split;

        /// <summary>Whether <see cref="Run"/> records the two-pass resolve in place of the resolve and the depth store.
        /// It may change between frames: both write the same history and previous depth.</summary>
        internal bool Split { get; set; } = TemporalResolvePath.SplitFromEnvironment;

        /// <summary>The two-pass resolve's objects once built. For tests.</summary>
        internal TemporalSplitResolve? SplitResolveForTests => _split;

        // BindInputs' first statement: the two-pass resolve keeps its own binding, so switching to it on any frame
        // finds its sets built.
        void BindSplit(in TemporalResolveInputs inputs, TemporalHistory history)
        {
            if (!Split) return;
            (_split ??= new TemporalSplitResolve(_gd)).Bind(inputs, history, _clampSampler, _resolveBuffer);
        }

        // Run's branch: records the two passes when the switch is on and says whether it did.
        bool RunSplit(IGpuCommandList cl, TemporalHistory history)
        {
            if (!Split || _split is null) return false;
            _split.Run(cl, history);
            return true;
        }
    }
}
