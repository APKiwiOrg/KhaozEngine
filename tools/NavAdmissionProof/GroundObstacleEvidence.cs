using System.Linq;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed record GraphSurfaceEvidence(int Layer, float? Height, bool Accepted);

internal sealed record ObstacleCellEvidence(int X, int Z, float WorldX, float WorldZ,
    float[] CapturedHeights, GraphSurfaceEvidence[] Surfaces);

internal sealed record GroundSideEvidence(string Side, ObstacleCellEvidence Clear,
    ObstacleCellEvidence Approach, ObstacleCellEvidence Obstruction, int? GroundLayer,
    bool ClearGroundApproach, bool AdjacentGroundObstruction, bool GroundEntryRefused);

internal sealed record GroundObstacleEvidence(ObstacleCellEvidence Center, GroundSideEvidence[] Sides)
{
    public bool ClearGroundApproaches => Sides.Length == 4 && Sides.All(side => side.ClearGroundApproach);
    public bool AdjacentGroundObstructions => Sides.Length == 4 && Sides.All(side => side.AdjacentGroundObstruction);
    public bool GroundEntriesRefused => Sides.Length == 4 && Sides.All(side => side.GroundEntryRefused);
    public bool Passed => ClearGroundApproaches && AdjacentGroundObstructions && GroundEntriesRefused;
}
