using System;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Settings for temporal rendering, reachable as <see cref="PixelPostProcessSettings.Temporal"/> beside
    /// <see cref="PixelPostProcessSettings.Bloom"/> and <see cref="PixelPostProcessSettings.Water"/>. It carries the
    /// camera-cut thresholds, the upscale preset or ratio, the sharpness, the mip bias offset and the sun-shadow
    /// light hold. The preset and ratio
    /// size the internal target whenever <see cref="RenderScale.Temporal"/> is in effect, which
    /// <see cref="AntiAliasing.Temporal"/> forces and which a game can also pick directly with no temporal rendering at
    /// all. The thresholds cost nothing until something asks for temporal rendering. They decide when a camera move is
    /// a cut: a frame whose camera moved further than <see cref="CutDistanceMetres"/> or turned more than
    /// <see cref="CutAngleDegrees"/> since the last frame drops its temporal history, exactly as an explicit
    /// <see cref="Scene3D.CameraCut"/> does. A render origin jump the previous frame cannot be rebased across triggers
    /// the same automatic cut, whatever the thresholds, as <see cref="TemporalResetReason.CameraCutDetected"/>
    /// describes. The two cuts differ only in the reason they report:
    /// <see cref="TemporalResetReason.CameraCutDetected"/> for the automatic one and
    /// <see cref="TemporalResetReason.CameraCutRequested"/> for the explicit call.
    /// </summary>
    public sealed class TemporalSettings
    {
        /// <summary>The colour history grid, <see cref="TemporalHistoryDetail.Display"/> by default. Fine keeps an
        /// additional colour pair at twice the display on each axis. Nothing reads it yet, so images are unchanged.
        /// Takes effect on the frame's first render, only while the temporal resolve runs and both the device limit
        /// and <see cref="HistoryDetailPixelLimit"/> admit it. Unknown values behave as Display. Read
        /// <see cref="Scene3D.TemporalHistoryDetailInEffect"/> for the latched selection. A change of that selection
        /// resets history for one frame with <see cref="TemporalResetReason.HistoryDetail"/>.</summary>
        public TemporalHistoryDetail HistoryDetail = TemporalHistoryDetail.Display;

        /// <summary>Inclusive display pixel budget for Fine, default 3,686,400 (2560 by 1440). Its extra RGBA16F
        /// colour pair uses 225 MiB at that size. A 3840 by 2160 display would need 506.25 MiB and falls back to
        /// Display by default. Zero and negative values disable Fine. The device's 2D render texture dimension
        /// limit also applies. A change that leaves the mode in effect unchanged does not reset history.</summary>
        public long HistoryDetailPixelLimit = 2560L * 1440;

        /// <summary>The largest distance, in metres, the camera eye may move between two frames and still continue the
        /// previous one. Default 16. <see cref="float.PositiveInfinity"/> turns the distance check off. A render origin
        /// jump the previous frame cannot be rebased across is still a cut, see
        /// <see cref="TemporalResetReason.CameraCutDetected"/>. NaN and negative values are caller errors. They are not
        /// validated and their behaviour is unsupported.</summary>
        public float CutDistanceMetres = 16f;

        /// <summary>The largest angle, in degrees, the camera's forward direction may turn between two frames and still
        /// continue the previous one. Default 60. A value of 180 or more turns the angle check off. NaN and negative
        /// values are caller errors. They are not validated and their behaviour is unsupported.</summary>
        public float CutAngleDegrees = 60f;

        /// <summary>The shadow-map texel budget that replaces <see cref="ShadowSettings.ShadowLightHoldTexels"/>
        /// while temporal anti-aliasing resolves the frame. Default 0, so the shadow fit follows the sun every frame
        /// instead of moving in held steps. Positive values hold exactly as the shadow setting does, using the same
        /// <see cref="ShadowSettings.ShadowLightHoldCasterHeight"/>. Negative and NaN values behave as zero.
        /// The first render fixes which budget the frame uses. An AA mode change between renders takes effect on
        /// the next frame, so every render of the current frame keeps the same budget selection.
        /// <para>Following re-records the atlas whenever the sun moves, including fully static scenes that could
        /// otherwise reuse it. Set 1 to restore the one-texel hold under temporal anti-aliasing when that saving is
        /// needed. Fractional holds beat against the jitter cycle and are not recommended. Other anti-aliasing modes,
        /// the motion-vector debug view alone and forced temporal rendering without the resolve keep the shadow
        /// setting's budget.</para></summary>
        public float ShadowLightHoldTexels = 0f;

        /// <summary>The smallest internal-over-display ratio <see cref="UpscaleRatio"/> accepts, 0.33 per axis, just
        /// under <see cref="TemporalUpscale.UltraPerformance"/>'s third.</summary>
        internal const float MinUpscaleRatio = 0.33f;

        /// <summary>The internal resolution preset while <see cref="AntiAliasing.Temporal"/> is active. Default
        /// <see cref="TemporalUpscale.Native"/>. A change resets the temporal history for one frame, reported as
        /// <see cref="TemporalResetReason.RenderScale"/>, unless <see cref="UpscaleRatio"/> overrides the preset.</summary>
        public TemporalUpscale Upscale = TemporalUpscale.Native;

        /// <summary>An explicit internal-over-display ratio per axis that overrides <see cref="Upscale"/> when set, clamped
        /// to 0.33 to 1. A value that is not finite is ignored and the preset applies. A change of the ratio in effect
        /// resets the temporal history for one frame, reported as <see cref="TemporalResetReason.RenderScale"/>, even
        /// when the render cap keeps the internal size.</summary>
        public float? UpscaleRatio;

        /// <summary>The display-over-internal factor per axis a preset stands for: 1, 1.5, 1.7, 2 or 3.</summary>
        internal static float DisplayOverInternal(TemporalUpscale preset) => preset switch
        {
            TemporalUpscale.Quality => 1.5f,
            TemporalUpscale.Balanced => 1.7f,
            TemporalUpscale.Performance => 2f,
            TemporalUpscale.UltraPerformance => 3f,
            _ => 1f,
        };

        /// <summary>The internal-over-display ratio per axis in effect: <see cref="UpscaleRatio"/> clamped when it is set
        /// and finite, else the reciprocal of the preset's factor.</summary>
        internal float ResolvedUpscaleRatio => UpscaleRatio is float ratio && float.IsFinite(ratio)
            ? Math.Clamp(ratio, MinUpscaleRatio, 1f)
            : 1f / DisplayOverInternal(Upscale);

        /// <summary>Strength of the contrast adaptive sharpen that runs after the tonemap while temporal
        /// anti-aliasing is on, from 0 (the pass does not run) to 1. The default 0.25 is light, in keeping with
        /// stability over crispness. The sharpen limits each pixel by its four neighbours, so it leaves an edge
        /// already at full contrast alone and sharpens an isolated pixel at most half as hard.
        /// <para>It sharpens the display image the temporal resolve produced, on the frame's first render only. A
        /// later render inside the frame, such as an offscreen capture, is never sharpened. The legacy order
        /// (<see cref="HdrSettings.Enabled"/> off) has no tonemap, and there the sharpen is the chain's first pass
        /// after the distortion apply. A value above 1 counts as 1, and a negative or NaN value as 0, so NaN never
        /// reaches the pass.</para></summary>
        public float Sharpness = .25f;

        /// <summary>The sharpness the pass is given: <see cref="Sharpness"/> clamped to 0 to 1, with NaN as 0, by the
        /// mapping the pass and its CPU mirror share (<see cref="TemporalSharpenMath.ResolvedSharpness"/>).</summary>
        internal float ResolvedSharpness => TemporalSharpenMath.ResolvedSharpness(Sharpness);

        /// <summary>Added to the automatic texture mip bias of log2(internal / display) while temporal anti-aliasing
        /// runs. The default minus 0.5 keeps a little more texture detail than the internal resolution alone would,
        /// which the jittered history resolves without shimmer. Clamped to minus 2 to plus 1, and a NaN or infinite
        /// value counts as 0. It applies to models, foliage, splat terrain and tile ground on the frame's first render
        /// only, the one the temporal resolve accumulates. A later render inside the frame, such as an offscreen
        /// capture, takes no bias, and neither does any render while temporal anti-aliasing is off.</summary>
        public float MipBiasOffset = -.5f;
    }
}
