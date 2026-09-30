namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE RESOLVE'S TUNING CONSTANTS, one for one with the <c>const float</c> block in
    /// <c>ShaderSources.TemporalResolveTuningGlsl</c> and <c>ShaderSources.TemporalCommonGlsl</c>.
    /// <c>TemporalResolveShaderTests</c> pins the two copies together, and the resolve's acceptance tests tune them. Each
    /// comment names the design step (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3) the constant belongs to.
    /// <para><b>ONE SET OF RULES, TWO ENTRY POINTS.</b> The constants are read by shared shader functions, the
    /// per-texel preparation (<see cref="ShaderSources.TemporalPrepareGlsl"/>) and the per-pixel accumulation
    /// (<see cref="ShaderSources.TemporalAccumulateGlsl"/>), which both entry points call: the fused pass, which
    /// prepares each pixel's 3x3 inline, and the split, whose first pass prepares each internal texel once into
    /// <see cref="TemporalSplitFormats"/>'s targets and whose second pass accumulates each display pixel from them. So
    /// a constant tunes both. Both round the weighted colour and the reactive difference to half float in integer
    /// steps, which the split stores exactly, so the two apply every rule to the same values, and
    /// <c>TemporalEntryIdentityGpuTests</c> shows they write the same history bit for bit on hardware and on WARP. On
    /// a software Vulkan device (llvmpipe) the state is identical and the colour within a measured bound.
    /// The split's targets hold 24 bytes an internal texel, 82.3 MB at 3456x2234 Quality, and exist only while it runs.
    /// <see cref="TemporalResolvePolicy"/> picks the split on every backend at every size, from the hosted NVIDIA
    /// measurements on Direct3D 11 and Vulkan and the Metal figures, and the design records them
    /// (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 3, The resolve), with the Vulkan frame boundary that frees replaced
    /// targets (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/1199">#1199</see>).</para>
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
        /// pixel was hidden last frame, and its history is rejected. Step 6 takes the same share the other way: at a
        /// moving edge (<see cref="LockEdgeMotionFraction"/>) where all four stored depths lie farther than the
        /// expected depth by more than it, the history there is a farther surface's, and the lock read with it is
        /// dropped. It is also the least depth step that separates two surfaces elsewhere: a centre texel farther than
        /// the dilated nearest by it may reproject by its own motion (<see cref="DilationReachInternalPixels"/>), and a
        /// stored depth whose run of texels along a row or a column, apart from the depths either side by it, is at
        /// most two long is a narrow feature (<see cref="DisocclusionVisibleShare"/>).</summary>
        public const float DisocclusionTolerance = 0.02f;
        /// <summary>Step 3. <see cref="TemporalResolveUniforms.CurrentToPrevious"/> assumes a static point, so the
        /// depth test runs only where the motion of the texel that reprojects the pixel, the dilated nearest or, beside
        /// a fast edge, the centre texel (<see cref="DilationReachInternalPixels"/>), carries its own unjittered sample
        /// within this many internal pixels, plus <see cref="MovingSurfaceMotionFraction"/> of that motion, of the UV
        /// that sample's surface point had last frame if it did not move
        /// (<see cref="TemporalResolveMath.StaticPreviousUv"/>). The motion was written for that same point, so a
        /// static surface agrees up to float precision and the target's rounding. Farther than that the surface moved,
        /// the static expected depth says nothing about it, and the test is skipped. Neighbourhood clipping (step 5)
        /// handles the pixel instead. A static point on or behind last frame's camera plane counts as moving, and the
        /// motion target already sends such a point off screen.</summary>
        public const float MovingSurfaceInternalPixels = 0.5f;
        /// <summary>Step 3. The share of the reprojected texel's motion length, in internal pixels, added to
        /// <see cref="MovingSurfaceInternalPixels"/>. The motion target is RG16F, whose 10-bit mantissa puts one unit
        /// in the last place at no more than 1/1024 of a channel's magnitude. Vulkan leaves the conversion's rounding
        /// undefined. Rounding to nearest moves a channel by up to half a unit, at most 1/2048 of its magnitude, which
        /// for a motion between 0.5 and 1 UV is up to 2.4e-4 UV, 0.94 internal pixels on a 3840 wide target, and alone
        /// would read a static surface as moving. Rounding toward zero moves it by less than a whole unit. 1/1024
        /// covers the whole unit, so it holds under either rounding, with twice the margin under rounding to
        /// nearest.</summary>
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
        /// ratio, so how often it shows follows its internal texel coverage, not the jitter cycle. A decay of one over the
        /// cycle, 1/72 at UltraPerformance, trails an unkeyed line crossing at 0.3 to 0.9 display pixels a frame by 5 to
        /// 8 display pixels at up to 50 percent of its contrast. One eighth trails at most 1 at 11 percent.</summary>
        public const float LockDecay = 1f / TemporalJitter.NativePhaseCount;
        /// <summary>Step 6. The hold on the luma clip is the lock times this, at most 1. With <see cref="LockDecay"/> of
        /// an eighth it stays whole through four frames without a ridge, then lets go over four more. A still line three
        /// eighths of a texel wide goes up to three frames unseen, and a hold equal to the lock clipped a share of it on
        /// each: it averaged 10.4, 5.4 and 8.3 percent of its contrast at Native, Quality and UltraPerformance. At 2 the
        /// pixel at its centre averages 41.7 percent at Native against a coverage of 37.5, and 36.1 and 81.7 upscaled,
        /// where that pixel is narrower than the texel, and changes by under 4 percent a frame at all three. 3 and 4
        /// raise the unkeyed trail's peak from 11 to 22 and 31 percent. A line an eighth of a texel wide, seen once in
        /// eight frames, outlasts the hold: it averages about 4 percent against its coverage of 12.5 and pulses by up to
        /// 9 percent a frame.</summary>
        public const float LockHoldGain = 2f;
        /// <summary>Step 6. Lock lost per internal pixel of motion between the centre texel and the dilated nearest
        /// surface this frame. Where they differ, a moving feature is passing over a background, and the pixel holds the
        /// history that feature carried there, not a sub-texel feature of its own. A surface moving as a whole, a swaying
        /// blade or an avatar the camera follows, differs nowhere and keeps its lock, where a release on the surface's
        /// own travel left a line on it no brighter than with the lock off. A keyed line at 0.9 display pixels a frame
        /// on Quality trails 1 display pixel at 18 percent of its contrast and 2 at 5 percent at 0 and 0.5, and 1 at
        /// both from 1 up, at 17 percent at 1 and 15 at 2 and 4. 2 and 4 cost a blade swaying at 0.1 internal pixels a
        /// frame over still ground on Quality, whose summed coverage falls from 22.1 percent to 20.8 and 13.1, where 0
        /// to 1 all keep 22.1. So 1 is the smallest factor that leaves one trailing pixel at 5 percent, and the largest
        /// measured that costs that blade nothing. A pixel that reprojected by its own motion
        /// (<see cref="DilationReachInternalPixels"/>) and kept its history holds its own, since where a moving surface
        /// showed step 3 keeps a history only whole (<see cref="DisocclusionVisibleShare"/>), and keeps its lock. A
        /// still line three eighths of a texel wide, with a keyed 30 display pixel box sliding past one internal texel
        /// away at 2 display pixels a frame, fell on its worst frame to 0.01 of its energy without the box at Native
        /// and to 0.00 to 0.25 at Quality, and was still at 0.43 to 0.69 sixteen frames after the box passed. It keeps
        /// 0.99 now. Keeping the lock where such a pixel restarted too left a ridged keyed box crossing the textured
        /// wall 6 and 8 of its 420 trail pixels at Native and Quality, against 3 and 4.</summary>
        public const float LockEdgeRelease = 1f;
        /// <summary>Step 6. A centre texel whose motion differs from the dilated motion by more than this share of the
        /// dilated motion's length sits at a moving edge. Both come from the RG16F motion target, which rounds each
        /// channel by up to 1/2048 of its magnitude, so two equal motions can read up to about 1.41/1024 of their length
        /// apart, and 1/512 is above that. At a moving edge whose history a farther surface left
        /// (<see cref="DisocclusionTolerance"/>), the lock read with it is dropped. Without that, a nearer surface
        /// crossing a held sub-texel line at 0.1 internal pixels a frame on UltraPerformance carries the line onto the 2
        /// display pixels ahead of it at up to 26 percent of its contrast, and with it at most 3 percent shows. Where
        /// nothing moves the stored depths never drop a lock, because an edge must also pass
        /// <see cref="LockEdgeFloorInternalPixels"/>: releasing wherever the history lay farther than the dilated surface
        /// took a still line beside a still nearer surface from 40.4 to 5.9 percent at Native. With the floor in place, a
        /// share of 0 measures the same as 1/512 on every thin-feature fact.</summary>
        public const float LockEdgeMotionFraction = 1f / 512f;
        /// <summary>Step 6. The least difference, in internal pixels, between the centre texel's motion and the dilated
        /// motion that counts as a moving edge, whatever <see cref="LockEdgeMotionFraction"/> gives. A background centre
        /// takes its motion through a round trip to NDC and back, <c>(2u - 1) * 0.5 + 0.5</c>, which float rounds by up to
        /// 2^-26 of the screen per axis below a quarter of it, even with the camera still. That is under 1.1e-4 internal
        /// pixels even at an internal width of 5120, about a tenth of this floor. Where a still surface stands in front
        /// of the sky, that rounding alone read as an edge beside a still sub-texel line in the top left quarter of the
        /// screen, and the line averaged 7.3 percent at Native and 6.3 at UltraPerformance against 40.4 and 81.5 at the
        /// centre. With this floor it measures the same as at the centre, and every other thin-feature fact is unchanged.
        /// A nearer surface crossing a held line at 0.1 internal pixels a frame is a hundred times this.</summary>
        public const float LockEdgeFloorInternalPixels = 1e-3f;
        /// <summary>Step 5. The least move the clip must make to the history, as blended, in the luma-weighted YCoCg
        /// space the clip runs in, for the pixel's clip flag. Passes that re-evaluate the resolve read the flag, such
        /// as the sampled temporal counts, and the resolve's colour never does. On flat content the variance box
        /// shrinks to its floor of 1e-5, so the half-float history's own rounding lies outside it and the clip moves
        /// nearly every pixel: a still flat frame flagged 94 percent of its pixels, and every one of those moves was
        /// under 1/4096. 1/1024 is a quarter of an 8-bit display step, far below anything visible, while a sharp
        /// change of colour between frames still flags nearly every pixel it reaches.</summary>
        public const float ClipFlagMinimumMove = 1f / 1024f;
        /// <summary>Steps 1 and 3. The most motion, in internal pixels, between the dilated nearest surface and the
        /// centre texel's own surface at which a pixel whose centre texel lies on the farther surface, by more than
        /// <see cref="DisocclusionTolerance"/>, still reads history along the dilated motion. It applies only where the
        /// nearer surface moved, so that its depth test is skipped. Past it that motion carries the pixel beyond the
        /// nearer surface's edge, onto another texel of the farther surface, so the pixel reprojects by its centre
        /// texel's own motion and depth instead. A 30 display pixel box keyed and crossing a textured wall at 4 display
        /// pixels a frame, 4 internal pixels at Native and 2.7 at Quality, left 63 and 180 of its 840 trail pixels
        /// further from the bare wall than a freshly revealed wall is, because the column behind its trailing edge read
        /// a wall texel 4 pixels away at full confidence each frame. With this and
        /// <see cref="DisocclusionVisibleShare"/> it leaves 1 and 0. The keyed line facts move up to 0.9 internal
        /// pixels a frame and the nearest-depth fact 1, and all keep the dilated history. 1.25 leaves a quarter pixel
        /// over that for the half-float motion target and the float round trip of a background centre, and 1 measures
        /// the same. At 0.5, the moving-surface threshold, the nearest-depth fact fails and a keyed line at 0.6
        /// internal pixels a frame on Quality keeps 12 percent of its contrast against 74. At 1.5 the box at
        /// UltraPerformance, 1.33 internal pixels a frame, leaves 233 pixels against 60. A still nearer surface keeps
        /// dilation: a still box in front of the textured wall under a perspective camera stepping sideways 2 internal
        /// pixels a frame against the wall left 18 and 19 trail pixels at Native and Quality with the rule applied
        /// there, against 6 and 10, and against the sky its edges took 0.0035 and 0.0063 fast flips a pixel a frame
        /// against none. The cost, where a moving object's edge pixel has its centre texel on the farther surface: a
        /// keyed box tilted and crossing the flat wall at 2 internal pixels a frame averages a luma error of 0.00146
        /// and 0.00173 over its edges at Native and Quality against 0.0008 and 0.0014 with dilation, and its temporal
        /// error over the band it crosses is 0.00208 and 0.00260 against 0.00085 and 0.00168. A keyed line one internal
        /// pixel wide kept only 0.26 of its reference energy over its own coverage at both presets until the rule of
        /// <see cref="MovingShareConfidence"/> gave its pixels their current coverage: it keeps 1.05 and 0.97 now,
        /// where dilation kept 1.06 and 0.94 but smeared it to 2.09 and 1.28 over the band with a trail of 8 pixels at
        /// Native, and its band's temporal error is 0.00155 and 0.00202 against 0.00255 and 0.00220. The 30 pixel box
        /// crossing the flat wall at 4 display pixels a frame shows 0.0029, 0.0025, 0.0061 and 0.0071 fast flips a
        /// pixel a frame at its moving edges at Quality, Balanced, Performance and UltraPerformance against 0.0001,
        /// 0.0000, 0.0000 and 0.0007, and its added change at UltraPerformance rises from 0.0034 to 0.0042.</summary>
        public const float DilationReachInternalPixels = 1.25f;
        /// <summary>
        /// Steps 1 and 3, the band, in metres. A pixel whose centre texel lies on a farther surface takes by dilation
        /// the motion of a nearer one. Where that nearer surface moved in the world, its own sample landing more than
        /// this many metres, as internal pixels at its depth through last frame's projection, plus
        /// <see cref="MovingSurfaceMotionFraction"/> of its motion, from where a static point would have been, is wide,
        /// and moves less on screen than the farther surface, as an avatar does under a camera that follows it, the
        /// pixel's history follows the avatar's edge, which stays put on screen while the ground passes under it. Its
        /// colour is the edge's anti-aliased coverage over a mix of the ground that passed, and dilation keeps it for
        /// the edge. A pixel on the avatar itself, whose surface travelled in the world more than
        /// <see cref="FollowedTravelRatio"/> times its motion on screen, is marked too, since the ground its feet stand
        /// on lies within the disocclusion tolerance of its lowest pixels and would take their colour on where the
        /// avatar uncovers it. The pixel stores the band mark beside the edge and the followed mark on the avatar, and
        /// a depth-tested pixel whose dilated nearest surface did not move in the world drops a history that carries
        /// either, so the ground restarts rather than carrying the avatar's colour off with the pan. A pixel beside or
        /// on the avatar, a nearer surface reading the band where its edge was, whose stored depths all lie farther,
        /// and a followed history read where the same surface still shows
        /// (<see cref="FollowedHistoryMotionFraction"/>), keep it.
        /// <para>
        /// A keyed box with ridged pixels, the camera following it across the textured wall under the reach, kept its
        /// luma in 20 and 32 of the 90 wall pixels it uncovered at 0.5 display pixels a frame at Native and Quality, 45
        /// and 87 of 210 at 1, and 101 of 300 at 1.5 on Quality, and the band leaves 0 at each. At Performance it
        /// leaves 3 of 90 and 2 of 210 against 35 and 109 at 0.5 and 1, and 0 from 1.5 on against 168 to 213. At
        /// UltraPerformance it leaves 15 to 36 against 35 to 182: there the reconstruction spreads the box's texel 6
        /// display pixels onto the wall, which the bare-wall floor never shows, and a restart gathers display-scale
        /// sample weight slowly. Over the box's own edges it lowers the edge error against the 4x reference at 0.25,
        /// 0.5 and 1 internal pixel a frame, from 0.0197, 0.0218 and 0.0169 to 0.0173, 0.0175 and 0.0115 at Native, and
        /// its fast flips stay under the reference's but at 0.25 internal pixels a frame on Native, 0.0003 against 0.
        /// </para>
        /// <para>
        /// Each condition is measured. Reprojecting such a pixel by its own motion instead shimmers the edge, fast
        /// flips 0.0134 and 0.0275 at 0.25 internal pixels a frame against the reference's 0 and 0.0069. A surface that
        /// moves more on screen than what lies behind it, a keyed object crossing a still view, keeps its band history
        /// where it leaves it: restarting the pixels it uncovers made a revealed sub-texel line show its raw sample,
        /// brighter than its converged value, in up to 3 display pixels against the lock fact's bound of 1. A still
        /// nearer surface keeps it too: under a zoom the farther surface moves more on screen beside a still object. A
        /// narrow nearer surface, a swaying blade or a thin line, is held by the thin-feature lock instead.
        /// </para>
        /// <para>
        /// A static surface's travel is the motion target's float error on positions relative to the render origin and
        /// the depth's reconstruction, one distance in the world that more pixels show at a higher resolution or a
        /// nearer depth. Orbiting, strafing and creeping past still crates and towers under a perspective camera, near
        /// the origin and 10 km out, it reached 0.075 internal pixels at 3840 wide, past the 0.05 a fixed pixel
        /// threshold allowed. In the world it was at most 0.211, 0.374 and 0.466 mm from the boot pitch of 0.75
        /// radians 12, 22 and 30 m away, 0.508, 0.778 and 1.23 mm from the grazing pitch of 0.26 radians, whose ground
        /// reaches the camera's 500 m far plane, and 0.058 mm from a steep pitch of 1.36, each on the fast orbit, the
        /// 0.508 mm near the origin at Native at a depth of 10.7 m. So 1 mm is 1.3 times the largest static travel to
        /// 22 m. 30 m away from the grazing pitch the fast orbit, its eye moving 1.5 m a frame, passes it on up to 0.24
        /// percent of its texels, and no still surface stores the band there, since a band pixel also travels more
        /// than <see cref="FollowedTravelRatio"/> times its motion on screen or lies beside a nearer edge whose farther
        /// centre moves more on screen. The slowest follow measured, half a
        /// display pixel a frame on UltraPerformance, 0.17 internal pixels, still marks the band. Under the perspective
        /// follow camera the band on the avatar's own pixels takes the ground trail of a walk away from the camera at
        /// the boot pitch from 17 to 55 pixels at Native and Quality to none.
        /// </para>
        /// </summary>
        public const float WorldMotionMetres = 0.001f;
        /// <summary>Step 1's followed surface: a pixel whose centre texel moves with the dilated nearest surface, a
        /// surface that moved in the world (<see cref="WorldMotionMetres"/>), is on a surface the camera follows where
        /// that surface travelled in the world more than this many times its motion on screen, rather than one
        /// crossing a still view, which travels in the world as far as it moves on screen. It stores the followed
        /// mark (<see cref="FollowedHistoryMotionFraction"/>). A pixel of that surface whose dilated nearest is a still
        /// surface in front of it, as the ground just in front of an avatar's lowest face under a low camera, stores
        /// the band mark where its own centre texel travelled that far, and the ground it uncovers drops it: walking
        /// away at 1 display pixel a frame on Quality at the low pitch, the trail's worst over the jitter's start
        /// phases falls from 10 pixels to 1.</summary>
        public const float FollowedTravelRatio = 2f;
        /// <summary>
        /// Step 3 beside a followed surface. The avatar's own pixels store the followed mark while the camera follows
        /// it (<see cref="WorldMotionMetres"/>), and a pixel whose nearest surface did not move in the world drops a
        /// history that carries it only where one of the nine current texels around the history position, background
        /// aside, moves on screen otherwise than the pixel reprojected, by more than this share of the pixel's own
        /// motion, and the pixel moved more than <see cref="FollowedStillDisplayPixels"/>. The avatar stays put on
        /// screen while the ground it uncovers pans at the walk's speed, so a ground pixel reading the avatar's
        /// history there drops it. On the frame the avatar stops in the world, or turns back through zero travel, its
        /// pixels read their own history in place, and under a damped camera that eases on after it, where the same
        /// avatar shows moving with them, apart only by its parallax, and keep it.
        /// <para>
        /// Dropped there, every pixel of the avatar showed one jittered sample on that frame and took 15 frames to
        /// settle: its pixels farther inside its outline than the reconstruction's reach read 3.7 and 3.0 times the
        /// error against the 4x reference of a kept history at Native and Quality on the orthographic follow walk, and
        /// 1.2 and 1.5 times on the perspective one. With the rule those pixels read exactly the error of the same
        /// avatar standing still in the world on every frame after a stop or a reversal. Past the most they read over
        /// it while walking, they read at most 0.3 percent of it more under the damped camera, and every pixel showing
        /// the avatar 0.8 percent after a stop or a reversal and 2.5 under the damped camera, where the ground or wall
        /// passes behind its outline. Only the texel at the history position, or the four around it, left the ground
        /// an avatar uncovers its colour: up to 8 pixels at Quality where the walk left none, and 18 of 90 against 3
        /// on the orthographic follow at Performance.
        /// </para>
        /// </summary>
        public const float FollowedHistoryMotionFraction = 0.5f;
        /// <summary>
        /// Step 3 beside a followed surface. A pixel that moved on screen no more than this many display pixels since
        /// the last frame reads a history carrying the followed mark where it shows it, and keeps it, whatever moves
        /// among the nine texels (<see cref="FollowedHistoryMotionFraction"/>). On the frame an avatar stops with the
        /// camera, or turns back through zero travel, its pixels move exactly nothing on screen, so a share of that
        /// motion was no threshold, and a second keyed box walking past behind the avatar at 1 display pixel a frame
        /// dropped the history of the outline beside it, up to 1.12 times a kept history's error on the orthographic
        /// walk at Native. A floor on the share holds nothing under the passer's speed, and at one internal pixel, the
        /// passer's speed at Quality, it let the ground a walk uncovers keep the avatar's colour. A reversal ramping
        /// through zero passes 0.125 display pixels a frame at the slowest walk measured, where a floor of 0.25 left
        /// some of that ground its history. Against the reference a walk away slower than the floor reads worse with
        /// it, 12.8 to 22.4/255 on up to 31 uncovered ground pixels against 10.1 to 13.2 without, and a walk sideways
        /// and an acceleration from standstill over 32 frames read better, 6.6 to 9.7 against 8.7 to 19.3. A walk at
        /// 1 m/s seen from 30 m moves the ground 0.36 display pixels a frame at 1920x1080.
        /// </summary>
        public const float FollowedStillDisplayPixels = 0.1f;
        /// <summary>Step 3. A depth-tested pixel whose expected surface shows under less than this share of the
        /// bilinear weight of its four stored depths was mostly covered last frame, and drops its history, unless the
        /// nearest of the four is narrow, its run of texels along a row or a column, apart in depth from those either
        /// side by <see cref="DisocclusionTolerance"/>, at most two long, and the stored state says no moving surface
        /// showed there last frame, or the lock the pixel carries lies within half of <see cref="LockDecay"/> of whole:
        /// a ridge refreshed it on the last frame and less than that was released since. Otherwise any one stored depth
        /// at the expected surface keeps the history, which spares a sub-pixel edge, but it also kept the ring of
        /// pixels around a moving object's old place, whose footprint reaches past the edge, from ever reading as
        /// revealed. The keyed box crossing a textured wall kept its colour in its bottom row at Native, and in its top
        /// and bottom rows and its first revealed column at Quality, and with <see cref="DilationReachInternalPixels"/>
        /// alone left 9 and 38 trail pixels against 1 and 0 with both. A quarter leaves 17 at Quality. Three quarters
        /// raise the moving edges' fast flips at Balanced and UltraPerformance to 0.0082 and 0.014 from 0.0025 and
        /// 0.0071, and a nearer surface crossing a held line on UltraPerformance ghosts 2 display pixels. Without
        /// either exception a still line narrower than a texel against the sky, or a blade over ground, drops its
        /// history on the frames the jitter misses it: the sky line's centre averages 13.9 percent at Native against
        /// its coverage of 37.5. Counting only a single texel as narrow and taking no account of the lock, two still
        /// blades side by side lost the left one's history at Quality, from 45.3 to 18.4 percent, and the right of
        /// three still blades side by side, which holds a lock, fell from 41.8 to 13.8 at Native. Keeping the history
        /// for any lock whose hold on the clip is whole, at least 1 / <see cref="LockHoldGain"/>, also kept it where a
        /// ridged, textured keyed box crossing the textured wall at 2 display pixels a frame left, since its motion
        /// releases only a third of its locks there: 10 and 39 of its 420 trail pixels at Native and Quality passed a
        /// freshly revealed wall's difference by more than 0.05, against 3 and 4 with the lock within half a decay of
        /// whole. The three still blades hold as before, and a teleported box's corners hold 7 and 9 of 144 pixels at
        /// Native and Quality against 8 and 12. A keyed line one or two internal texels wide moving on at 2 internal
        /// pixels a frame is as narrow as a missed still blade: taking any narrow feature, the narrow exception kept
        /// its colour where it left over the textured wall, 128 and 177 of the trail pixels for the line one texel wide
        /// and 50 and 136 for two, against 11 and 23, and 0 and 3, where the moving surface's stored state rules it
        /// out, and 0 and 1, and 0 and 0, since the pixels beside such a line restart once it moves on
        /// (<see cref="MovingShareConfidence"/>). The still blades and lines hold as before. Where the stored state
        /// says a moving surface showed there, neither exception applies, and any weight on a stored depth nearer than
        /// expected drops the history. A keyed box with ridged pixels walking across the textured wall at 2.5 display
        /// pixels a frame while the camera follows it, as a third-person camera follows an avatar, kept its luma in 38
        /// and 22 of the 510 wall pixels it uncovered at Native and Quality, against 0 and 0 now: its pixels hold locks
        /// near whole, and a footprint it covered in half kept a history half its colour. At 1.5 display pixels a frame
        /// on Quality, 1 internal pixel, it kept 101 of 300 with the rule held to surfaces past
        /// <see cref="DilationReachInternalPixels"/>, and 5 with it on every moving surface. The keyed box crossing the
        /// textured wall at 4 display pixels a frame leaves 6 and 12 of 840 at Performance and UltraPerformance against
        /// 17 and 63. On every moving surface the rule moves two lock facts' printed lines, both within their bounds: a
        /// dark surface crossing a held line at 0.6 internal pixels a frame on UltraPerformance shows one revealed
        /// pixel at half the line's contrast, from its first raw sample, against none, and a keyed line at 0.9 display
        /// pixels a frame on Quality keeps at least 63 percent of its still contrast against 59, and no longer trails
        /// its 1 pixel at 17 percent.</summary>
        public const float DisocclusionVisibleShare = 0.5f;
        /// <summary>Steps 5 and 8. Beside a keyed feature at most two texels wide in this frame's depth that moves more
        /// than <see cref="DilationReachInternalPixels"/> against the surface behind it, a pixel that reprojected by
        /// its own motion holds that surface alone in its history, and the feature this frame's reconstruction saw is
        /// missing from it. The feature's share of the reconstruction weight, on the texels nearer than the pixel's own
        /// surface by <see cref="DisocclusionTolerance"/>, takes the feature's current colour in the history, and the
        /// pixel stores this confidence, since that colour holds for one frame, so it restarts from the current
        /// sample on the next. A keyed line one internal pixel wide crossing the flat wall at 2 internal pixels a frame
        /// kept 0.26 of its reference energy over its own coverage at Native and Quality, where MSAA 4x keeps 0.999:
        /// its pixels read converged wall and took the line at about a sixteenth a frame. It keeps 1.05 and 0.97 now,
        /// with fast flips of 0.019 and 0.022 against MSAA 4x's 0.021 and 0.022, and its band's temporal error is
        /// 0.00155 and 0.00202 against 0.00288 and 0.00376. Keeping the confidence whole left a line two texels wide 7
        /// of its 630 trail pixels over the textured wall at Quality, and scaling it by one minus the share left 6,
        /// against 0 now, because over a grey texture the clip pulls a pixel holding the feature's colour to the
        /// neighbourhood mean at full confidence. The line one texel wide leaves 0 and 1 against 11 and 23. Applied
        /// beside any moving surface, the same colour raised a keyed box edge's fast flips at Native from 0.00058 to
        /// 0.00112, so it waits for a narrow feature. The narrow test reads the run through one texel, so it also fires
        /// where a wide object is one or two texels across in this frame's depth, at a corner's tip or on a face seen
        /// at a grazing angle: it raised the tilted keyed box's fast flips from 0.00058 to 0.00068 at Native and from
        /// 0.00168 to 0.00186 at Quality, within the bounds of 0.00088 and 0.00273. A pixel whose lock holds takes the
        /// colour only for the share the hold leaves, and one whose hold is whole keeps its own history and confidence.
        /// A still line three eighths of a texel wide beside a keyed passer one or two texels wide, sliding past half a
        /// texel away at 2 display pixels a frame, fell on its worst frame to 0.02 of its energy at Native and 0.00 at
        /// Quality, because the confidence stored beside it dropped the line's history on the frames the jitter missed
        /// it. It keeps 0.91 and 0.94 now. Storing one minus the share of the confidence left it at 0.03 to 0.39 and
        /// took the fast keyed line to 0.80 and 0.74 of its reference, and keeping the confidence where the hold is
        /// whole left it at 0.37 or less and the line one texel wide crossing the textured wall 29 and 58 trail
        /// pixels.</summary>
        public const float MovingShareConfidence = 0f;
        /// <summary>Step 4 for a converged pixel (<see cref="ShaderSources.TemporalDisplayKernelGlsl"/>). Below
        /// Native a pixel that kept its history takes the colour of the 3x3 reconstructed with the Lanczos 2 sized in
        /// display pixels as its carried confidence rises from this to whole, in place of the one sized in internal
        /// pixels, which band-limits each frame to the internal Nyquist. Its alpha keeps the internal reconstruction.
        /// On the mip bias checkerboard Performance keeps 0.768 of native's local contrast against 0.747, and Quality
        /// 0.883 against 0.781. From 0.25, with <see cref="DisplayKernelFullWeight"/> at 0.1, it kept 0.785 and 0.896,
        /// and a ridged keyed box crossing the textured wall at Quality left 6 pixels in excess of its trail floor
        /// against 4, under a bound of 7.</summary>
        public const float DisplayKernelConfidenceStart = 0.5f;
        /// <summary>Step 4 for a converged pixel. The display-sized share falls to none as the pixel moves this many
        /// display pixels a frame, where the history is resampled every frame and a sparse current sample shows as
        /// flicker. With neither this nor <see cref="DisplayKernelConfidenceStart"/>, a followed keyed box at 2 display
        /// pixels a frame at Quality left an excess of 3 against its bound of 2, and a followed avatar on perspective
        /// ground 2 against 1 at Quality and 14 against 1 at UltraPerformance.</summary>
        public const float DisplayKernelMotionPixels = 2f;
        /// <summary>Step 4 for a converged pixel. The display-sized kernels' sum over the 3x3 is whole where a sample
        /// lands on the pixel and falls to zero, or below it, where the jitter puts the pixel between samples, which at
        /// Performance is half an internal pixel from each. Below this sum the reconstruction says little, and the
        /// pixel takes it only in proportion. Not measured apart: the one variant at 0.1 also started the share at a
        /// quarter of the confidence (<see cref="DisplayKernelConfidenceStart"/>).</summary>
        public const float DisplayKernelFullWeight = 0.25f;
    }
}
