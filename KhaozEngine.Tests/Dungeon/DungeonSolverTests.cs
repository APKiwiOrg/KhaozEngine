using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonSolverTests
    {
        static DungeonConfig Config() => new() { RoomCountTarget = 10, MaxFloors = 1, LockCount = 0, BossRoom = false, LoopEdgeBudget = 0 };

        [Theory]
        [InlineData(1UL)]
        [InlineData(2UL)]
        [InlineData(3UL)]
        [InlineData(7UL)]
        [InlineData(42UL)]
        public void GeneratedLayout_Verifies(ulong seed)
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), seed);

            DungeonSolveReport report = DungeonSolver.Verify(layout);

            Assert.True(report.IsSolvable);
            Assert.Empty(report.Errors);
        }

        [Fact]
        public void CorruptedLayout_Fails()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 7UL);
            DungeonEdge edge = layout.Edges[0];
            DungeonTile doorTile = edge.Doors[0];

            // Poke the raster directly (InternalsVisibleTo) so one edge's door cell no longer matches the
            // expected DoorFrame/StairTop kind the solver requires, without touching the generator itself.
            int index = (doorTile.Floor * layout.Depth + doorTile.Z) * layout.Width + doorTile.X;
            layout.CellsMutable[index] = DungeonCellKind.Wall;

            DungeonSolveReport report = DungeonSolver.Verify(layout);

            Assert.False(report.IsSolvable);
            Assert.NotEmpty(report.Errors);
        }

        [Fact]
        public void Generate_SetsCriticalPathLength()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 7UL);

            Assert.True(layout.Rooms.Count >= 2);
            Assert.True(layout.Stats.CriticalPathLength > 0);
        }
    }
}
