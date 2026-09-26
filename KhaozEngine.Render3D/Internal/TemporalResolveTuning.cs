namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE RESOLVE'S TUNING CONSTANTS, one for one with the <c>const float</c> block in
    /// <c>ShaderSources.TemporalResolveTuningGlsl</c> and <c>ShaderSources.TemporalCommonGlsl</c>.
    /// <c>TemporalResolveShaderTests</c> pins the two copies together, and the resolve's acceptance tests tune them. Each
    /// comment names the design step (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3) the constant belongs to.
    /// </summary>
    internal static class TemporalResolveTuning
    {
        /// <summary>Step 1. The motion target clears both channels to <see cref="MotionMath.Sentinel"/> where no opaque
        /// geometry drew. An x channel whose magnitude passes this reads as background, as
        /// <see cref="MotionMath.IsBackground"/> reads it. Written motion is clamped to <see cref="MotionMath.MaxMotionUv"/>
        /// per axis, so an off-screen value, <see cref="MotionMath.OffScreenMotion"/> included, never reads as background
        /// and is rejected as off screen.</summary>
        public const float MotionSentinel = MotionMath.BackgroundThreshold;
        /// <summary>Steps 1 and 3. The linear depth the depth store writes for background, and the expected depth of a
        /// background pixel.</summary>
        public const float BackgroundLinearDepth = 1.0e30f;
        /// <summary>Step 3. A previous surface nearer than the expected depth by more than this share of it means the
        /// pixel was hidden last frame, and its history is rejected.</summary>
        public const float DisocclusionTolerance = 0.02f;
        /// <summary>Step 3. <see cref="TemporalResolveUniforms.CurrentToPrevious"/> assumes a static point, so the depth
        /// test runs only where the dilated texel's motion carries its own unjittered sample within this many internal
        /// pixels, plus <see cref="MovingSurfaceMotionFraction"/> of that motion, of the UV that sample's surface point had
        /// last frame if it did not move (<see cref="TemporalResolveMath.StaticPreviousUv"/>). The motion was written for
        /// that same point, so a static surface agrees up to float precision and the target's rounding. Farther than that
        /// the surface moved, the static expected depth says nothing about it, and the test is skipped. Neighbourhood
        /// clipping (step 5) handles the pixel instead. A static point on or behind last frame's camera plane counts as
        /// moving, and the motion target already sends such a point off screen.</summary>
        public const float MovingSurfaceInternalPixels = 0.5f;
        /// <summary>Step 3. The share of the dilated texel's motion length, in internal pixels, added to
        /// <see cref="MovingSurfaceInternalPixels"/>. The motion target is RG16F, whose 10-bit mantissa rounds each
        /// channel by up to half a unit in the last place, at most 1/2048 of its magnitude. For a motion between 0.5 and
        /// 1 UV that is up to 2.4e-4 UV, 0.94 internal pixels on a 3840 wide target, which alone would read a static
        /// surface as moving. 1/1024 is twice that bound.</summary>
        public const float MovingSurfaceMotionFraction = 1f / 1024f;
        /// <summary>Step 8. The accumulated sample weight's cap. The current weight is at least w / (15 + w), about one
        /// in sixteen for a well-placed sample.</summary>
        public const float MaxAccumulation = 15f;
        /// <summary>Step 8. The cap at <see cref="MotionAccumulationPixels"/> of display motion and above, so repeated
        /// history resampling under fast motion cannot blur.</summary>
        public const float MovingAccumulation = 8f;
        /// <summary>Step 8. Display pixels of motion per frame at which the cap reaches <see cref="MovingAccumulation"/>.</summary>
        public const float MotionAccumulationPixels = 16f;
        /// <summary>Step 5. The variance box's half-width in standard deviations for a still pixel. Wide, for stability.</summary>
        public const float GammaStill = 1.5f;
        /// <summary>Step 5. The half-width for a pixel moving <see cref="GammaMotionPixels"/> or more.</summary>
        public const float GammaMoving = 0.75f;
        /// <summary>Step 5. Display pixels of motion per frame over which gamma narrows from still to moving.</summary>
        public const float GammaMotionPixels = 3f;
        /// <summary>Step 7. Weighted luma difference between the final and the opaque-only colour times this is the
        /// reactive estimate, clamped to 1.</summary>
        public const float ReactiveGain = 4f;
        /// <summary>Step 7. The share of accumulated confidence a fully reactive pixel drops.</summary>
        public const float ReactiveStrength = 0.9f;
        /// <summary>Step 6. The smallest weighted luma step, against both neighbours on one axis, that makes a ridge.</summary>
        public const float LockRidgeAbsolute = 0.02f;
        /// <summary>Step 6. The ridge step relative to the centre luma, whichever of the two is larger.</summary>
        public const float LockRidgeRelative = 0.1f;
        /// <summary>Step 6. Display pixels of motion per frame where locks start to release.</summary>
        public const float LockMotionStartPixels = 1f;
        /// <summary>Step 6. Display pixels of motion per frame where locks are fully released.</summary>
        public const float LockMotionEndPixels = 4f;
        /// <summary>Step 6. Lock lost per unit of reactive estimate.</summary>
        public const float LockReactiveRelease = 2f;
        /// <summary>Step 6. Lock lost per frame, the same at every preset. A thin feature shows when a jittered sample
        /// lands on it, and the Halton x offsets visit every eighth of a texel about once in eight frames at any display
        /// ratio, so how often it shows follows its internal texel coverage, not the jitter cycle. One over the cycle
        /// held a lock for 72 frames at UltraPerformance, and an unkeyed line crossing at 0.6 display pixels a frame
        /// trailed 2 display pixels at 28 percent of its contrast. One eighth, with <see cref="LockHoldGain"/>, trails
        /// at most 1 at 11 percent.</summary>
        public const float LockDecay = 1f / TemporalJitter.NativePhaseCount;
        /// <summary>Step 6. The hold on the luma clip is the lock times this, at most 1. With <see cref="LockDecay"/> of
        /// an eighth it stays whole through four frames without a ridge, then lets go over four more. A still line three
        /// eighths of a texel wide goes up to three frames unseen. A hold equal to the lock clipped a share of it on each
        /// of them, and at Native the line averaged 10 percent of its contrast against a coverage of 37.5. At 2 it
        /// averages 42 percent and changes by under 4 percent a frame at Native, Quality and UltraPerformance. 3 and 4
        /// raised the unkeyed trail's peak from 11 to 22 and 31 percent.</summary>
        public const float LockHoldGain = 2f;
        /// <summary>Step 6. Lock lost per internal pixel the dilated surface moved on its own this frame, the motion the
        /// camera does not explain, so the lock holds only while the reprojected history stays consistent (design
        /// section 3 step 6). A ridge refreshed on a surface crossing texels would leave the pixel it just crossed
        /// locked on the history that surface carried there, held after the surface has gone. At 1, a surface moving a
        /// texel a frame keeps no lock. A keyed line at 0.9 display pixels a frame on Quality trailed 1 display pixel at
        /// 14 percent of its contrast without it, and none with it. 2 cost the same line at 0.6 display pixels a frame 2
        /// to 4 points of the contrast it keeps.</summary>
        public const float LockTravelRelease = 1f;
    }
}
