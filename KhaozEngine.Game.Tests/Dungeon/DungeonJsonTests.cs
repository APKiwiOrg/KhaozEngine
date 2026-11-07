using System.Text.Json.Nodes;
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
            Assert.Contains('\n', json);
            Assert.DoesNotContain('\r', json);
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
        public void Config_WithCorridorWidthAndHalls_RoundTrips_AndSchemaValidates()
        {
            DungeonConfig config = Config();
            config.CorridorMinWidth = 2;
            config.CorridorMaxWidth = 4;
            config.HallChancePercent = 30;
            config.HallMinLengthTiles = 9;
            config.HallMaxLengthTiles = 14;

            string json = DungeonJson.SaveConfig(config);

            ValidationReport report = JsonSchemaValidator.Validate(json, DungeonSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));

            DungeonConfig loaded = DungeonJson.LoadConfig(json);
            Assert.Equal(2, loaded.CorridorMinWidth);
            Assert.Equal(4, loaded.CorridorMaxWidth);
            Assert.Equal(30, loaded.HallChancePercent);
            Assert.Equal(9, loaded.HallMinLengthTiles);
            Assert.Equal(14, loaded.HallMaxLengthTiles);
        }

        [Fact]
        public void Layout_RoundTrips_HashEqual()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 42UL);
            string json = DungeonJson.SaveLayout(layout);
            Assert.Contains('\n', json);
            Assert.DoesNotContain('\r', json);
            DungeonLayout loaded = DungeonJson.LoadLayout(json);

            Assert.Equal(layout.LayoutHash(), loaded.LayoutHash());

            // Re-saving the round-tripped layout produces byte-identical JSON.
            Assert.Equal(json, DungeonJson.SaveLayout(loaded));
        }

        [Fact]
        public void Layout_RoundTrips_RoofedCeilingSettings()
        {
            DungeonConfig config = Config();
            config.CeilingMode = DungeonCeilingMode.Roofed;
            config.CeilingHeightMeters = 1.25f;
            DungeonLayout layout = DungeonGenerator.Generate(config, 42UL);

            DungeonLayout loaded = DungeonJson.LoadLayout(DungeonJson.SaveLayout(layout));

            Assert.Equal(DungeonCeilingMode.Roofed, loaded.CeilingMode);
            Assert.Equal(1.25f, loaded.CeilingHeightMeters);
        }

        [Fact]
        public void Layout_MissingCeilingFields_LoadsOpenDefaults()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 42UL);
            JsonNode node = JsonNode.Parse(DungeonJson.SaveLayout(layout))!;
            node.AsObject().Remove("ceilingMode");
            node.AsObject().Remove("ceilingHeightMeters");

            DungeonLayout loaded = DungeonJson.LoadLayout(node.ToJsonString());

            Assert.Equal(DungeonCeilingMode.Open, loaded.CeilingMode);
            Assert.Equal(0f, loaded.CeilingHeightMeters);
        }

        [Fact]
        public void Layout_NegativeCeilingHeight_Throws_NamingField()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 42UL);
            JsonNode node = JsonNode.Parse(DungeonJson.SaveLayout(layout))!;
            node["ceilingHeightMeters"] = -1f;

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(node.ToJsonString()));

            Assert.Contains("ceilingHeightMeters", ex.Message);
        }

        [Fact]
        public void Layout_NonFiniteCeilingHeight_Throws_NamingField()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 42UL);
            string json = Regex.Replace(DungeonJson.SaveLayout(layout),
                "\"ceilingHeightMeters\":\\s*[-+0-9.eE]+", "\"ceilingHeightMeters\": 1e1000",
                RegexOptions.None, System.TimeSpan.FromSeconds(1));

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(json));

            Assert.Contains("ceilingHeightMeters", ex.Message);
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
        public void PartialConfig_SchemaValidates_AndLoadsDefaults()
        {
            // Every DungeonConfig property has an engine-side default, so a hand-authored partial config
            // (the CLI/MCP use case) must both pass the schema and load with defaults filled in.
            string json = "{ \"roomCountTarget\": 20 }";

            ValidationReport report = JsonSchemaValidator.Validate(json, DungeonSchema.GetJson());
            Assert.True(report.IsValid, string.Join("\n", report.Errors));

            DungeonConfig defaults = new();
            DungeonConfig loaded = DungeonJson.LoadConfig(json);
            Assert.Equal(20, loaded.RoomCountTarget);
            Assert.Equal(defaults.CellSizeMeters, loaded.CellSizeMeters);
            Assert.Equal(defaults.MaxFloors, loaded.MaxFloors);
            Assert.Equal(defaults.PlotWidthTiles, loaded.PlotWidthTiles);
            Assert.Equal(defaults.LockCount, loaded.LockCount);
            Assert.Equal(defaults.BossRoom, loaded.BossRoom);
        }

        // The default config with each member replaced in turn, so a refused document breaks only the rule named.
        static string ConfigJson(params (string Member, JsonNode? Value)[] members)
        {
            JsonObject node = JsonNode.Parse(DungeonJson.SaveConfig(new DungeonConfig()))!.AsObject();
            foreach ((string member, JsonNode? value) in members)
            {
                node[member] = value;
            }

            return node.ToJsonString();
        }

        // Integer config rules: each refused value sits one past the documented limit, and the control loads at it.
        // hallChancePercent turns halls on for the hall margin row only.
        [Theory]
        [InlineData("roomCountTarget", -1, 1, DungeonJsonRefusal.IntegerBelowMinimum, 1, null, 0)]
        [InlineData("roomMinTiles", 0, 1, DungeonJsonRefusal.IntegerBelowMinimum, 1, null, 0)]
        [InlineData("lockCount", -1, 0, DungeonJsonRefusal.NonnegativeValueRequired, 0, null, 0)]
        [InlineData("hallChancePercent", 101, 100, DungeonJsonRefusal.PercentageOutsideRange, 0, 100, 0)]
        [InlineData("hallChancePercent", -1, 0, DungeonJsonRefusal.PercentageOutsideRange, 0, 100, 0)]
        [InlineData("plotWidthTiles", 9, 10, DungeonJsonRefusal.RoomMarginInsufficient, 10, null, 0)]
        [InlineData("plotDepthTiles", 17, 18, DungeonJsonRefusal.HallMarginInsufficient, 18, null, 25)]
        public void BadField_Throws_NamingField(string member, int refused, int limit, DungeonJsonRefusal reason,
            int minimum, int? maximum, int hallChancePercent)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadConfig(ConfigJson(("hallChancePercent", hallChancePercent), (member, refused))));

            Assert.Equal(reason, ex.Reason);
            Assert.Equal(member, ex.JsonPath);
            Assert.Equal(refused, ex.ActualInteger);
            Assert.Equal(minimum, ex.MinimumInteger);
            Assert.Equal(maximum, ex.MaximumInteger);
            Assert.Null(ex.Section);

            DungeonConfig loaded = DungeonJson.LoadConfig(ConfigJson(("hallChancePercent", hallChancePercent), (member, limit)));
            Assert.Equal(limit, JsonNode.Parse(DungeonJson.SaveConfig(loaded))![member]!.GetValue<int>());
        }

        [Theory]
        [InlineData("cellSizeMeters", 0f)]
        [InlineData("ceilingHeightMeters", -1f)]
        public void NonPositiveSize_Throws_NamingField(string member, float refused)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadConfig(ConfigJson((member, refused))));

            Assert.Equal(DungeonJsonRefusal.PositiveValueRequired, ex.Reason);
            Assert.Equal(member, ex.JsonPath);
            Assert.Equal(refused, ex.ActualNumber);

            // The limit is zero, exclusive, so the smallest positive float loads.
            DungeonConfig loaded = DungeonJson.LoadConfig(ConfigJson((member, float.Epsilon)));
            Assert.Equal(float.Epsilon, JsonNode.Parse(DungeonJson.SaveConfig(loaded))![member]!.GetValue<float>());
        }

        [Fact]
        public void MinimumOverMaximum_Throws_NamingTheMinimum()
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadConfig(ConfigJson(("roomMinTiles", 9), ("roomMaxTiles", 8))));

            Assert.Equal(DungeonJsonRefusal.RangeUnordered, ex.Reason);
            Assert.Equal("roomMinTiles", ex.JsonPath);
            Assert.Equal(9, ex.ActualMinimum);
            Assert.Equal(8, ex.ActualMaximum);

            Assert.Equal(8, DungeonJson.LoadConfig(ConfigJson(("roomMinTiles", 8), ("roomMaxTiles", 8))).RoomMinTiles);
        }

        // The two document kinds share their decoding rules, so each row runs one load path.
        static void Load(string section, string json)
        {
            if (section == "config")
            {
                DungeonJson.LoadConfig(json);
            }
            else
            {
                DungeonJson.LoadLayout(json);
            }
        }

        static string ValidDocument(string section) => section == "config"
            ? DungeonJson.SaveConfig(new DungeonConfig())
            : DungeonJson.SaveLayout(DungeonGenerator.Generate(Config(), 1UL));

        [Theory]
        [InlineData("config")]
        [InlineData("layout")]
        public void TruncatedDocument_Throws_JsonMalformed_WithTheReadersPosition(string section)
        {
            string valid = ValidDocument(section);
            string truncated = valid[..valid.LastIndexOf('}')];

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => Load(section, truncated));

            Assert.Equal(DungeonJsonRefusal.JsonMalformed, ex.Reason);
            Assert.Equal(section, ex.Section);
            System.Text.Json.JsonException inner = Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
            Assert.NotNull(ex.LineNumber);
            Assert.Equal(inner.LineNumber, ex.LineNumber);
            Assert.Equal(inner.BytePositionInLine, ex.BytePositionInLine);
            Assert.Equal(inner.Path, ex.JsonPath);

            Load(section, valid);
        }

        [Theory]
        [InlineData("config")]
        [InlineData("layout")]
        public void ArrayRoot_Throws_RootObjectRequired(string section)
        {
            string valid = ValidDocument(section);

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => Load(section, "[" + valid + "]"));

            Assert.Equal(DungeonJsonRefusal.RootObjectRequired, ex.Reason);
            Assert.Equal(section, ex.Section);
            Assert.Null(ex.JsonPath);

            Load(section, valid);
        }

        [Theory]
        [InlineData("config", "roomCountTarget")]
        [InlineData("layout", "width")]
        public void StringForAnInteger_Throws_JsonMemberUnconvertible_NamingThePath(string section, string member)
        {
            JsonObject node = JsonNode.Parse(ValidDocument(section))!.AsObject();
            int value = node[member]!.GetValue<int>();
            node[member] = "many";

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => Load(section, node.ToJsonString()));

            Assert.Equal(DungeonJsonRefusal.JsonMemberUnconvertible, ex.Reason);
            Assert.Equal(section, ex.Section);
            Assert.Equal("$." + member, ex.JsonPath);
            Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);

            node[member] = value;
            Load(section, node.ToJsonString());
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

        [Fact]
        public void NullGridRow_Throws_NamingField()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 1UL);
            string json = DungeonJson.SaveLayout(layout);
            string firstRow = FindFirstGridRow(json);
            string json2 = json.Replace(firstRow, "null");

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(json2));
            Assert.Contains("grid", ex.Message);
        }

        [Fact]
        public void NullRoomEntry_Throws_NamingField()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 1UL);
            JsonNode node = JsonNode.Parse(DungeonJson.SaveLayout(layout))!;
            node["rooms"]![0] = null;

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(node.ToJsonString()));
            Assert.Contains("rooms[0]", ex.Message);
        }

        [Fact]
        public void UnknownGridChar_Throws_NamingPosition()
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 1UL);
            string json = DungeonJson.SaveLayout(layout);
            string firstRow = FindFirstGridRow(json);
            string mutatedRow = "\"X" + firstRow.Substring(2);
            string json2 = json.Replace(firstRow, mutatedRow);

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(json2));
            Assert.Contains("grid[0][0][0]", ex.Message);
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
