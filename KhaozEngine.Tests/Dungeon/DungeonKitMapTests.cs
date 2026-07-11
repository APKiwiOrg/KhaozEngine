using System;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonKitMapTests
    {
        [Fact]
        public void Require_Unmapped_Throws_NamingPiece()
        {
            var map = new DungeonKitMap();

            var ex = Assert.Throws<InvalidOperationException>(() => map.Require(DungeonPiece.Wall));

            Assert.Contains(nameof(DungeonPiece.Wall), ex.Message);
        }

        [Fact]
        public void Greybox_MapsAllPieces()
        {
            var map = DungeonKitMap.Greybox();

            Assert.Equal("dungeon_floor", map.Require(DungeonPiece.Floor));
            Assert.Equal("dungeon_wall", map.Require(DungeonPiece.Wall));
            Assert.Equal("dungeon_doorframe", map.Require(DungeonPiece.DoorFrame));
            Assert.Equal("dungeon_stair", map.Require(DungeonPiece.StairUp));
            Assert.Equal("dungeon_landing", map.Require(DungeonPiece.StairDown));
            Assert.Equal("dungeon_ceiling", map.Require(DungeonPiece.Ceiling));
        }

        [Fact]
        public void Map_WhitespaceKitId_Throws_NamingParameter()
        {
            var map = new DungeonKitMap();

            var ex = Assert.Throws<ArgumentException>(() => map.Map(DungeonPiece.Floor, "   "));

            Assert.Equal("kitId", ex.ParamName);
        }

        [Fact]
        public void TileCenter_IdentityTransform()
        {
            var transform = new DungeonPlotTransform(0f, 0f, 0f, 0f);

            var a = transform.TileCenter(new DungeonTile(0, 0, 0), 2f, 4f);
            Assert.Equal(1f, a.X, 3);
            Assert.Equal(0f, a.Y, 3);
            Assert.Equal(1f, a.Z, 3);

            var b = transform.TileCenter(new DungeonTile(2, 3, 1), 2f, 4f);
            Assert.Equal(5f, b.X, 3);
            Assert.Equal(4f, b.Y, 3);
            Assert.Equal(7f, b.Z, 3);
        }

        [Fact]
        public void TileCenter_Yaw90()
        {
            var transform = new DungeonPlotTransform(0f, 0f, 0f, MathF.PI / 2f);

            var result = transform.TileCenter(new DungeonTile(0, 0, 0), 2f, 4f);

            Assert.Equal(-1f, result.X, 4);
            Assert.Equal(0f, result.Y, 4);
            Assert.Equal(1f, result.Z, 4);
        }
    }
}
