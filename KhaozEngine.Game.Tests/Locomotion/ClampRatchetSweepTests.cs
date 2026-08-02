using System;
using System.Numerics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// THE ACCEPTANCE BAR FOR STEEP TERRAIN (#468): NO HEADING CLIMBS, AT ANY TICK RATE, ON ANY INPUT.
//
// The named cases in ClampRatchetTests.cs each ride ONE heading, chosen because the playtest or the climber-bot
// repro had reported it. That is how four rounds of this chain kept shipping: each round closed the headings it had
// been shown and left the rest of the circle unmeasured, and the next round found the hole somewhere else on it.
// 17.29.0's first cut is the sharpest example - it killed every heading the fixtures named and still left 44 of 360
// climbing at 120 Hz, the worst of them gaining 389 m in 20 seconds with no jump and no footing grant anywhere.
//
// So the bar is the whole circle, swept: 360 headings at one degree, four tick rates, walk and run, with and
// without the jump button held. 5760 rides and 4.9 million ticks. That is affordable because a Step against an
// analytic fixture is well under a microsecond, and it is the difference between "the cases we thought of pass" and
// "the face cannot be climbed".
//
// It is deliberately TWO measurements, because they fail in different ways:
//   - the NET after the ride, here, which catches a slow pump that no single tick would flag.
//   - the PER-TICK rise invariant, in Ride (ClampRatchetTests.cs) and therefore on every tick of every case in both
//     files. It catches per-tick theft that a net cannot see once the character has descended hundreds of metres.
// The round-four review found the pump with the first and the theft with the second, on a build whose entire
// aggregate suite was green.
public partial class ClampRatchetTests
{
    // How long each swept ride holds its heading, in seconds. The named cases run 80 s; the sweep runs a quarter of
    // that because it runs 5760 times, and 20 s is far past the point where a pump is unmistakable - the pre-fix
    // worst climber nets +389 m inside it, which is 389x the bound below.
    const float SweepSeconds = 20f;

    // The settle window excluded from the verdict, as a fraction of the ride. The character is seeded at rest ON the
    // face, so its first second is an arrival: the run's kinetic energy converts to a real ride up the fall line
    // (worth RunSpeed^2 / 2g = 2.88 m at the shipped tuning, and intended - see SlideTransient). The NET is measured
    // at the end of the ride, so that transient is long over; this constant exists to say plainly that the verdict is
    // a settled one and to keep the reported worst honest about which part of the ride it came from.
    const float SweepSettleFraction = 0.25f;

    [Theory]
    [InlineData(1f / 15f)]
    [InlineData(1f / 30f)]
    [InlineData(1f / 60f)]
    [InlineData(1f / 120f)]
    public void No_heading_on_the_creased_face_nets_altitude_at_any_tick_rate(float dt)
    {
        var t = Tuning;
        float worstNet = float.NegativeInfinity, worstPeak = 0f;
        int worstDeg = -1, climbers = 0, totalGrants = 0, totalJumps = 0;
        string worstInput = "";

        for (int deg = 0; deg < 360; deg++)
        {
            float radians = deg * MathF.PI / 180f;
            var heading = new Vector2(MathF.Cos(radians), MathF.Sin(radians));
            foreach ((bool run, bool jump) in Inputs)
            {
                (float peak, float final, int grants, int jumps) =
                    Ride(t, heading, run, jump, dt, SweepSeconds);
                totalGrants += grants;
                totalJumps += jumps;
                worstPeak = MathF.Max(worstPeak, peak);
                if (final > worstNet)
                {
                    worstNet = final;
                    worstDeg = deg;
                    worstInput = $"{(run ? "run" : "walk")}{(jump ? "+jump" : "")}";
                }
                if (final > SlideTransient) climbers++;
            }
        }

        string measured = $"{1f / dt:F0} Hz: worst net {worstNet:F3} m at {worstDeg} deg ({worstInput}), " +
                          $"worst peak {worstPeak:F3} m, climbers {climbers}/{360 * 4}, grants {totalGrants}, " +
                          $"jumps {totalJumps}";

        // ONE BOUND FOR BOTH MEASUREMENTS, and it is SlideTransient rather than a second number, because the thing
        // being bounded is the same thing: what a face may hand back out of the energy it was given. A ride that
        // ENDS above its start by more than the run's own kinetic energy could buy has taken altitude from
        // somewhere else, and the clamp is the only somewhere else there is. Measured after the fix, across all
        // four rates and all four inputs: every one of the 5760 rides ends between 641 and 895 metres BELOW its
        // start, so the bound is never within three orders of magnitude of binding.
        Assert.True(worstNet <= SlideTransient, $"a heading climbed the creased face. {measured}");
        Assert.Equal(0, climbers);

        // NO FOOTING ANYWHERE ON THE FACE, on any heading. The single-heading census in
        // The_open_face_grants_no_wedge_support makes this claim for one input; the sweep makes it for the circle.
        // A face is not a wedge from any direction, and a grant here would be a free launch for a held jump.
        Assert.Equal(0, totalGrants);
        Assert.Equal(0, totalJumps);

        // The ride has to have actually been a ride. Without this the whole sweep would pass trivially if the
        // character were ever seeded off the fixture or frozen in place, which is the failure mode a bound-only
        // acceptance test cannot distinguish from a pass.
        Assert.True(worstNet < -10f * SweepSeconds * SweepSettleFraction,
            $"the sweep did not slide: no heading descended meaningfully. {measured}");
    }

    // The four input combinations every heading is ridden with: the speed the character is asking for, and whether
    // the jump button is held. Holding jump matters because a wedge grant is invisible in Grounded once step 5
    // spends it (MoveState.SupportGranted is the signal Ride counts), so a held jump is how a footing bug on a face
    // converts into altitude.
    static (bool run, bool jump)[] Inputs =>
        [(false, false), (false, true), (true, false), (true, true)];
}
