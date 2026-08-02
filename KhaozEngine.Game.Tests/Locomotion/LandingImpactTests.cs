using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// MoveState.LandingImpactSpeed: the downward speed (m/s, non-negative) the step is about to erase on the ONE tick a
// character transitions airborne -> grounded, captured before the landing zeroes VerticalVelocity. Zero on every other
// tick. It is the fact the sim already computed, exported instead of reconstructed downstream - the same reasoning that
// put StepDeltaY and CommandedVelocity on MoveState - and it is what a game reads to apply fall damage without
// finite-differencing a position it cannot trust across a landing.
//
// These pin the value on the analytic-terrain path (no physics world needed): a jump-and-land round trip, a walk off a
// ledge, a grounded walk, the MaxFallSpeed cap, a landing that a buffered jump immediately re-launches, and the swim
// paths that must never fabricate an impact.
public class LandingImpactTests
{
    const float Dt = 1f / 30f;

    static MoveTuning Tuning => MoveTuning.Default;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static MoveCommand Idle => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: false);
    static MoveCommand Jump => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
    static MoveCommand East(bool jump = false) => new(new Vector2(1f, 0f), run: false, cameraYaw: 0f, jump: jump);

    // What the step's step-2 vertical integrate produces this tick from a carried vertical velocity: gravity applied,
    // then the terminal clamp. This is EXACTLY the number the landing erases, so it is the impact the latch must report.
    static float IntegratedFall(float carriedVertical, in MoveTuning t)
    {
        float v = carriedVertical - t.Gravity * Dt;
        return v < -t.MaxFallSpeed ? -t.MaxFallSpeed : v;
    }

    [Fact]
    public void JumpAndLand_ReportsThePreLandingDownwardSpeed_OnExactlyTheLandingTick()
    {
        MoveTuning t = Tuning;
        var s = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true };
        s = CharacterMovement.Step(s, Idle, Dt, Flat, t);          // settle
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.LandingImpactSpeed);

        s = CharacterMovement.Step(s, Jump, Dt, Flat, t);          // launch
        Assert.False(s.Grounded);
        Assert.Equal(0f, s.LandingImpactSpeed);

        int landingTick = -1;
        float impactOnLanding = 0f, expected = 0f, impactTickBefore = -1f, impactTickAfter = -1f;
        for (int i = 0; i < 120; i++)
        {
            float carried = s.VerticalVelocity;
            bool wasGrounded = s.Grounded;
            float previousImpact = s.LandingImpactSpeed;
            s = CharacterMovement.Step(s, Idle, Dt, Flat, t);
            if (landingTick < 0 && !wasGrounded && s.Grounded)
            {
                landingTick = i;
                impactOnLanding = s.LandingImpactSpeed;
                impactTickBefore = previousImpact;
                expected = -IntegratedFall(carried, t);
            }
            else if (landingTick >= 0 && i == landingTick + 1)
            {
                impactTickAfter = s.LandingImpactSpeed;
            }
            else if (landingTick < 0)
            {
                Assert.Equal(0f, s.LandingImpactSpeed);            // every airborne tick before the landing reports 0
            }
        }

        Assert.True(landingTick >= 0, "the jump never landed");
        Assert.True(expected > 5f, $"the fall was too gentle to be a meaningful pin ({expected:F3} m/s)");
        Assert.Equal(expected, impactOnLanding, 4);
        Assert.Equal(0f, impactTickBefore);                        // the tick BEFORE the landing
        Assert.Equal(0f, impactTickAfter);                         // and the tick AFTER it
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.LandingImpactSpeed);                    // still 0 many grounded ticks later
    }

    [Fact]
    public void WalkOffALedge_ReportsTheImpactOnTheLandingTick()
    {
        // The direction-aware slope gate lets a grounded walk leave a clifftop, and the fall that follows is a landing like
        // any other, so the drop's speed must arrive on the tick the character reaches the lower ground.
        MoveTuning t = Tuning;
        const float EdgeX = 5f, Drop = -4f;
        Func<float, float, float> ground = (x, z) => x < EdgeX ? 0f : Drop;
        var s = new MoveState { Position = new Vector3(EdgeX - 0.5f, t.CapsuleHalfHeight, 0f), Grounded = true };

        int nonZeroTicks = 0;
        float impact = 0f, expected = 0f;
        for (int i = 0; i < 120; i++)
        {
            float carried = s.VerticalVelocity;
            bool wasGrounded = s.Grounded;
            s = CharacterMovement.Step(s, East(), Dt, ground, t);
            if (s.LandingImpactSpeed != 0f)
            {
                nonZeroTicks++;
                impact = s.LandingImpactSpeed;
                expected = -IntegratedFall(carried, t);
                Assert.True(!wasGrounded && s.Grounded, "an impact was reported on a tick that was not a landing");
            }
        }

        Assert.Equal(1, nonZeroTicks);                             // exactly one landing in a walk-off-and-land run
        Assert.Equal(expected, impact, 4);
        // A 4 m free fall under g=25 reaches ~sqrt(2*25*4) = 14.1 m/s, and the discrete integrate lands near it.
        Assert.InRange(impact, 13f, 15.5f);
        Assert.True(s.Position.Y < t.CapsuleHalfHeight, "the character never left the clifftop");
    }

    [Fact]
    public void GroundedWalk_ReportsZeroOnEveryTick()
    {
        MoveTuning t = Tuning;
        var s = new MoveState { Position = new Vector3(0f, t.CapsuleHalfHeight, 0f), Grounded = true };
        for (int i = 0; i < 90; i++)
        {
            s = CharacterMovement.Step(s, East(), Dt, Flat, t);
            Assert.True(s.Grounded);
            Assert.Equal(0f, s.LandingImpactSpeed);
        }
    }

    [Fact]
    public void TheImpactIsCappedByMaxFallSpeed()
    {
        // Terminal velocity is physical, not a data loss: a fall long enough to reach MaxFallSpeed reports exactly
        // MaxFallSpeed, while a short fall reports its own (smaller) speed. min(actual, MaxFallSpeed), both halves.
        MoveTuning t = Tuning with { MaxFallSpeed = 12f };
        var s = new MoveState { Position = new Vector3(0f, 200f, 0f), Grounded = false };
        float terminal = 0f;
        for (int i = 0; i < 2000 && terminal == 0f; i++)
        {
            s = CharacterMovement.Step(s, Idle, Dt, Flat, t);
            if (s.LandingImpactSpeed != 0f) terminal = s.LandingImpactSpeed;
        }
        Assert.Equal(t.MaxFallSpeed, terminal, 4);

        // The same fall under a terminal speed it never reaches reports the true (sub-terminal) speed instead.
        MoveTuning fast = Tuning with { MaxFallSpeed = 200f };
        var s2 = new MoveState { Position = new Vector3(0f, 2f + fast.CapsuleHalfHeight, 0f), Grounded = false };
        float shortFall = 0f;
        for (int i = 0; i < 200 && shortFall == 0f; i++)
        {
            s2 = CharacterMovement.Step(s2, Idle, Dt, Flat, fast);
            if (s2.LandingImpactSpeed != 0f) shortFall = s2.LandingImpactSpeed;
        }
        Assert.InRange(shortFall, 8f, 12f);                        // ~sqrt(2*25*2) = 10 m/s, far below the 200 cap
    }

    [Fact]
    public void ALandingThatABufferedJumpRelaunches_StillReportsTheImpact()
    {
        // Holding jump through a fall makes step 5 consume the buffer on the very tick the character lands, so the
        // state ends the tick airborne again. The impact still happened, and reporting it is what stops a bunny-hop
        // from cancelling fall damage. Grounded false + a nonzero impact is therefore a legitimate combination.
        MoveTuning t = Tuning;
        var s = new MoveState { Position = new Vector3(0f, 6f + t.CapsuleHalfHeight, 0f), Grounded = false };
        float impact = 0f;
        bool groundedOnTheImpactTick = true;
        for (int i = 0; i < 120 && impact == 0f; i++)
        {
            s = CharacterMovement.Step(s, Jump, Dt, Flat, t);      // jump held every tick
            if (s.LandingImpactSpeed != 0f) { impact = s.LandingImpactSpeed; groundedOnTheImpactTick = s.Grounded; }
        }
        Assert.True(impact > 5f, $"a bunny-hopped landing reported no impact ({impact:F3} m/s)");
        Assert.False(groundedOnTheImpactTick);                     // re-launched by the buffered jump the same tick
        Assert.True(s.VerticalVelocity > 0f, "the buffered jump should have re-launched the character");
    }

    [Fact]
    public void FallingIntoWaterFabricatesNoImpact_AndASwimExitReportsOnlyThePostExitDrop()
    {
        // The swim-exit decision, with its evidence. SwimStep returns Grounded = false UNCONDITIONALLY (gravity and
        // ground-snap are suspended while swimming), so no swim tick is ever an airborne -> grounded transition and none
        // of them can latch an impact. That covers the case where fabricating one would be worst: a character diving off
        // a cliff into a lake. The water broke the fall, the buoyancy settle bleeds the entry velocity out, and nothing
        // is reported - not on the entry tick, not on the sink, not when the settle bottoms out on the lakebed.
        //
        // Only the hysteresis EXIT tick runs the land path, and a swim exit that GROUNDS is therefore a landing like any
        // other, reporting the honest drop the character took AFTER the exit. The entry fall is gone by then, which is
        // the whole point: a 35 m/s dive that ends with the character wading ashore does not report 35 m/s.
        MoveTuning t = Tuning;
        float bedY = -20f;                                         // deep water for the dive, shallows for the exit
        Func<float, float, float> bed = (x, z) => bedY;
        Func<float, float, float, MovementMedium> lake = (x, z, feetY) => new MovementMedium(5f, inWater: true, 1f);
        var s = new MoveState { Position = new Vector3(0f, 30f, 0f), Grounded = false };

        float fastestFall = 0f;
        for (int i = 0; i < 200; i++)
        {
            s = CharacterMovement.Step(s, Idle, Dt, bed, t, groundNormal: null, world: null, clampXz: null, medium: lake);
            fastestFall = MathF.Max(fastestFall, -s.VerticalVelocity);
            Assert.Equal(0f, s.LandingImpactSpeed);
        }
        Assert.True(s.Swimming, "the character should have entered the water and stayed swimming");
        Assert.True(fastestFall > 20f, $"the entry dive was too gentle to be a meaningful pin ({fastestFall:F1} m/s)");

        // Now the bed under the swimmer rises to the shallows (the headless stand-in for swimming ashore): the floor
        // lifts the settled capsule, submersion drops below SwimExitDepthFraction, and the land path takes over.
        bedY = 4.4f;
        float impact = 0f;
        for (int i = 0; i < 200 && impact == 0f; i++)
        {
            s = CharacterMovement.Step(s, Idle, Dt, bed, t, groundNormal: null, world: null, clampXz: null, medium: lake);
            impact = s.LandingImpactSpeed;
        }
        Assert.False(s.Swimming, "wading into the shallows should have ended the swim");
        Assert.True(s.Grounded, "the character should have settled onto the bed");
        // One tick of gravity from a settled waterline, which is all the exit tick had to fall. Not the dive.
        Assert.InRange(impact, 0.1f, 3f);
        Assert.True(impact < 0.25f * fastestFall,
            $"the swim exit reported the pre-swim entry fall ({impact:F1} m/s against a {fastestFall:F1} m/s dive)");
    }

    [Fact]
    public void TheImpactStreamIsDeterministic_AcrossIdenticalRuns()
    {
        // Both heads run this exact code, so two identical runs produce a bit-identical stream (the guarantee the
        // server's authoritative read and the client's predicted one agree on the same landing).
        static float[] Run()
        {
            MoveTuning t = Tuning;
            var s = new MoveState { Position = new Vector3(0f, 8f + t.CapsuleHalfHeight, 0f), Grounded = false };
            var stream = new float[120];
            for (int i = 0; i < stream.Length; i++)
            {
                s = CharacterMovement.Step(s, Idle, Dt, Flat, t);
                stream[i] = s.LandingImpactSpeed;
            }
            return stream;
        }
        float[] a = Run(), b = Run();
        for (int i = 0; i < a.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(a[i]), BitConverter.SingleToInt32Bits(b[i]));
    }
}
