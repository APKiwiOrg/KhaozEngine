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
        IGpuShaderSet? _atDisplaySizeShaders;
        IGpuPipeline? _atDisplaySizePipeline;

        /// <summary>Whether a frame upscales: its display over internal ratio (<c>Jitter.z</c>) is above 1, the test
        /// <c>temporalDisplayShare</c> makes before a pixel may take the display-sized reconstruction. Under the
        /// render cap a Native preset can upscale, 3456x2234 from 3342x2160.</summary>
        internal static bool Upscales(in TemporalResolveUniforms uniforms) => uniforms.Jitter.Z > 1f;

        /// <summary>For the cost measurement alone: record the upscaling second pass on a frame at the display's own
        /// size too, where it writes the same history, so one run times both programs.</summary>
        internal bool UpscalingProgramForTests { get; set; }

        /// <summary>Whether the second pass compiled for the display's own size has been built. For tests.</summary>
        internal bool AtDisplaySizeBuiltForTests => _atDisplaySizePipeline is not null;

        IGpuPipeline AccumulatePipeline(bool upscales)
        {
            if (upscales || UpscalingProgramForTests) return _accumulatePipeline;
            if (_atDisplaySizePipeline is null)
            {
                IGpuResourceFactory f = _gd.Factory;
                _atDisplaySizeShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
                    ShaderSources.TemporalAccumulateFragment(TemporalResolvePrecisionPolicy.For(_gd), upscales: false));
                _atDisplaySizePipeline = TemporalResolveRenderer.Fullscreen(f, _atDisplaySizeShaders,
                    _accumulateLayout,
                    new GpuOutputDescription(null, TemporalFormats.HistoryColor, TemporalFormats.HistoryConfidence));
            }
            return _atDisplaySizePipeline;
        }

        void DisposeAtDisplaySize()
        {
            _atDisplaySizePipeline?.Dispose();
            _atDisplaySizeShaders?.Dispose();
            _atDisplaySizePipeline = null;
            _atDisplaySizeShaders = null;
        }
    }
}
