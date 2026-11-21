using KhaozEngine.Locomotion;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Exact body reach presented as a conservative, finite feet region for planning.</summary>
internal static class GroundPursuitGoal
{
    internal static NavGoalRegion Create(in MoveTuning tuning, in ReachTarget target, float range)
    {
        var reach = new ReachGoalSnapshot(target, range, tuning.CapsuleRadius, tuning.CapsuleHalfHeight);
        return reach.CreateRegion(ReachGoalSnapshot.AnchorFor(target));
    }
}
