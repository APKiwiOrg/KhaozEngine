using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game;

// E5 (UE-style step-event mesh smoothing): ReplicatedCharacterAnimators accumulates the local entity's exported
// discrete-step impulse (CharacterSample.StepCumulativeY, diffed) into a MESH vertical offset that decays exponentially
// to zero, subtracted from the drawn feet so the mesh starts at the pre-step height and eases up/down to the true feet.
// This is the dedicated layer for ISOLATED steps the continuous glide (ClimbRate) declines (it renders singles raw, so
// they pop). These pin: the ease profile (monotone, bounded by the step, sub-perceptual by ~150 ms), flat-ground
// identity, the hard-cut guards (teleport / snap distance / disabled), remote inertness, and the bridge-side exactly-once
// consumption (a steady cumulative is applied once, not re-added every frame).
public class StepOffsetSmoothingTests
{
    const float Dt = 1f / 60f;

    static (Skeleton, IReadOnlyDictionary<LocomotionState, AnimationClip>) Rig()
    {
        var skel = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
        AnimationClip Park(string n) => new(n, 1f, new List<JointTrack>
        { new JointTrack(0) { Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear) } });
        var clips = new Dictionary<LocomotionState, AnimationClip>
        {
            [LocomotionState.Idle] = Park("i"), [LocomotionState.Walk] = Park("w"), [LocomotionState.Run] = Park("r"),
            [LocomotionState.Jump] = Park("j"), [LocomotionState.Fall] = Park("f"),
        };
        return (skel, clips);
    }

    static ReplicatedCharacterAnimators NewBridge(CharacterAnimatorTuning? tuning = null)
    {
        var (skel, clips) = Rig();
        return new ReplicatedCharacterAnimators(() => new AnimatedCharacter(skel, clips, LocomotionThresholds.Default),
            tuning ?? CharacterAnimatorTuning.Default);
    }

    // A local exact-movement sample at a given feet height, with the step accumulator and (optional) continuous climb rate.
    static CharacterSample Local(float y, float stepCumulative, float climbRate = 0f, float planarSpeed = 3f)
        => new CharacterSample(1, new Vector3(0f, y, 0f), isLocal: true, grounded: true, verticalVelocity: 0f,
            planarSpeed: planarSpeed, swimming: false, climbRate: climbRate, stepCumulativeY: stepCumulative);

    static float DrawnY(ReplicatedCharacterAnimators a) => a.Live[0].RenderPosition.Y;

    // Largest single-frame render catch-up for an ISOLATED step under the given tuning: flat approach (mesh at the true
    // feet), a one-tick full-step commit (the mesh FREEZES at the pre-step height), then eased frames with the true feet
    // steady. Returns the biggest frame-to-frame |change| in the drawn feet-Y over the event. The freeze offset decays
    // exponentially, so the ease is front-loaded and this max is the FIRST eased frame = step * (1 - e^(-rate * Dt)).
    static float LargestStepCatchUp(CharacterAnimatorTuning tuning, float step)
    {
        var a = NewBridge(tuning);
        for (int i = 0; i < 4; i++) a.Update(new[] { Local(0f, 0f) }, Dt);   // flat: identity
        float prev = DrawnY(a);
        float maxDelta = 0f;
        for (int i = 0; i < 21; i++)                                          // commit frame + eased frames, true feet steady
        {
            a.Update(new[] { Local(step, step) }, Dt);
            float y = DrawnY(a);
            maxDelta = MathF.Max(maxDelta, MathF.Abs(y - prev));
            prev = y;
        }
        return maxDelta;
    }

    [Fact]
    public void IsolatedStepUp_MeshStartsAtPreStep_ThenEasesMonotoneToTrueFeet()
    {
        var a = NewBridge();
        // Flat approach: the mesh is at the true feet (identity).
        for (int i = 0; i < 4; i++) a.Update(new[] { Local(0f, 0f) }, Dt);
        Assert.Equal(0f, DrawnY(a), 5);

        // The sim commits a 0.30 m step-up in one tick (the pop the continuous glide would render raw): the true feet jump
        // to 0.30 and the step accumulator jumps to 0.30. The MESH must start at the PRE-step height (0), not pop up.
        a.Update(new[] { Local(0.30f, 0.30f) }, Dt);
        Assert.True(MathF.Abs(DrawnY(a) - 0f) < 0.01f, $"the mesh should start at the pre-step height (~0), got {DrawnY(a):F3}");

        // Then it eases monotonically up to the true feet (0.30), never overshooting and never lagging more than the full
        // step (never below the pre-step height), settling sub-perceptually (< 3 mm) by ~200 ms.
        float prev = DrawnY(a); float settleT = -1f;
        for (int i = 1; i <= 14; i++)   // 14 frames = ~233 ms
        {
            a.Update(new[] { Local(0.30f, 0.30f) }, Dt);
            float y = DrawnY(a);
            Assert.True(y >= prev - 1e-4f, $"frame {i}: mesh must ease UP monotonically ({prev:F4} -> {y:F4})");
            Assert.True(y <= 0.30f + 1e-4f, $"frame {i}: mesh must never overshoot the true feet ({y:F4} > 0.30)");
            Assert.True(y >= -1e-4f, $"frame {i}: mesh must never lag more than the full step ({y:F4} < pre-step 0)");
            if (settleT < 0f && 0.30f - y < 0.003f) settleT = (i + 1) * Dt;
            prev = y;
        }
        Assert.True(settleT > 0f && settleT <= 0.20f, $"the step-up should settle (<3 mm) within ~200 ms, took {settleT * 1000f:F0} ms");
    }

    [Fact]
    public void IsolatedStepDown_MeshStartsAbove_ThenEasesMonotoneDownToTrueFeet()
    {
        var a = NewBridge();
        for (int i = 0; i < 4; i++) a.Update(new[] { Local(0f, 0f) }, Dt);
        Assert.Equal(0f, DrawnY(a), 5);

        // A 0.30 m step-DOWN: true feet drop to -0.30, accumulator drops to -0.30. The mesh starts at the PRE-step height
        // (0, above the drop) and eases DOWN.
        a.Update(new[] { Local(-0.30f, -0.30f) }, Dt);
        Assert.True(MathF.Abs(DrawnY(a) - 0f) < 0.01f, $"the mesh should start at the pre-step height (~0), got {DrawnY(a):F3}");

        float prev = DrawnY(a);
        for (int i = 1; i <= 12; i++)
        {
            a.Update(new[] { Local(-0.30f, -0.30f) }, Dt);
            float y = DrawnY(a);
            Assert.True(y <= prev + 1e-4f, $"frame {i}: mesh must ease DOWN monotonically ({prev:F4} -> {y:F4})");
            Assert.True(y >= -0.30f - 1e-4f, $"frame {i}: mesh must never overshoot the true feet ({y:F4} < -0.30)");
            Assert.True(y <= 1e-4f, $"frame {i}: mesh must never lag more than the full step ({y:F4} > pre-step 0)");
            prev = y;
        }
        Assert.True(-0.30f - DrawnY(a) > -0.002f, "the step-down should have settled onto the true feet by 200 ms");
    }

    [Fact]
    public void StepUp_WithInterTickInterp_NeverSinksBelowThePreStepFloor()
    {
        // The interp/commit phase mismatch: the sim commits the step at a tick boundary (the cumulative jumps FULLY), but
        // the sample feet-Y is the inter-tick-interpolated render position, only PART way up on the frames right after.
        // Adding the full impulse to that mid-interp height would SINK the mesh below the pre-step floor (a reversal worse
        // than the pop). The freeze must keep the mesh at/above the pre-step floor (0). RED for a raw-impulse accumulator.
        var a = NewBridge();
        for (int i = 0; i < 3; i++) a.Update(new[] { Local(0f, 0f) }, Dt);
        // Step-up commit: cumulative jumps to 0.30 on the FIRST render frame; the interpolated feet climb 0 -> 0.30 over 2.
        a.Update(new[] { Local(0.15f, 0.30f) }, Dt);   // frame 1: feet mid-interp (0.15), cumulative already full
        Assert.True(DrawnY(a) >= -1e-4f, $"the mesh sank {DrawnY(a) * 1000f:F0} mm below the pre-step floor (interp/commit overshoot)");
        a.Update(new[] { Local(0.30f, 0.30f) }, Dt);   // frame 2: interp completes
        Assert.True(DrawnY(a) >= -1e-4f, $"the mesh sank {DrawnY(a) * 1000f:F0} mm below the pre-step floor after the interp completed");
        for (int i = 0; i < 12; i++) { a.Update(new[] { Local(0.30f, 0.30f) }, Dt); Assert.True(DrawnY(a) >= -1e-4f && DrawnY(a) <= 0.30f + 1e-4f); }
    }

    [Fact]
    public void StepDown_WithInterTickInterp_NeverBumpsAboveThePreStep()
    {
        // Mirror for a step-down: adding the full negative impulse to the mid-interp feet would BUMP the mesh ABOVE the
        // pre-step height (an upward pop then a drop). The freeze must keep the mesh at/below the pre-step (0). RED for a
        // raw-impulse accumulator (it would read ~+0.15 on the first commit frame).
        var a = NewBridge();
        for (int i = 0; i < 3; i++) a.Update(new[] { Local(0f, 0f) }, Dt);
        a.Update(new[] { Local(-0.15f, -0.30f) }, Dt);   // frame 1: feet mid-interp (-0.15), cumulative already full -0.30
        Assert.True(DrawnY(a) <= 1e-4f, $"the mesh bumped {DrawnY(a) * 1000f:F0} mm above the pre-step (interp/commit overshoot)");
        a.Update(new[] { Local(-0.30f, -0.30f) }, Dt);
        Assert.True(DrawnY(a) <= 1e-4f, $"the mesh bumped above the pre-step after the interp completed ({DrawnY(a):F3})");
        for (int i = 0; i < 12; i++) { a.Update(new[] { Local(-0.30f, -0.30f) }, Dt); Assert.True(DrawnY(a) <= 1e-4f && DrawnY(a) >= -0.30f - 1e-4f); }
    }

    [Fact]
    public void FlatGround_MeshIsByteIdentical_ToTrueFeet()
    {
        var a = NewBridge();
        float[] heights = { 0f, 0.001f, -0.002f, 0.0f, 0.003f };   // tiny terrain-follow bumps, no step
        foreach (float h in heights)
        {
            a.Update(new[] { Local(h, 0f) }, Dt);
            Assert.Equal(h, DrawnY(a), 6);   // no step accumulator movement -> zero offset -> exact identity
        }
    }

    [Fact]
    public void SteadyCumulative_AfterAStep_IsConsumedExactlyOnce_NotReAddedEveryFrame()
    {
        // The bridge DIFFS the cumulative, so a step's impulse enters the offset ONCE (on the frame the cumulative
        // changes) and then only DECAYS. If it re-added the (steady) cumulative each frame, the offset would GROW and the
        // mesh would sink further below the true feet every frame instead of easing up. Pin the once-only consumption:
        // after the step frame, the mesh only rises (offset only shrinks).
        var a = NewBridge();
        a.Update(new[] { Local(0f, 0f) }, Dt);
        a.Update(new[] { Local(0.30f, 0.30f) }, Dt);   // step
        float afterStep = DrawnY(a);
        Assert.True(MathF.Abs(afterStep) < 0.01f, $"mesh should start near pre-step, got {afterStep:F3}");
        float prev = afterStep;
        for (int i = 0; i < 6; i++)
        {
            a.Update(new[] { Local(0.30f, 0.30f) }, Dt);   // cumulative UNCHANGED - must not re-add
            float y = DrawnY(a);
            Assert.True(y > prev - 1e-4f, $"frame {i}: a steady cumulative must not re-add (mesh sank {prev:F4} -> {y:F4})");
            prev = y;
        }
    }

    [Fact]
    public void Teleport_SnapRenderHeight_ZeroesOffset_HardCut()
    {
        var a = NewBridge();
        a.Update(new[] { Local(0f, 0f) }, Dt);
        a.Update(new[] { Local(0.30f, 0.30f) }, Dt);   // build a step offset
        Assert.True(MathF.Abs(DrawnY(a)) < 0.01f, "offset should be active (mesh below true feet)");

        // A teleport: the consumer snaps, and the destination carries a re-baselined cumulative (ClientPrediction
        // Reset/Reseed zeroed it). The mesh must HARD-CUT to the true feet, not read the cumulative reset as a step.
        a.SnapRenderHeight(1);
        a.Update(new[] { Local(5.0f, 0f) }, Dt);       // teleported far, cumulative reset to 0
        Assert.Equal(5.0f, DrawnY(a), 5);              // exact hard cut, no residual offset, no spurious step
    }

    [Fact]
    public void SnapDistanceGuard_HugeCumulativeJump_HardCuts_NotEased()
    {
        // A per-frame cumulative jump larger than SlopeGlideSnapDistance is not a real step (a teleport re-baseline that
        // slipped the snap re-sync). The offset must hard-cut to 0 instead of easing a 3 m "step".
        var a = NewBridge();
        a.Update(new[] { Local(0f, 0f) }, Dt);
        a.Update(new[] { Local(3.0f, 3.0f) }, Dt);     // 3 m > SlopeGlideSnapDistance (1.5): not a step
        Assert.Equal(3.0f, DrawnY(a), 5);              // rendered raw, no easing
    }

    [Fact]
    public void Disabled_StepSmoothing_RendersRaw_ThePopBaseline()
    {
        // RED baseline: with the step smoother disabled (rate <= 0) the isolated step POPS - the drawn feet jump the whole
        // step in one frame (exactly today's behaviour, which this feature fixes). Contrast the enabled case above.
        var tuning = CharacterAnimatorTuning.Default; tuning.StepSmoothingRate = 0f;
        var a = NewBridge(tuning);
        a.Update(new[] { Local(0f, 0f) }, Dt);
        float before = DrawnY(a);
        a.Update(new[] { Local(0.30f, 0.30f) }, Dt);
        float after = DrawnY(a);
        Assert.True(MathF.Abs(after - before - 0.30f) < 1e-4f, $"disabled: the step should pop the full 0.30 in one frame (delta {after - before:F4})");
    }

    [Fact]
    public void Remote_NoStepAccumulator_MeshTracksRaw()
    {
        // Remotes carry StepCumulativeY == 0 (the impulse rides no wire - their singles are softened by position
        // interpolation elsewhere), so the step-offset layer is INERT for them: the mesh tracks the sample position raw.
        var a = NewBridge();
        var remote = new CharacterSample(2, new Vector3(0f, 0f, 0f), isLocal: false, grounded: true, verticalVelocity: 0f);
        a.Update(new[] { remote }, Dt);
        var remoteStepped = new CharacterSample(2, new Vector3(0f, 0.30f, 0f), isLocal: false, grounded: true, verticalVelocity: 0f);
        a.Update(new[] { remoteStepped }, Dt);
        Assert.Equal(0.30f, DrawnY(a), 5);   // no offset accumulated (cumulative stayed 0)
    }

    [Fact]
    public void FirstRiserThenGlide_ComposesWithoutSeam()
    {
        // Composition (constraint 3): the FIRST riser of a run gets the step-offset (climbRate still 0), then the
        // continuous glide engages (climbRate > 0) and the step-offset decays out. Feed a realistic entry: flat approach,
        // one riser via the step-offset, then a smooth continuous climb via ClimbRate with a STEADY cumulative. Assert the
        // drawn feet move smoothly (no frame-to-frame jump larger than a fraction of a riser) across the handoff, and
        // converge onto the climbing true feet (the first-riser offset decays out).
        var a = NewBridge();
        for (int i = 0; i < 3; i++) a.Update(new[] { Local(0f, 0f) }, Dt);

        // First riser: true feet at 0.30, cumulative 0.30, climbRate still 0 (signal not engaged).
        a.Update(new[] { Local(0.30f, 0.30f, climbRate: 0f) }, Dt);

        // Continuous climb: true feet rise smoothly, climbRate steady (~1.34 m/s walk on the 0.30/0.40 stair), cumulative
        // frozen at 0.30 (no new discrete step). Per frame the true feet rise climbRate*dt. The seam signature would be a
        // BACKWARD pop or an OVERSHOOT above the climbing feet when the signal engages mid-decay; the forward ease itself
        // (the first-riser offset decaying while the glide raises the height) is the intended smoothing, not a seam.
        float trueY = 0.30f; const float climb = 1.34f; float prevDrawn = DrawnY(a);
        for (int i = 0; i < 24; i++)
        {
            trueY += climb * Dt;
            a.Update(new[] { Local(trueY, 0.30f, climbRate: climb) }, Dt);
            float y = DrawnY(a);
            Assert.True(y >= prevDrawn - 1e-4f, $"frame {i}: no backward pop across the glide handoff ({prevDrawn:F4} -> {y:F4})");
            Assert.True(y <= trueY + 1e-3f, $"frame {i}: mesh must not overshoot ABOVE the climbing feet ({y:F4} > {trueY:F4})");
            prevDrawn = y;
        }
        // The mesh converged onto the climbing true feet (the first-riser offset decayed out) - a clean handoff.
        Assert.True(MathF.Abs(prevDrawn - trueY) < 0.01f, $"the mesh should track the climbing feet after the handoff (drawn {prevDrawn:F3} vs true {trueY:F3})");
    }

    [Fact]
    public void IsolatedStep_LargestSingleFrameCatchUp_StaysBelowTheNearPopBound()
    {
        // Lock the StepSmoothingRate taste constant. The step smoother eases an isolated riser as a decaying FREEZE
        // offset, so the biggest single-frame move is the FIRST eased frame = step * (1 - e^(-rate*Dt)). At the shipped
        // default (30/s) that is ~39% of the step - a soft settle, not a quick catch-up. The taste call was 30/s OVER a
        // fast-settle 40+/s (see the CharacterAnimatorTuning.StepSmoothingRate derivation: "Gentler than a fast-settle
        // rate (40+/s)"). This pins that margin: the largest single-frame catch-up must stay STRICTLY BELOW the catch-up
        // the rejected 40/s fast-settle would produce - a near-pop bound DERIVED FROM THE DECAY CONSTANTS (rate, Dt, step),
        // not a bare magnitude literal - so the guard trips if StepSmoothingRate is bumped into the rejected band.
        const float Step = 0.30f;
        const float RejectedFastSettleRate = 40f;   // the doc's rejected "40+/s" fast-settle boundary (30/s was chosen over it)
        float nearPopBound = Step * (1f - MathF.Exp(-RejectedFastSettleRate * Dt));   // ~0.487 * step

        float catchUp = LargestStepCatchUp(CharacterAnimatorTuning.Default, Step);

        // The default's largest catch-up matches the analytic decay of its own rate and is the documented ~40% of the step.
        float expectedDefault = Step * (1f - MathF.Exp(-CharacterAnimatorTuning.DefaultStepSmoothingRate * Dt));
        Assert.Equal(expectedDefault, catchUp, 4);
        Assert.InRange(catchUp / Step, 0.38f, 0.41f);   // ~39% at 30/s

        // GREEN at 30/s: the largest single-frame catch-up is strictly below the 40/s near-pop bound (comfortable margin,
        // 30/s ~= 39% vs the bound ~= 49%).
        Assert.True(catchUp < nearPopBound,
            $"largest single-frame catch-up {catchUp / Step:P1} of the step must stay below the {RejectedFastSettleRate}/s near-pop bound {nearPopBound / Step:P1}");

        // RED at 40+/s (the direction the guard protects): bumping StepSmoothingRate into the rejected fast-settle band
        // makes the first eased frame meet/exceed the bound. Demonstrated at 45/s (~53%, unambiguously above); the bound
        // itself sits at the 40/s level, so any rate >= 40/s trips it.
        var faster = CharacterAnimatorTuning.Default;
        faster.StepSmoothingRate = 45f;
        Assert.False(LargestStepCatchUp(faster, Step) < nearPopBound,
            "a 45/s fast-settle rate (in the rejected 40+/s band) must NOT stay below the near-pop bound");
    }
}
