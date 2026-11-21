using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>A region predicate that owns its inputs and can be called without a bound delegate.</summary>
internal abstract class NavGoalPredicate
{
    internal abstract bool Contains(Vector3 feetPosition);
}
