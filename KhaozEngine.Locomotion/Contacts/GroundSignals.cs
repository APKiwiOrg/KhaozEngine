using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion.Contacts;

/// <summary>Classifies a grounded move as a continuous stair run or an isolated step. These signals describe the
/// accepted move and never feed it.</summary>
internal static class GroundSignals
{
    internal const float RunGrade = 0.3f;
    internal const float SeedFraction = 0.7f;
    internal const float SmoothingRate = 5f;
    internal const float MaxDescentRate = 6f;
    internal const float MaxDescentGrade = 2f;
    internal const int Samples = 3;

    [ThreadStatic] static bool t_alwaysSample;

    /// <summary>Test only. Makes look-ahead on this thread sample even when ascent is already decided, so a test can
    /// compare the skip with the sampled path. Disposing the scope restores the skip.</summary>
    internal static SamplingScope AlwaysSample()
    {
        t_alwaysSample = true;
        return default;
    }

    /// <summary>Restores the look-ahead skip on this thread when the forced scope ends.</summary>
    internal readonly struct SamplingScope : IDisposable
    {
        public void Dispose() => t_alwaysSample = false;
    }

    /// <summary>Resolves the move's ground signals before a jump consumes its footing. Look-ahead uses the tick's
    /// support queries and lease only when a discrete step, a lift over carried lag or a carried run triggers it, and
    /// not when a carried ascent already continues on facts the move holds.
    /// Every climb rate decision reads only facts every head carries: the move's own steps and lag, the replicated
    /// rate and its average. The unreplicated <paramref name="stepRise"/> shapes only the returned step
    /// event.</summary>
    internal static (float ClimbRate, float Ewma, float StepDeltaY) Resolve(in MoveState input, float stepRise,
        float moveRise, bool riseSeen, bool dropSeen, float carriedLag, Vector2 groundTravel, Vector3 feet, in SupportSample axisSupport, bool walkableGround,
        float dt, in MoveTuning tuning, float footRadius, float cosMaxSlope, ContactQueryContext queries)
    {
        if (!walkableGround || !float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
            return (0f, 0f, 0f);
        float achieved = groundTravel.Length();
        if (!(achieved >= ShellMotion.ContactSkin)) achieved = 0f;
        bool liftedOverLag = carriedLag > 0f && moveRise > 0f;
        if (!(riseSeen || dropSeen || liftedOverLag || Ascending(input.ClimbRate) || Descending(input.ClimbRate) ||
                input.ClimbRateEwma > 0f))
            return (0f, 0f, stepRise);
        if (axisSupport.Status != SupportStatus.Walkable) return (0f, 0f, 0f);
        bool owedClimb = (double)axisSupport.Height - feet.Y >
            (double)ShellMotion.ContactSkin + axisSupport.HeightError;
        // A carried ascent that a seated rise, a lift over lag or owed climb continues is an ascent whatever the
        // window holds, and the ascent reads no sample, so the look-ahead Finds are skipped (SC-13).
        bool ascentDecided = Ascending(input.ClimbRate) && (riseSeen || liftedOverLag || owedClimb);
        if (ascentDecided && !t_alwaysSample) return Ascent(input, moveRise, dt, stepRise);

        Vector2 direction = achieved > 0f ? groundTravel / achieved : Vector2.Zero;
        var axis = new Vector2(feet.X, feet.Z);
        Span<Vector2> samples = stackalloc Vector2[Samples + 1];
        Span<SupportSample> supports = stackalloc SupportSample[Samples + 1];
        samples[0] = new Vector2(0f, axisSupport.Height);
        supports[0] = axisSupport;
        int count = 1;
        bool windowRise = false, windowDrop = false;
        // No travel empties the window, but still allows applied steps and a carried run to report.
        for (int k = 1; achieved > 0f && k <= Samples; k++)
        {
            float distance = k * tuning.CapsuleRadius;
            float reach = tuning.StepHeight + 2 * distance;
            Vector2 sampleAxis = axis + distance * direction;
            SupportSample support;
            try
            {
                support = queries.FindSupport(
                    new FootSupportQuery(sampleAxis, feet.Y, footRadius, reach, reach, cosMaxSlope));
            }
            // Optional presentation evidence cannot veto the accepted move.
            catch (ContactQueryFailure) { continue; }
            if (support.Status != SupportStatus.Walkable) continue;
            float step = GroundSeat.DiscreteStep(supports[count - 1], axis + samples[count - 1].X * direction,
                support, sampleAxis, cosMaxSlope);
            windowRise |= step > 0f;
            windowDrop |= step < 0f;
            samples[count] = new Vector2(distance, support.Height);
            supports[count++] = support;
        }

        // Fit against heights relative to the axis support, so a large world height does not erase the grade.
        double sumX = 0, sumY = 0, sumXx = 0, sumXy = 0;
        for (int i = 0; i < count; i++)
        {
            double x = samples[i].X, y = (double)samples[i].Y - samples[0].Y;
            sumX += x;
            sumY += y;
            sumXx += x * x;
            sumXy += x * y;
        }
        bool hasGrade = count >= 3;
        float grade = hasGrade ? (float)((count * sumXy - sumX * sumY) / (count * sumXx - sumX * sumX))
            : -RunGrade;
        bool ascent = hasGrade && grade >= RunGrade && windowRise;
        bool descent = hasGrade && grade <= -RunGrade && windowDrop;
        // The landing flattens the fitted grade before the last risers are paid. A carried run ends on step
        // evidence, while a new run needs both a grade and a discrete step in its direction.
        ascent |= ascentDecided || (Ascending(input.ClimbRate) && windowRise);
        descent |= Descending(input.ClimbRate) && (dropSeen || windowDrop);
        if (ascent) return Ascent(input, moveRise, dt, stepRise);
        // A descent without travel has no rate, so it reports as no run rather than a negative zero.
        float drop = descent ? MathF.Min(achieved / dt * Math.Clamp(-grade, RunGrade, MaxDescentGrade), MaxDescentRate)
            : 0f;
        return drop > 0f ? (-drop, 0f, 0f) : (0f, 0f, stepRise);
    }

    // The ascent reads only the move's rise, the tick and the carried average, never a look-ahead sample.
    static (float ClimbRate, float Ewma, float StepDeltaY) Ascent(in MoveState input, float moveRise, float dt,
        float stepRise)
    {
        float rate = moveRise / dt;
        float previous = input.ClimbRateEwma;
        float ewma = previous == 0f ? SeedFraction * rate
            : previous + (1f - MathF.Exp(-SmoothingRate * dt)) * (rate - previous);
        // An ascent the wire cannot show (below half a quantum) ends here with its EWMA, so no head carries a
        // run that a basis rebuilt from the wire would not.
        if (!(ewma > MoveState.ClimbRateQuantum / 2)) return (0f, 0f, stepRise);
        return (ewma, ewma, 0f);
    }

    /// <summary>Whether a carried climb rate shows an ascent run in progress. Every head carries the rate through its
    /// wire quantum, so only a rate the wire keeps nonzero counts.</summary>
    internal static bool Ascending(float climbRate) => climbRate > MoveState.ClimbRateQuantum / 2;

    /// <summary>Whether a carried climb rate shows a descent run in progress, the mirror of
    /// <see cref="Ascending"/>.</summary>
    internal static bool Descending(float climbRate) => climbRate < -MoveState.ClimbRateQuantum / 2;
}
