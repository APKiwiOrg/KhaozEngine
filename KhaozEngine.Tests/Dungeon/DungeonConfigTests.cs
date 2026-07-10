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

        [Fact]
        public void Defaults_AreOpenTop()
        {
            var config = new DungeonConfig();
            Assert.Equal(DungeonCeilingMode.Open, config.CeilingMode);
            Assert.Null(config.CeilingHeightMeters);
        }

        [Theory]
        [InlineData(nameof(DungeonConfig.CellSizeMeters))]
        [InlineData(nameof(DungeonConfig.RoomMinTiles))]
        [InlineData(nameof(DungeonConfig.PlotWidthTiles))]
        [InlineData(nameof(DungeonConfig.CeilingHeightMeters))]
        public void Invalid_Throws_NamingProperty(string property)
        {
            var config = new DungeonConfig();
            switch (property)
            {
                case nameof(DungeonConfig.CellSizeMeters): config.CellSizeMeters = 0f; break;
                case nameof(DungeonConfig.RoomMinTiles): config.RoomMinTiles = 9; break; // > RoomMaxTiles (8)
                case nameof(DungeonConfig.PlotWidthTiles): config.PlotWidthTiles = 5; break; // < RoomMaxTiles + 2
                case nameof(DungeonConfig.CeilingHeightMeters): config.CeilingHeightMeters = 0f; break; // <= 0 when set
            }
            var ex = Assert.Throws<ArgumentException>(() => config.Validate());
            Assert.Contains(property, ex.Message);
        }
    }
}
