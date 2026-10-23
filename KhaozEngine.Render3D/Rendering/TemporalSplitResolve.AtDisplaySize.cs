using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>The second pass compiled for the display's own size
    /// (<see cref="ShaderSources.TemporalAccumulateAtDisplaySizeFrag"/>), without the display-sized reconstruction no
    /// pixel takes there. Built on the first frame that records it, with the upscaling pass's layout and outputs, and
    /// kept beside it, so a change of preset switches between two built pipelines.</summary>
    internal sealed partial class TemporalSplitResolve
    {
        TemporalVisibilityPipelinePair? _atDisplaySizePrograms;

        /// <summary>Whether a frame upscales: its display over internal ratio (<c>Jitter.z</c>) is above 1, the test
        /// <c>temporalDisplayShare</c> makes before a pixel may take the display-sized reconstruction. Under the
        /// render cap a Native preset can upscale, 3456x2234 from 3342x2160.</summary>
        internal static bool Upscales(in TemporalResolveUniforms uniforms) => uniforms.Jitter.Z > 1f;

        /// <summary>For the cost measurement alone: record the upscaling second pass on a frame at the display's own
        /// size too, where it writes the same history, so one run times both programs.</summary>
        internal bool UpscalingProgramForTests { get; set; }

        /// <summary>Whether the second pass compiled for the display's own size has been built. For tests.</summary>
        internal bool AtDisplaySizeBuiltForTests => _atDisplaySizePrograms is not null;

        TemporalVisibilityPipelinePair AccumulatePrograms(bool upscales)
        {
            if (upscales || UpscalingProgramForTests) return _accumulatePrograms;
            _atDisplaySizePrograms ??= new TemporalVisibilityPipelinePair(_gd,
                ShaderSources.TemporalAccumulateFragment(TemporalResolvePrecisionPolicy.For(_gd), upscales: false),
                [_accumulateLayout], [_fullyLitAccumulateLayout],
                new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence),
                _fragmentSourceForTests);
            return _atDisplaySizePrograms;
        }

        internal bool UsesVisibility(bool upscales, bool withoutVisibility)
            => AccumulatePrograms(upscales).UsesVisibility(withoutVisibility);

        void DisposeAtDisplaySize()
        {
            _atDisplaySizePrograms?.Dispose();
            _atDisplaySizePrograms = null;
        }
    }
}
