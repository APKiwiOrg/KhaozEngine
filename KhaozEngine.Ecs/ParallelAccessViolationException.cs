using System;

namespace KhaozEngine.Ecs;

/// <summary>
/// Thrown by the debug hazard guard when a <see cref="World.ParallelForEach{T1}(RefAction{T1}, KhaozEngine.Simulation.IJobScheduler?)"/>
/// action calls back into the world (a structural change, or reading/writing a component through the world API)
/// instead of staying per-row-pure. Per-row-pure means: touch only the components handed to you by ref for the
/// current entity. To make structural changes from a parallel action, use the buffered overload (each worker gets
/// its own <see cref="EntityCommandBuffer"/>, merged deterministically at the join). Disable the guard for a
/// shipping hot loop via <see cref="World.ParallelHazardChecks"/>.
/// </summary>
public sealed class ParallelAccessViolationException : InvalidOperationException
{
    public ParallelAccessViolationException(string operation)
        : base($"'{operation}' was called on the world from inside a ParallelForEach action. Parallel actions must " +
               "be per-row-pure (touch only the ref components handed in for the current entity); record structural " +
               "changes via the buffered ParallelForEach overload's EntityCommandBuffer instead.")
    {
    }
}
