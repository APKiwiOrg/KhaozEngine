using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A thread-scoped switch to the uncached support evaluator. While it is active on a thread, every
/// support cost optimization is bypassed on that thread, so equivalence tests can compare an optimized result
/// with the reference result bit for bit. Nothing it bypasses is published or consumed.</summary>
internal static class SupportReferenceEvaluation
{
    [ThreadStatic] static bool t_active;

    /// <summary>Whether this thread evaluates support with the reference evaluator.</summary>
    internal static bool Active => t_active;

    /// <summary>Activates the reference evaluator on this thread until the returned scope is disposed.</summary>
    internal static Scope Begin()
    {
        bool prior = t_active;
        t_active = true;
        return new(prior);
    }

    /// <summary>Restores the switch value that was current when the scope began.</summary>
    internal readonly struct Scope(bool prior) : IDisposable
    {
        public void Dispose() => t_active = prior;
    }
}
