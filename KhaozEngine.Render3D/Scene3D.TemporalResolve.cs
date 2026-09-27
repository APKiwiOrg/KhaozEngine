using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The temporal resolve's frame wiring (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md, sections 1 and
    /// 2). <see cref="AntiAliasing.Temporal"/> turns temporal rendering on, sizes the internal target as a fraction of the
    /// display, and sets the jitter cycle from the display over internal scale. A partial of its own because
    /// <c>Scene3D.cs</c> is frozen by the file-size ratchet.
    /// </summary>
    public sealed partial class Scene3D
    {
        // The display size the frame's first render latched, for the jitter cycle and the history key.
        int _latchedDisplayWidth, _latchedDisplayHeight;

        /// <summary>Whether temporal anti-aliasing is in effect: the effective anti-aliasing mode is
        /// <see cref="AntiAliasingMode.Temporal"/>, which <see cref="PixelPostProcessSettings.Pixelated"/> refuses. It
        /// requests temporal rendering and sizes the internal target by
        /// <see cref="PixelPostProcessSettings.EffectiveUpscaleRatio"/>.</summary>
        internal bool TemporalResolveActive => Post.EffectiveAaMode == AntiAliasingMode.Temporal;

        /// <summary>The display over internal scale per axis for the latched display size, which sets the jitter
        /// sequence length. <see cref="LatchFrameView"/> reads it on the frame's first render and fixes the sequence
        /// length it gives for the frame, which <see cref="LastTemporalDiagnostics"/> reports. 1 unless temporal
        /// anti-aliasing is in effect.</summary>
        float DisplayOverInternalRatio => TemporalDisplayOverInternal(_latchedDisplayWidth, _latchedDisplayHeight);

        /// <summary>The display over internal scale per axis for a display of <paramref name="displayWidth"/> by
        /// <paramref name="displayHeight"/> while temporal anti-aliasing is in effect, else exactly 1. The scale the
        /// jitter cycle needs, unrounded with the render cap included, as <see cref="TemporalJitter.PhaseCount"/>
        /// defines it.</summary>
        internal float TemporalDisplayOverInternal(int displayWidth, int displayHeight)
        {
            if (!TemporalResolveActive || displayWidth <= 0 || displayHeight <= 0) return 1f;
            return 1f / (Post.EffectiveUpscaleRatio * ScaledViewport(Post, displayWidth, displayHeight).CapScale);
        }
    }
}
