namespace KhaozEngine.Primitives
{
    /// <summary>
    /// Pure easing curves for time-based transitions. Each reshapes a progress value <c>t</c> (clamped to
    /// <c>[0,1]</c>) and returns the eased value in <c>[0,1]</c>, with <c>f(0)=0</c> and <c>f(1)=1</c>.
    /// </summary>
    public static class Easing
    {
        public static float Linear(float t) => MathUtil.Clamp01(t);
        public static float SmoothStep(float t) { t = MathUtil.Clamp01(t); return t * t * (3f - 2f * t); }
        public static float EaseIn(float t) { t = MathUtil.Clamp01(t); return t * t; }
        public static float EaseOut(float t) { t = MathUtil.Clamp01(t); return t * (2f - t); }
        public static float EaseInOut(float t)
        {
            t = MathUtil.Clamp01(t);
            return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
        }
    }
}
