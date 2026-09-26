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
        /// pixels of the UV that sample's surface point had last frame if it did not move
        /// (<see cref="TemporalResolveMath.StaticPreviousUv"/>). The motion was written for that same point, so a static
        /// surface agrees to float precision. Farther than that the surface moved, the static expected depth says nothing
        /// about it, and the test is skipped. Neighbourhood
        /// clipping (step 5) handles the pixel instead. A static point on or behind last frame's camera plane counts as
        /// moving, and the motion target already sends such a point off screen.</summary>
        public const float MovingSurfaceInternalPixels = 0.5f;
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
    }
}
