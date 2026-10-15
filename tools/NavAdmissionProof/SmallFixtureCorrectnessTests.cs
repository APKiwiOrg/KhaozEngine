using System;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement.Research;

public sealed class SmallFixtureCorrectnessTests(ITestOutputHelper output)
{
    // Same lattice, central box, floor, capture settings and tuning as the hosted fixture.
    // Only the window is smaller and the two distant boxes are absent. No large fixture constructor.
    private static PhysicsNavBakeOptions SmallOptions => new(-1.5f, -1.5f, 1.5f, 1.5f, 0.25f,
        5, 8, ProofFixture.Tuning.MaxSlopeRadians, 144, 576, MaxSurfacesPerColumn: 4);

    [Fact]
    public void AcceptedRoofDoesNotInvalidateGroundObstruction()
    {
        using var fixture = new PublicBoxCapture(SmallOptions);
        GroundNavigation navigation = Build(fixture);
        GroundObstacleEvidence obstacle = GroundObstacleInspection.Inspect(navigation, fixture.Capture);
        output.WriteLine(JsonSerializer.Serialize(obstacle, ProofJson.Options));
        Assert.Equal(144, Assert.Single(navigation.Space.Layers).Width * navigation.Space.Layers[0].Height);
        Assert.Equal(0.125f, obstacle.Center.WorldX);
        Assert.Equal(0.125f, obstacle.Center.WorldZ);
        Assert.Contains(2f, obstacle.Center.CapturedHeights);
        GraphSurfaceEvidence roof = Assert.Single(obstacle.Center.Surfaces, surface => surface.Accepted);
        Assert.Equal(2f, roof.Height);
        Assert.True(obstacle.Passed);
        NavTraversalGraph graph = CanonicalProfileFile.Graph(navigation);
        Assert.All(obstacle.Sides, side =>
        {
            Assert.NotNull(side.GroundLayer);
            Assert.Contains(0f, side.Obstruction.CapturedHeights);
            Assert.Equal(1, Math.Abs(side.Approach.X - side.Obstruction.X) + Math.Abs(side.Approach.Z - side.Obstruction.Z));
            Assert.True(side.ClearGroundApproach);
            Assert.True(side.AdjacentGroundObstruction);
            Assert.True(side.GroundEntryRefused);
            Assert.All(side.Obstruction.Surfaces, surface => Assert.False(graph.CanTraverse(
                side.GroundLayer!.Value, side.Approach.X, side.Approach.Z,
                surface.Layer, side.Obstruction.X, side.Obstruction.Z)));
        });
        // Independent hand-checked positions show this is a capsule side collision at ground Y=0.
        GroundSideEvidence east = Assert.Single(obstacle.Sides, side => side.Side == "east");
        Assert.Equal(1.125f, east.Clear.WorldX);
        Assert.Equal(0.875f, east.Approach.WorldX);
        Assert.Equal(0.625f, east.Obstruction.WorldX);
        Assert.Equal(0f, Assert.Single(east.Approach.Surfaces, surface => surface.Accepted).Height);
    }

    [Fact]
    public void ClearGroundHasRealNeighborTraversal()
    {
        using var fixture = new PublicBoxCapture(SmallOptions, centralObstacle: false);
        GroundNavigation navigation = Build(fixture);
        NavTraversalGraph graph = CanonicalProfileFile.Graph(navigation);
        NavGrid grid = Assert.Single(navigation.Space.Layers);
        (int x, int z) = grid.CellOf(0, 0);
        Assert.Equal(0f, grid.SurfaceHeightAt(x, z));
        Assert.Equal(0f, grid.SurfaceHeightAt(x + 1, z));
        Assert.True(graph.CanTraverse(0, x, z, 0, x + 1, z));
        Assert.True(graph.CanTraverse(0, x + 1, z, 0, x, z));
    }

    [Fact]
    public void MissingObstacleCannotPassGroundAcceptance()
    {
        using var fixture = new PublicBoxCapture(SmallOptions, centralObstacle: false);
        GroundNavigation navigation = Build(fixture);
        GroundObstacleEvidence obstacle = GroundObstacleInspection.Inspect(navigation, fixture.Capture);
        output.WriteLine(JsonSerializer.Serialize(obstacle, ProofJson.Options));
        Assert.True(obstacle.ClearGroundApproaches);
        Assert.False(obstacle.AdjacentGroundObstructions);
        Assert.False(obstacle.GroundEntriesRefused);
        Assert.False(obstacle.Passed);
        Assert.All(obstacle.Sides, side =>
        {
            Assert.Contains(side.Obstruction.Surfaces, surface => surface.Accepted && surface.Height == 0f);
            Assert.False(side.GroundEntryRefused);
        });
        // Isolate obstacle acceptance from the hosted size thresholds, which stay unchanged.
        var acceptance = new GraphAcceptance(new(1, 10000, 1, 50000, 0, 0, true,
            obstacle.ClearGroundApproaches, obstacle.AdjacentGroundObstructions, obstacle.GroundEntriesRefused),
            [new(0, 164, 204)], obstacle);
        Assert.Throws<InvalidOperationException>(acceptance.RequireAccepted);
    }

    private static GroundNavigation Build(PublicBoxCapture fixture) => fixture.Capture.BuildProfile(
        ProofFixture.Tuning, default, GroundProfileOptions.Default, new NavBakeExecution(1));
}
