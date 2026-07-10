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
        [InlineData(nameof(DungeonConfig.CorridorMinWidth))]
        [InlineData(nameof(DungeonConfig.CorridorMaxWidth))]
        [InlineData(nameof(DungeonConfig.HallChancePercent))]
        [InlineData(nameof(DungeonConfig.HallMinLengthTiles))]
        public void Invalid_Throws_NamingProperty(string property)
        {
            var config = new DungeonConfig();
            switch (property)
            {
                case nameof(DungeonConfig.CellSizeMeters): config.CellSizeMeters = 0f; break;
                case nameof(DungeonConfig.RoomMinTiles): config.RoomMinTiles = 9; break; // > RoomMaxTiles (8)
                case nameof(DungeonConfig.PlotWidthTiles): config.PlotWidthTiles = 5; break; // < RoomMaxTiles + 2
                case nameof(DungeonConfig.CorridorMinWidth): config.CorridorMinWidth = 0; break; // < 1
                case nameof(DungeonConfig.CorridorMaxWidth): config.CorridorMinWidth = 4; config.CorridorMaxWidth = 3; break; // min > max
                case nameof(DungeonConfig.HallChancePercent): config.HallChancePercent = 101; break; // > 100
                case nameof(DungeonConfig.HallMinLengthTiles): config.HallChancePercent = 20; config.HallMinLengthTiles = 30; config.HallMaxLengthTiles = 16; break; // min > max
            }
            var ex = Assert.Throws<ArgumentException>(() => config.Validate());
            Assert.Contains(property, ex.Message);
        }

        [Fact]
        public void WideCorridorAndHall_Defaults_AreBackCompatNoOps()
        {
            var config = new DungeonConfig();
            Assert.Equal(1, config.CorridorMinWidth);
            Assert.Equal(1, config.CorridorMaxWidth);
            Assert.Equal(0, config.HallChancePercent);
        }

        [Fact]
        public void HallEnabled_PlotTooSmallForHall_Throws_NamingPlot()
        {
            // A plot that fits the largest room but not the largest hall is only rejected once halls are on.
            var config = new DungeonConfig
            {
                RoomMaxTiles = 8,
                PlotWidthTiles = 12,   // 8 + 2 <= 12, room-fit ok; 16 + 2 > 12, hall does not fit
                PlotDepthTiles = 12,
                HallMaxLengthTiles = 16,
            };
            config.Validate();   // halls off: valid

            config.HallChancePercent = 25;
            var ex = Assert.Throws<ArgumentException>(() => config.Validate());
            Assert.Contains(nameof(DungeonConfig.PlotWidthTiles), ex.Message);
        }
    }
}
