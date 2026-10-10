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

    /// <summary>Resolves the move's ground signals before a jump consumes its footing. Look-ahead uses the tick's
    /// support queries and lease only when a discrete step or a carried run triggers it.</summary>
    internal static (float ClimbRate, float Ewma, float StepDeltaY) Resolve(in MoveState input, float stepRise,
        float moveRise, Vector2 groundTravel, Vector3 feet, in SupportSample axisSupport, bool walkableGround,
        float dt, in MoveTuning tuning, float footRadius, float cosMaxSlope, Func<float, float, float>? groundHeight,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world, IPhysicsQueryLease? lease)
    {
        if (!walkableGround || !float.IsFinite(feet.X) || !float.IsFinite(feet.Y) || !float.IsFinite(feet.Z))
            return (0f, 0f, 0f);
        float achieved = groundTravel.Length();
        if (!(achieved >= ShellMotion.ContactSkin)) achieved = 0f;
        if (!(stepRise != 0f || input.ClimbRate != 0f || input.ClimbRateEwma > 0f))
            return (0f, 0f, stepRise);
        if (axisSupport.Status != SupportStatus.Walkable) return (0f, 0f, 0f);

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
            SupportSample support = FootSupport.Find(groundHeight, groundNormal, world, lease,
                new FootSupportQuery(sampleAxis, feet.Y, footRadius, reach, reach, cosMaxSlope));
            if (support.Status != SupportStatus.Walkable) continue;
            float step = GroundSeat.DiscreteStep(supports[count - 1], axis + samples[count - 1].X * direction,
                support, sampleAxis);
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
        bool owedClimb = (double)axisSupport.Height - feet.Y >
            (double)ShellMotion.ContactSkin + axisSupport.HeightError;
        bool ascent = hasGrade && grade >= RunGrade && windowRise;
        bool descent = hasGrade && grade <= -RunGrade && windowDrop;
        // The landing flattens the fitted grade before the last risers are paid. A carried run ends on step
        // evidence, while a new run needs both a grade and a discrete step in its direction.
        ascent |= input.ClimbRate > 0f && (stepRise > 0f || windowRise || owedClimb);
        descent |= input.ClimbRate < 0f && (stepRise < 0f || windowDrop);
        if (ascent)
        {
            float rate = moveRise / dt;
            float previous = input.ClimbRateEwma;
            float ewma = previous == 0f ? SeedFraction * rate
                : previous + (1f - MathF.Exp(-SmoothingRate * dt)) * (rate - previous);
            float climb = MathF.Max(0f, ewma);
            return (climb, ewma, climb != 0f ? 0f : stepRise);
        }
        // A descent without travel has no rate, so it reports as no run rather than a negative zero.
        float drop = descent ? MathF.Min(achieved / dt * Math.Clamp(-grade, RunGrade, MaxDescentGrade), MaxDescentRate)
            : 0f;
        return drop > 0f ? (-drop, 0f, 0f) : (0f, 0f, stepRise);
    }
}
