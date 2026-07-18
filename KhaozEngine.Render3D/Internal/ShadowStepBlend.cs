using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure bookkeeping for the temporal shadow cross-fade (<see cref="ShadowSettings.ShadowStepBlendSeconds"/>): given
    /// the fit's (already quantized) key-light direction each frame, decides WHEN a quantized-direction step begins,
    /// ramps a 0..1 blend weight across the fade window, and retires the outgoing set when the window ends. No GPU, no
    /// engine state, so the headless <c>ShadowStepBlendTests</c> can pin the step-detect, weight ramp, and retirement.
    /// </summary>
    /// <remarks>
    /// The atlas re-renders on both a camera pan and a sun step, but only a SUN STEP (the quantized direction changing)
    /// has an "old direction" to cross-fade from, so the trigger is keyed on the quantized direction, not the fitted
    /// matrices. On a step the caller freezes the outgoing atlas + receiver matrices (the live set becomes the frozen
    /// set) and renders the incoming step; while <see cref="Blending"/>, the receiver lerps the two PCF results by
    /// <see cref="Weight"/> (0 = fully outgoing, 1 = fully incoming). A step arriving mid-blend simply restarts: the
    /// current live set becomes the new frozen set and the newest step renders, so only ever ONE frozen set is alive
    /// (bounded memory). The fade window is captured at the step, so tuning the duration mid-fade does not rescale the
    /// fade in flight (it applies to the next step).
    /// </remarks>
    internal struct ShadowStepBlend
    {
        Vector3 _committedDir;   // the quantized light direction the LIVE atlas currently holds
        bool _hasCommitted;      // false until the first Advance seeds _committedDir
        bool _blending;          // a cross-fade is in flight
        float _elapsed;          // seconds since the current step began
        float _window;           // the fade duration captured at the step (immune to a mid-fade duration change)

        /// <summary>A cross-fade is in flight (the frozen set must be kept alive + sampled).</summary>
        public readonly bool Blending => _blending;

        /// <summary>Cross-fade weight the receiver lerps by: <c>0</c> = fully the OUTGOING (frozen) set, <c>1</c> = fully
        /// the INCOMING (live) set. <c>1</c> whenever no cross-fade is in flight (the receiver samples only the live
        /// atlas), which is the byte-stable default-off state.</summary>
        public readonly float Weight =>
            _blending && _window > 0f ? Math.Clamp(_elapsed / _window, 0f, 1f) : 1f;

        /// <summary>
        /// Advance the blend one frame. <paramref name="quantizedDir"/> is the direction the shadow fit used this frame
        /// (already quantized when <see cref="ShadowSettings.ShadowLightQuantizeDegrees"/> &gt; 0). <paramref name="dt"/>
        /// is the elapsed seconds, <paramref name="blendSeconds"/> the configured fade window (pass <c>0</c> to disable
        /// blending, e.g. when quantization is off or the second atlas was not provisioned). Returns <c>true</c> IFF a
        /// step BEGINS this frame, so the caller freezes the outgoing atlas + receiver matrices before rendering the
        /// incoming step. On a fresh step the weight is <c>0</c> (fully outgoing) and does not advance until the next
        /// frame, so no incoming detail leaks in on the transition frame itself.
        /// </summary>
        public bool Advance(Vector3 quantizedDir, float dt, float blendSeconds)
        {
            if (!_hasCommitted)
            {
                _committedDir = quantizedDir;
                _hasCommitted = true;
            }
            else if (quantizedDir != _committedDir)
            {
                _committedDir = quantizedDir;
                if (blendSeconds > 0f)
                {
                    // Fresh step (restarts an in-flight blend: the current live set becomes the new frozen set). Weight
                    // starts at 0 (fully outgoing) and holds there this frame; the ramp begins next frame.
                    _blending = true;
                    _elapsed = 0f;
                    _window = blendSeconds;
                    return true;
                }
                _blending = false;   // blending disabled: a plain re-render step, no cross-fade (the dirty path handles it)
            }

            if (_blending)
            {
                _elapsed += MathF.Max(0f, dt);
                if (_elapsed >= _window) { _blending = false; _elapsed = _window; }   // window elapsed: retire the frozen set
            }
            return false;
        }

        /// <summary>Cancel any in-flight cross-fade and forget the committed direction (the shadow tier turned off, or a
        /// scene reset). The next <see cref="Advance"/> re-seeds the committed direction without a step.</summary>
        public void Reset() => this = default;
    }
}
