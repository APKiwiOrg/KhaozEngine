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
        // Regression #1721 also applies to code-built configs and the generator entry point.
        [InlineData(nameof(DungeonConfig.CellSizeMeters), float.PositiveInfinity)]
        [InlineData(nameof(DungeonConfig.CellSizeMeters), float.NegativeInfinity)]
        [InlineData(nameof(DungeonConfig.CellSizeMeters), float.NaN)]
        [InlineData(nameof(DungeonConfig.FloorHeightMeters))]
        [InlineData(nameof(DungeonConfig.FloorHeightMeters), float.PositiveInfinity)]
        [InlineData(nameof(DungeonConfig.FloorHeightMeters), float.NegativeInfinity)]
        [InlineData(nameof(DungeonConfig.FloorHeightMeters), float.NaN)]
        [InlineData(nameof(DungeonConfig.RoomMinTiles))]
        [InlineData(nameof(DungeonConfig.PlotWidthTiles))]
        [InlineData(nameof(DungeonConfig.CorridorMinWidth))]
        [InlineData(nameof(DungeonConfig.CorridorMaxWidth))]
        [InlineData(nameof(DungeonConfig.HallChancePercent))]
        [InlineData(nameof(DungeonConfig.HallMinLengthTiles))]
        [InlineData(nameof(DungeonConfig.CeilingHeightMeters))]
        [InlineData(nameof(DungeonConfig.CeilingHeightMeters), float.PositiveInfinity)]
        [InlineData(nameof(DungeonConfig.CeilingHeightMeters), float.NaN)]
        public void Invalid_Throws_NamingProperty(string property, float size = 0f)
        {
            var config = new DungeonConfig();
            switch (property)
            {
                case nameof(DungeonConfig.CellSizeMeters): config.CellSizeMeters = size; break;
                case nameof(DungeonConfig.FloorHeightMeters): config.FloorHeightMeters = size; break;
                case nameof(DungeonConfig.RoomMinTiles): config.RoomMinTiles = 9; break; // > RoomMaxTiles (8)
                case nameof(DungeonConfig.PlotWidthTiles): config.PlotWidthTiles = 5; break; // < RoomMaxTiles + 2
                case nameof(DungeonConfig.CorridorMinWidth): config.CorridorMinWidth = 0; break; // < 1
                case nameof(DungeonConfig.CorridorMaxWidth): config.CorridorMinWidth = 4; config.CorridorMaxWidth = 3; break; // min > max
                case nameof(DungeonConfig.HallChancePercent): config.HallChancePercent = 101; break; // > 100
                case nameof(DungeonConfig.HallMinLengthTiles): config.HallChancePercent = 20; config.HallMinLengthTiles = 30; config.HallMaxLengthTiles = 16; break; // min > max
                case nameof(DungeonConfig.CeilingHeightMeters): config.CeilingHeightMeters = size; break; // <= 0 or nonfinite when set
            }
            var ex = Assert.Throws<ArgumentException>(() => config.Validate());
            // The min-over-max rule names the minimum, the first invalid property in check order.
            string expected = property == nameof(DungeonConfig.CorridorMaxWidth)
                ? nameof(DungeonConfig.CorridorMinWidth) : property;
            Assert.Equal(expected, ex.ParamName);

            if (property is nameof(DungeonConfig.CellSizeMeters) or nameof(DungeonConfig.FloorHeightMeters))
            {
                ArgumentException generatorError = Assert.Throws<ArgumentException>(() => DungeonGenerator.Generate(config, 1UL));
                Assert.Equal(property, generatorError.ParamName);

                if (property == nameof(DungeonConfig.CellSizeMeters))
                {
                    config.CellSizeMeters = float.MaxValue;
                }
                else
                {
                    config.FloorHeightMeters = float.MaxValue;
                }
                config.Validate();
                DungeonLayout control = DungeonGenerator.Generate(config, 1UL);
                Assert.Equal(float.MaxValue, property == nameof(DungeonConfig.CellSizeMeters)
                    ? control.CellSizeMeters : control.FloorHeightMeters);
            }
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
            Assert.Equal(nameof(DungeonConfig.PlotWidthTiles), ex.ParamName);
        }
    }
}
