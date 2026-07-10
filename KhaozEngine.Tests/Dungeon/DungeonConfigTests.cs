using System;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonConfigTests
    {
        [Fact]
        public void Defaults_Validate()
        {
            new DungeonConfig().Validate();
        }

        [Theory]
        [InlineData(nameof(DungeonConfig.CellSizeMeters))]
        [InlineData(nameof(DungeonConfig.RoomMinTiles))]
        [InlineData(nameof(DungeonConfig.PlotWidthTiles))]
        public void Invalid_Throws_NamingProperty(string property)
        {
            var config = new DungeonConfig();
            switch (property)
            {
                case nameof(DungeonConfig.CellSizeMeters): config.CellSizeMeters = 0f; break;
                case nameof(DungeonConfig.RoomMinTiles): config.RoomMinTiles = 9; break; // > RoomMaxTiles (8)
                case nameof(DungeonConfig.PlotWidthTiles): config.PlotWidthTiles = 5; break; // < RoomMaxTiles + 2
            }
            var ex = Assert.Throws<ArgumentException>(() => config.Validate());
            Assert.Contains(property, ex.Message);
        }
    }
}
