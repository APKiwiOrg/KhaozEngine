using System;

namespace KhaozEngine.Locomotion;

public sealed partial class MovementQueryLease
{
    /// <summary>The water boundary reach certificate for one path in this lease's frame. It runs
    /// <see cref="TraceWater"/>'s own pre-checks in the same order: a usable lease, a ready selection, a valid query in
    /// this world and the skinned affine path inside the witness. It then calls the pin under the same
    /// authentication and catches. Every failure declines, so the full trace that follows reproduces today's refusal
    /// for it. A disposed lease or a foreign thread throws, as the trace does.</summary>
    internal MovementWaterReach CertifyWaterBoundary(in MovementMediumSweepQuery query)
    {
        AssertUsable();
        if (!_selectionReady || !query.IsValid || !SameWorld(query.Body.CurrentSpace) || !WaterPathInsideWitness(query) ||
            _pin is not IMovementBoundaryReachPin pin) return MovementWaterReach.Declined;
        try
        {
            AssertCurrent();
            MovementWaterReach reach = pin.CertifyBoundaryReach(query);
            AssertCurrent();
            return reach;
        }
        catch (ObjectDisposedException) when (_disposed) { throw; }
        catch (InvalidOperationException) { return MovementWaterReach.Declined; }
        catch (ArgumentException) { return MovementWaterReach.Declined; }
    }
}
