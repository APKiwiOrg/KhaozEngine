namespace KhaozEngine.Locomotion;

/// <summary>Visits a conservative superset of keys that AreAdjacent can accept. Returning false stops visitation.</summary>
public delegate bool MovementAnalyticSupportCandidateVisitor(MovementSurfaceFeatureKey key);

/// <summary>Optional source-topology acceleration. Known means every possible adjacent key was visited.
/// It never establishes adjacency. Callers still invoke AreAdjacent and may use an all-pairs fallback.</summary>
public interface IMovementAnalyticSupportCandidates
{
    MovementAvailability VisitAdjacentCandidates(MovementSurfaceFeatureKey key, MovementAnalyticSupportCandidateVisitor visitor);
}
