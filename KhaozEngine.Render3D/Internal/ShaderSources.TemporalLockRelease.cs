namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE CARRIED-LOCK RELEASE, ORDERED WITHOUT <c>precise</c>. Step 6 releases a carried lock by motion, reactive
    /// estimate and edge motion. Visibility adds a second use of the released lock, and that exposed different
    /// contractions of this arithmetic in the fused and split programs. <c>precise</c> fixed the order on Metal, but
    /// FXC spreads it over nearly the whole Direct3D 11 program, and WARP and lavapipe lower a precise division by
    /// three as a correctly rounded division where Metal multiplies by its rounded reciprocal.
    /// <para><b>EVERY STEP IS ONE IEEE OPERATION.</b> Each input and each rounded intermediate passes through
    /// <c>temporalOrdered</c>, which ORs into its bits the sign bit of <c>Sizes.x</c> shifted down to bit 0. A compiler
    /// cannot see that value, so it can neither fuse a product into the next sum nor reorder the products, and the
    /// division is written as a multiply by <see cref="TemporalResolveTuning.LockMotionReleaseScale"/>, the float32
    /// <c>0x3EAAAAAB</c> as its exact decimal. Every backend therefore computes
    /// <see cref="TemporalResolveMath.ReleaseLock"/> bit for bit.</para>
    /// <para><b>THE BARRIER IS ZERO</b> because <c>Sizes.x</c> is the internal width, at least 1
    /// (<see cref="TemporalResolveMath.BuildUniforms"/>), so its sign bit is clear. Every program that includes it
    /// declares <c>Sizes</c> in its <c>Resolve</c> block.</para>
    /// </summary>
    internal static partial class ShaderSources
    {
        internal const string TemporalLockReleaseGlsl = @"
const float LockMotionReleaseScale = 0.3333333432674407958984375;
float temporalOrdered(float v) { return uintBitsToFloat(floatBitsToUint(v) | (floatBitsToUint(Sizes.x) >> 31u)); }
float temporalReleaseLock(float value, float motionPixels, float reactive, float edgeMotion) {
    float moved = temporalOrdered(temporalOrdered(motionPixels) - LockMotionStartPixels);
    float motionKeep = temporalOrdered(1.0 - clamp(temporalOrdered(moved * LockMotionReleaseScale), 0.0, 1.0));
    float reactiveKeep = temporalOrdered(1.0 - clamp(temporalOrdered(reactive) * LockReactiveRelease, 0.0, 1.0));
    float edgeKeep = temporalOrdered(1.0 - clamp(temporalOrdered(edgeMotion) * LockEdgeRelease, 0.0, 1.0));
    float keep = temporalOrdered(temporalOrdered(motionKeep * reactiveKeep) * edgeKeep);
    return temporalOrdered(temporalOrdered(value) * keep);
}
";
    }
}
