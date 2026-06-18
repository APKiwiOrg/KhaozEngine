using System;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Drives a one-shot, time-based transition of a <see cref="Camera2D"/> from its current state to a
    /// target <see cref="CameraState"/> over a duration, reshaped by an easing curve. Distinct from the
    /// continuous exponential smoothing of <c>CameraFollow</c>/<c>GroupCamera</c>: a blend has a definite
    /// start, end, and duration. Headless, no GPU.
    /// </summary>
    public sealed class CameraBlend
    {
        private readonly Camera2D _camera;
        private CameraState _start;
        private CameraState _target;
        private Func<float, float> _easing = Easing.SmoothStep;
        private float _duration;
        private float _elapsed;

        /// <summary>Creates a blend driver for the given camera.</summary>
        public CameraBlend(Camera2D camera) => _camera = camera;

        /// <summary>The camera this blend drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>True from a positive-duration <see cref="To"/> until progress reaches 1.</summary>
        public bool IsBlending { get; private set; }

        /// <summary>Raw progress 0..1 (pre-easing): 0 before the first blend, 1 when complete or after an
        /// instant snap. <see cref="Stop"/> leaves it at its last value (the fraction reached when halted).</summary>
        public float Progress { get; private set; }

        /// <summary>
        /// Captures the current camera as the start state and blends to <paramref name="target"/> over
        /// <paramref name="duration"/> seconds with <paramref name="easing"/> (default
        /// <see cref="Easing.SmoothStep"/>). <paramref name="duration"/> &lt;= 0 snaps to the target
        /// immediately (no blend). Calling this mid-blend re-captures the current camera as the new start.
        /// </summary>
        public void To(CameraState target, float duration, Func<float, float>? easing = null)
        {
            _start = CameraState.From(_camera);
            _target = target;
            _easing = easing ?? Easing.SmoothStep;
            _duration = duration;
            _elapsed = 0f;

            if (duration <= 0f)
            {
                target.ApplyTo(_camera);
                Progress = 1f;
                IsBlending = false;
                return;
            }

            Progress = 0f;
            IsBlending = true;
        }

        /// <summary>Advances the active blend by <paramref name="dt"/> seconds, applying the eased
        /// interpolation to the camera. No-op when idle.</summary>
        public void Update(float dt)
        {
            if (!IsBlending) return;

            _elapsed += dt;
            float t = _elapsed / _duration;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            Progress = t;

            CameraState.Lerp(_start, _target, _easing(t)).ApplyTo(_camera);

            if (t >= 1f) IsBlending = false;
        }

        /// <summary>Cancels an active blend in place: the camera stays where it is and
        /// <see cref="IsBlending"/> becomes false.</summary>
        public void Stop() => IsBlending = false;
    }
}
