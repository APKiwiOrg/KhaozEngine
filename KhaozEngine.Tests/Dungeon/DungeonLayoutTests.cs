using System;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonLayoutTests
    {
        private static DungeonLayout CreateLayout(int width, int depth, int floors)
        {
            return new DungeonLayout(width, depth, floors, 2f, 4f)
            {
                Rooms = Array.Empty<DungeonRoom>(),
                Edges = Array.Empty<DungeonEdge>(),
                Keys = Array.Empty<DungeonKeyPlacement>(),
                Markers = Array.Empty<DungeonMarker>(),
                Stats = new LayoutStats()
            };
        }

        [Fact]
        public void GetCell_OutOfRange_ReturnsEmpty()
        {
            var layout = CreateLayout(4, 4, 1);
            layout.CellsMutable[0] = DungeonCellKind.RoomFloor;

            Assert.Equal(DungeonCellKind.Empty, layout.GetCell(-1, 0, 0));
            Assert.Equal(DungeonCellKind.Empty, layout.GetCell(0, -1, 0));
            Assert.Equal(DungeonCellKind.Empty, layout.GetCell(4, 0, 0));
            Assert.Equal(DungeonCellKind.Empty, layout.GetCell(0, 4, 0));
            Assert.Equal(DungeonCellKind.Empty, layout.GetCell(0, 0, 1));
            Assert.Equal(DungeonCellKind.Empty, layout.GetCell(0, 0, -1));
            Assert.Equal(DungeonCellKind.RoomFloor, layout.GetCell(0, 0, 0));
        }

        [Theory]
        [InlineData(DungeonCellKind.RoomFloor, true)]
        [InlineData(DungeonCellKind.Corridor, true)]
        [InlineData(DungeonCellKind.DoorFrame, true)]
        [InlineData(DungeonCellKind.StairLower, true)]
        [InlineData(DungeonCellKind.StairUpper, true)]
        [InlineData(DungeonCellKind.StairTop, true)]
        [InlineData(DungeonCellKind.Empty, false)]
        [InlineData(DungeonCellKind.Wall, false)]
        [InlineData(DungeonCellKind.StairVoid, false)]
        public void IsWalkable_MatchesExpectedKinds(DungeonCellKind kind, bool expected)
        {
            Assert.Equal(expected, DungeonLayout.IsWalkable(kind));
        }

        [Fact]
        public void IsWalkable_ExactlySixKindsWalkable()
        {
            int walkableCount = 0;
            foreach (DungeonCellKind kind in Enum.GetValues<DungeonCellKind>())
            {
                if (DungeonLayout.IsWalkable(kind))
                {
                    walkableCount++;
                }
            }

            Assert.Equal(6, walkableCount);
        }

        [Fact]
        public void LayoutHash_DiffersWhenOneCellChanges()
        {
            var a = CreateLayout(2, 2, 1);
            var b = CreateLayout(2, 2, 1);
            for (int i = 0; i < a.CellsMutable.Length; i++)
            {
                a.CellsMutable[i] = DungeonCellKind.RoomFloor;
                b.CellsMutable[i] = DungeonCellKind.RoomFloor;
            }

            b.CellsMutable[0] = DungeonCellKind.Wall;

            Assert.NotEqual(a.LayoutHash(), b.LayoutHash());
        }

        [Fact]
        public void LayoutHash_SameStateIsStable()
        {
            var a = CreateLayout(3, 2, 1);
            var b = CreateLayout(3, 2, 1);
            a.CellsMutable[2] = DungeonCellKind.Corridor;
            b.CellsMutable[2] = DungeonCellKind.Corridor;

            Assert.Equal(a.LayoutHash(), b.LayoutHash());
        }
    }
}
