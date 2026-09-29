using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The resolve's two entry points (<see cref="TemporalResolveEntry"/>). The scene chooses one per
    /// resolving render (<see cref="SelectEntry"/>) from <see cref="TemporalResolvePolicy"/>. Both write the same
    /// history pair and previous depth, so the choice may change from one frame to the next. Their histories match bit
    /// for bit wherever <c>TemporalEntryIdentityGpuTests</c> passes, which it does on Metal, on a Tesla T4 on
    /// Direct3D 11 and Vulkan, and on WARP. On a software Vulkan device (llvmpipe) the state matches and the colour
    /// stays within a measured bound. The fused entry point's
    /// objects are built on its first frame, and the split's (<see cref="TemporalSplitResolve"/>) on its first frame,
    /// whose targets it retires on any frame that does not record it.</summary>
    internal sealed partial class TemporalResolveRenderer
    {
        TemporalSplitResolve? _split;

        /// <summary>The entry point <see cref="BindInputs"/> and <see cref="Run"/> use. A renderer starts on the one
        /// <see cref="TemporalResolvePolicy.EnvironmentVariable"/> forces, else the fused one, until
        /// <see cref="SelectEntry"/>.</summary>
        internal TemporalResolveEntry Entry { get; private set; } =
            TemporalResolvePolicy.Forced ?? TemporalResolveEntry.Fused;

        /// <summary>The entry point the last <see cref="Run"/> recorded, null before the first.</summary>
        internal TemporalResolveEntry? LastEntry { get; private set; }

        /// <summary>The split's objects once built. For tests.</summary>
        internal TemporalSplitResolve? SplitResolveForTests => _split;

        /// <summary>Whether the split's targets exist.</summary>
        internal bool SplitTargetsAllocated => _split?.TargetsAllocated == true;

        /// <summary>Choose the entry point for the next <see cref="BindInputs"/> and <see cref="Run"/>. Choosing the
        /// fused one retires the split's targets and sets into <paramref name="retired"/>, so a frame that records the
        /// fused entry point holds none of them.</summary>
        internal void SelectEntry(TemporalResolveEntry entry, GpuRetireQueue retired)
        {
            Entry = entry;
            if (entry == TemporalResolveEntry.Fused) _split?.ReleaseTargets(retired);
        }

        /// <summary>The fragment programs of the draws <see cref="Run"/> records for an entry point, in order, before
        /// any count probe. For tests that find the resolve in recorded commands.</summary>
        internal static string[] EntryFragments(TemporalResolveEntry entry) => entry == TemporalResolveEntry.Split
            ? [ShaderSources.TemporalPrepareFrag, ShaderSources.TemporalAccumulateFrag]
            : [ShaderSources.TemporalResolveFrag, ShaderSources.TemporalDepthStoreFrag];

        // BindInputs' first statement: the chosen entry point's own objects. A renderer used outside a scene, which
        // never selects, retires nothing, so the fused entry point there releases the split's targets by draining.
        void BindEntry(in TemporalResolveInputs inputs, TemporalHistory history, GpuRetireQueue? retired)
        {
            if (Entry == TemporalResolveEntry.Split)
            {
                _split ??= new TemporalSplitResolve(_gd);
                _split.Bind(inputs, history, _clampSampler, _resolveBuffer, retired);
                return;
            }
            _split?.ReleaseTargets(retired);
            EnsureFused();
        }

        // Run's branch: records the split's two passes when it is the entry point, and says whether it did.
        bool RunSplit(IGpuCommandList cl, TemporalHistory history)
        {
            if (Entry != TemporalResolveEntry.Split) return false;
            _split!.Run(cl, history);
            return true;
        }
    }
}
