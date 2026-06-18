namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Pure easing curves for time-based transitions (e.g. <see cref="CameraBlend"/>). Each reshapes a
    /// progress value <c>t</c> (clamped to <c>[0,1]</c>) and returns the eased value in <c>[0,1]</c>, with
    /// <c>f(0)=0</c> and <c>f(1)=1</c>.
    /// </summary>
    public static class Easing
    {
        /// <summary>No easing: returns <c>t</c> unchanged (clamped).</summary>
        public static float Linear(float t) => Clamp01(t);

        /// <summary>Smooth acceleration and deceleration: <c>t*t*(3 - 2t)</c>. The <see cref="CameraBlend"/> default.</summary>
        public static float SmoothStep(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }

        /// <summary>Accelerating from zero: <c>t*t</c>.</summary>
        public static float EaseIn(float t) { t = Clamp01(t); return t * t; }

        /// <summary>Decelerating to one: <c>t*(2 - t)</c>.</summary>
        public static float EaseOut(float t) { t = Clamp01(t); return t * (2f - t); }

        /// <summary>Quadratic ease-in then ease-out: <c>t&lt;0.5 ? 2t^2 : 1 - 2(1-t)^2</c>.</summary>
        public static float EaseInOut(float t)
        {
            t = Clamp01(t);
            return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
        }

        private static float Clamp01(float t) => t < 0f ? 0f : t > 1f ? 1f : t;
    }
}
