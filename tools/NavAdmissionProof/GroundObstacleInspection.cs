using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Movement.Research;

internal static class GroundObstacleInspection
{
    private const float HeightTolerance = 0.001f;

    internal static GroundObstacleEvidence Inspect(GroundNavigation navigation, PhysicsNavBake capture)
    {
        NavTraversalGraph graph = CanonicalProfileFile.Graph(navigation);
        INavColumnProvider columns = CanonicalProfileFile.CapturedColumns(capture);
        if (navigation.Space.Layers.Count == 0)
            return new(new(0, 0, 0, 0, [], []), []);
        NavGrid grid = navigation.Space.Layers[0];
        Vector3 center = PublicBoxCapture.CentralCenter;
        Vector3 half = PublicBoxCapture.CentralHalfExtents;
        float radius = ProofFixture.Tuning.CapsuleRadius;
        float groundY = center.Y - half.Y;
        (int centerX, int centerZ) = grid.CellOf(center.X, center.Z);
        var sides = new GroundSideEvidence[4];
        (string Name, int Dx, int Dz)[] directions = [("west", -1, 0), ("east", 1, 0), ("south", 0, -1), ("north", 0, 1)];
        for (int i = 0; i < directions.Length; i++)
        {
            (string name, int dx, int dz) = directions[i];
            // Pick the first cell center outside the capsule-expanded box, then its two neighbors.
            (int x, int z) = grid.CellOf(center.X + dx * (half.X + radius), center.Z + dz * (half.Z + radius));
            Vector2 point = grid.CellCenter(x, z);
            if ((point.X - center.X) * dx + (point.Y - center.Z) * dz <=
                (dx != 0 ? half.X : half.Z) + radius)
            { x += dx; z += dz; }
            ObstacleCellEvidence approach = Cell(x, z);
            ObstacleCellEvidence clear = Cell(x + dx, z + dz);
            ObstacleCellEvidence target = Cell(x - dx, z - dz);
            GraphSurfaceEvidence? ground = approach.Surfaces.FirstOrDefault(surface =>
                surface.Accepted && AtHeight(surface.Height, groundY));
            bool clearApproach = ground is not null && clear.Surfaces.Any(surface =>
                surface.Layer == ground.Layer && surface.Accepted && AtHeight(surface.Height, groundY)) &&
                graph.CanTraverse(ground.Layer, clear.X, clear.Z, ground.Layer, approach.X, approach.Z);
            float sideDistance = (target.WorldX - center.X) * dx + (target.WorldZ - center.Z) * dz;
            float sideExtent = dx != 0 ? half.X : half.Z;
            bool adjacentGround = Math.Abs(target.X - approach.X) + Math.Abs(target.Z - approach.Z) == 1 &&
                grid.InBounds(target.X, target.Z) && grid.InBounds(approach.X, approach.Z) &&
                target.CapturedHeights.Any(height => AtHeight(height, groundY)) &&
                sideDistance > sideExtent && sideDistance < sideExtent + radius &&
                !target.Surfaces.Any(surface => surface.Accepted && AtHeight(surface.Height, groundY));
            // Query actual adjacent cells from a measured accepted ground surface, across every layer.
            // A missing source must fail this check, never supply a vacuous refusal.
            bool refused = ground is not null && grid.InBounds(target.X, target.Z) &&
                target.Surfaces.All(surface => !graph.CanTraverse(ground.Layer, approach.X, approach.Z,
                    surface.Layer, target.X, target.Z));
            sides[i] = new(name, clear, approach, target, ground?.Layer, clearApproach, adjacentGround, refused);
        }
        return new(Cell(centerX, centerZ), sides);

        ObstacleCellEvidence Cell(int x, int z)
        {
            Vector2 point = grid.CellCenter(x, z);
            var sampled = new NavSurfaceSample[4];
            int count = columns.SampleColumn(point.X, point.Y, sampled);
            var surfaces = new GraphSurfaceEvidence[graph.Layers.Count];
            for (int layer = 0; layer < surfaces.Length; layer++)
                surfaces[layer] = new(layer, navigation.Space.Layers[layer].SurfaceHeightAt(x, z),
                    graph.IsNodePassable(layer, x, z));
            return new(x, z, point.X, point.Y, sampled.Take(count).Select(sample => sample.Height).ToArray(), surfaces);
        }
    }

    private static bool AtHeight(float? actual, float expected) =>
        actual is float height && MathF.Abs(height - expected) <= HeightTolerance;
}
