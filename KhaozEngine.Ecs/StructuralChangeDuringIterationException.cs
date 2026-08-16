using System;

namespace KhaozEngine.Ecs;

/// <summary>
/// Thrown when a structural change (<see cref="World.Spawn"/>, <see cref="World.Despawn"/>, or the archetype move
/// behind <see cref="World.Set{T}"/> / <see cref="World.Add{T}"/> / <see cref="World.Remove{T}"/>) is made directly
/// on a world while one of its queries is iterating it: from inside a <see cref="Query.ForEach{T1}(RefAction{T1})"/>
/// action, or from the body of a <see cref="Query.Entities"/> loop.
/// </summary>
/// <remarks>
/// Iteration walks each archetype's rows by index, and a structural change swap-removes rows underneath that walk:
/// one entity gets visited twice and another is silently skipped for the rest of the pass. A change that GROWS the
/// archetype is worse still, because the resize detaches the <c>ref</c> component parameters already handed to the
/// in-flight action, and every write the action makes to them afterwards lands in an array nothing will ever read
/// again. None of that throws on its own, which is why this exists.
/// <para>A call site has two ways out, and they are not interchangeable. The deferred path the ECS has always
/// documented records the change in <see cref="World.Commands"/> (or your own <see cref="EntityCommandBuffer"/>)
/// and plays it back after the loop, which <see cref="World.Update"/> already does after each system. That is the
/// right one whenever a one-frame delay is unobservable. When something reads the result later in the SAME frame,
/// a lazy component attach whose consumer submits in that frame being the usual case, defer nothing: materialize
/// the entities first (<c>Entities().ToList()</c>, or a buffer you own and reuse) and make the change after the
/// loop, where it is an ordinary out-of-iteration call.</para>
/// <para>Reading and writing COMPONENTS mid-iteration is not affected and stays legal: the ref parameters, and
/// <c>Has</c> / <c>Get</c> / <c>TryGet</c> / a <c>Set</c> that overwrites an already-present component, move no rows.
/// The parallel counterpart of this guard is <see cref="ParallelAccessViolationException"/>, which is stricter
/// because a parallel action must also stay off other entities entirely.</para>
/// </remarks>
public sealed class StructuralChangeDuringIterationException : InvalidOperationException
{
    /// <summary>The world call that made the change ("Spawn", "Despawn", "Set/Add" or "Remove").</summary>
    public string Operation { get; }

    public StructuralChangeDuringIterationException(string operation)
        : base($"'{operation}' was called on the world from inside a query iteration (a ForEach action, or the body " +
               "of an Entities() loop). Iteration walks archetype rows by index, so a structural change moves rows " +
               "out from under it and silently skips or double-visits entities. Two ways out: record the change in " +
               "World.Commands (or an EntityCommandBuffer) and play it back after the loop, when a one-frame delay " +
               "is fine, or materialize the entities first (Entities().ToList(), or a buffer you own) and make the " +
               "change after the loop, when it has to be visible later in the same frame.")
        => Operation = operation;
}
