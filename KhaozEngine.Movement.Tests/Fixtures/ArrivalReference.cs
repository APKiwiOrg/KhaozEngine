using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>The pre-1729 range stop, retained only in the test assembly.</summary>
internal static class ArrivalReference
{
    internal static Vector2 Stop(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, float dt, GroundMoveContext context, Vector2 command, in MoveState predicted,
        StepAdmission admits, out int previews)
    {
        previews = 0;
        if (!ReachGeometry.Within(RangeApproachCore.Body(predicted, tuning), target, range)) return command;
        float low = 0f, high = 1f;
        for (int iteration = 0; iteration < 32; iteration++)
        {
            float fraction = low + (high - low) * 0.5f;
            if (fraction == low || fraction == high) break;
            previews++;
            MoveState candidate = context.Step(body, command * fraction, run, dt, tuning);
            if (admits(body, candidate, tuning) && ReachGeometry.Within(RangeApproachCore.Body(candidate, tuning), target, range))
                high = fraction;
            else
                low = fraction;
        }
        return command * high;
    }
}
