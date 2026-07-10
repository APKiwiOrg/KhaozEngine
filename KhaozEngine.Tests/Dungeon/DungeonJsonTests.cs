using System.Text.RegularExpressions;
using KhaozEngine.Content;
using KhaozEngine.Dungeon;
using Xunit;

namespace KhaozEngine.Tests.Dungeon
{
    public class DungeonJsonTests
    {
        static DungeonConfig Config() => new()
        {
            RoomCountTarget = 10,
            MaxFloors = 2,
            LockCount = 1,
            BossRoom = true,
            LoopEdgeBudget = 1,
        };

        [Fact]
        public void Config_RoundTrips()
        {
            DungeonConfig config = Config();
            string json = DungeonJson.SaveConfig(config);
            DungeonConfig loaded = DungeonJson.LoadConfig(json);

            Assert.Equal(config.CellSizeMeters, loaded.CellSizeMeters);
            Assert.Equal(config.FloorHeightMeters, loaded.FloorHeightMeters);
            Assert.Equal(config.RoomCountTarget, loaded.RoomCountTarget);
            Assert.Equal(config.RoomMinTiles, loaded.RoomMinTiles);
            Assert.Equal(config.RoomMaxTiles, loaded.RoomMaxTiles);
            Assert.Equal(config.MaxFloors, loaded.MaxFloors);
            Assert.Equal(config.PlotWidthTiles, loaded.PlotWidthTiles);
            Assert.Equal(config.PlotDepthTiles, loaded.PlotDepthTiles);
            Assert.Equal(config.CriticalPathTarget, loaded.CriticalPathTarget);
            Assert.Equal(config.LoopEdgeBudget, loaded.LoopEdgeBudget);
            Assert.Equal(config.LockCount, loaded.LockCount);
            Assert.Equal(config.BossRoom, loaded.BossRoom);
            Assert.Equal(config.SpawnMarkersPerRoomMax, loaded.SpawnMarkersPerRoomMax);
            Assert.Equal(config.LootMarkersPerRoomMax, loaded.LootMarkersPerRoomMax);

            // Same config always serializes to the same bytes: stable property order, invariant floats.
            Assert.Equal(json, DungeonJson.SaveConfig(loaded));
        }

        [Fact]
        public void Layout_RoundTrips_HashEqual()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 42UL);
            string json = DungeonJson.SaveLayout(layout);
            DungeonLayout loaded = DungeonJson.LoadLayout(json);

            Assert.Equal(layout.LayoutHash(), loaded.LayoutHash());

            // Re-saving the round-tripped layout produces byte-identical JSON.
            Assert.Equal(json, DungeonJson.SaveLayout(loaded));
        }

        [Fact]
        public void Layout_SchemaValidates()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 7UL);
            string json = DungeonJson.SaveLayout(layout);

            ValidationReport report = JsonSchemaValidator.Validate(json, DungeonSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void Config_SchemaValidates()
        {
            string json = DungeonJson.SaveConfig(Config());

            ValidationReport report = JsonSchemaValidator.Validate(json, DungeonSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));
        }

        [Fact]
        public void BadField_Throws_NamingField()
        {
            string json = DungeonJson.SaveConfig(new DungeonConfig())
                .Replace("\"roomCountTarget\": 12", "\"roomCountTarget\": -1");

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadConfig(json));
            Assert.Contains("roomCountTarget", ex.Message);
        }

        [Fact]
        public void BadLayoutDims_Throws_NamingField()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 1UL);
            string json = DungeonJson.SaveLayout(layout).Replace("\"width\": 64", "\"width\": 0");

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(json));
            Assert.Contains("width", ex.Message);
        }

        [Fact]
        public void BadLayoutRowLength_Throws_NamingField()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 1UL);
            string json = DungeonJson.SaveLayout(layout);
            string firstRow = FindFirstGridRow(json);
            string innerContent = firstRow.Substring(1, firstRow.Length - 2);
            string shortenedRow = "\"" + innerContent.Substring(0, innerContent.Length - 1) + "\"";
            string json2 = json.Replace(firstRow, shortenedRow);

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(json2));
            Assert.Contains("grid", ex.Message);
        }

        [Fact]
        public void BadLayoutRoomId_Throws_NamingField()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 1UL);
            string json = DungeonJson.SaveLayout(layout);
            string json2 = Regex.Replace(json, "\"roomA\":\\s*-?\\d+", "\"roomA\": 9999", RegexOptions.None, System.TimeSpan.FromSeconds(1));

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(json2));
            Assert.Contains("roomA", ex.Message);
        }

        // Finds the first quoted row string under the "grid" property (the opening bracket of the grid array
        // is immediately followed by nested arrays and strings, so the first quote encountered scanning
        // forward from it is always the first row's opening quote).
        static string FindFirstGridRow(string json)
        {
            int gridIndex = json.IndexOf("\"grid\"");
            int quoteStart = json.IndexOf('"', json.IndexOf('[', gridIndex) + 1);
            int quoteEnd = json.IndexOf('"', quoteStart + 1);
            return json.Substring(quoteStart, quoteEnd - quoteStart + 1);
        }
    }
}
