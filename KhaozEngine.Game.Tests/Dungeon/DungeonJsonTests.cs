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

        // A saved layout with its ceilingHeightMeters replaced by a raw JSON number, so out-of-range literals such as
        // 1e1000 (which System.Text.Json reads as infinity) reach the semantic check.
        static string LayoutWithCeiling(string literal)
        {
            DungeonLayout layout = DungeonGenerator.Generate(Config(), 42UL);
            return Regex.Replace(DungeonJson.SaveLayout(layout),
                "\"ceilingHeightMeters\":\\s*[-+0-9.eE]+", "\"ceilingHeightMeters\": " + literal,
                RegexOptions.None, System.TimeSpan.FromSeconds(1));
        }

        // The guard checks negative before finite, so negative infinity is a negative value, not a nonfinite one.
        [Theory]
        [InlineData("-1", -1.0)]
        [InlineData("-1e1000", double.NegativeInfinity)]
        public void Layout_NegativeCeilingHeight_Throws_NamingField(string literal, double refused)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWithCeiling(literal)));

            Assert.Equal(DungeonJsonRefusal.NonnegativeValueRequired, ex.Reason);
            Assert.Equal("$.ceilingHeightMeters", ex.JsonPath);
            Assert.Equal(refused, ex.ActualNumber);
            Assert.Equal(0.0, ex.MinimumNumber);

            // The limit is zero, inclusive.
            Assert.Equal(0f, DungeonJson.LoadLayout(LayoutWithCeiling("0")).CeilingHeightMeters);
        }

        [Fact]
        public void Layout_NonFiniteCeilingHeight_Throws_NamingField()
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWithCeiling("1e1000")));

            Assert.Equal(DungeonJsonRefusal.NumberNonfinite, ex.Reason);
            Assert.Equal("$.ceilingHeightMeters", ex.JsonPath);
            Assert.Equal(double.PositiveInfinity, ex.ActualNumber);
            Assert.Null(ex.MinimumNumber);

            // The largest finite float loads.
            Assert.Equal(float.MaxValue, DungeonJson.LoadLayout(LayoutWithCeiling("3.4028235E+38")).CeilingHeightMeters);
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
        [InlineData("roomCountTarget", "$.roomCountTarget", 0, 1, DungeonJsonRefusal.IntegerBelowMinimum, 1, null, 0)]
        [InlineData("roomMinTiles", "$.roomMinTiles", 0, 1, DungeonJsonRefusal.IntegerBelowMinimum, 1, null, 0)]
        [InlineData("lockCount", "$.lockCount", -1, 0, DungeonJsonRefusal.IntegerBelowMinimum, 0, null, 0)]
        [InlineData("hallChancePercent", "$.hallChancePercent", 101, 100, DungeonJsonRefusal.PercentageOutsideRange, 0, 100, 0)]
        [InlineData("hallChancePercent", "$.hallChancePercent", -1, 0, DungeonJsonRefusal.PercentageOutsideRange, 0, 100, 0)]
        [InlineData("plotWidthTiles", "$.plotWidthTiles", 9, 10, DungeonJsonRefusal.RoomMarginInsufficient, 10, null, 0)]
        [InlineData("plotDepthTiles", "$.plotDepthTiles", 17, 18, DungeonJsonRefusal.HallMarginInsufficient, 18, null, 25)]
        public void BadField_Throws_NamingField(string member, string jsonPath, int refused, int limit, DungeonJsonRefusal reason,
            int minimum, int? maximum, int hallChancePercent)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadConfig(ConfigJson(("hallChancePercent", hallChancePercent), (member, refused))));

            Assert.Equal(reason, ex.Reason);
            Assert.Equal(jsonPath, ex.JsonPath);
            Assert.Equal(refused, ex.ActualInteger);
            Assert.Equal(minimum, ex.MinimumInteger);
            Assert.Equal(maximum, ex.MaximumInteger);
            Assert.Null(ex.Section);

            DungeonConfig loaded = DungeonJson.LoadConfig(ConfigJson(("hallChancePercent", hallChancePercent), (member, limit)));
            Assert.Equal(limit, JsonNode.Parse(DungeonJson.SaveConfig(loaded))![member]!.GetValue<int>());
        }

        [Theory]
        [InlineData("cellSizeMeters", "$.cellSizeMeters", 0f)]
        [InlineData("ceilingHeightMeters", "$.ceilingHeightMeters", -1f)]
        public void NonPositiveSize_Throws_NamingField(string member, string jsonPath, float refused)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadConfig(ConfigJson((member, refused))));

            Assert.Equal(DungeonJsonRefusal.PositiveValueRequired, ex.Reason);
            Assert.Equal(jsonPath, ex.JsonPath);
            Assert.Equal(refused, ex.ActualNumber);

            // The limit is zero, exclusive, so the smallest normal float loads.
            DungeonConfig loaded = DungeonJson.LoadConfig(ConfigJson((member, 1.17549435E-38f)));
            Assert.Equal(1.17549435E-38f, JsonNode.Parse(DungeonJson.SaveConfig(loaded))![member]!.GetValue<float>());
        }

        [Fact]
        public void MinimumOverMaximum_Throws_NamingTheMinimum()
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadConfig(ConfigJson(("roomMinTiles", 9), ("roomMaxTiles", 8))));

            Assert.Equal(DungeonJsonRefusal.RangeUnordered, ex.Reason);
            Assert.Equal("$.roomMinTiles", ex.JsonPath);
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
        [InlineData("config", "roomCountTarget", "$.roomCountTarget")]
        [InlineData("layout", "width", "$.width")]
        public void StringForAnInteger_Throws_JsonMemberUnconvertible_NamingThePath(string section, string member, string jsonPath)
        {
            JsonObject node = JsonNode.Parse(ValidDocument(section))!.AsObject();
            int value = node[member]!.GetValue<int>();
            node[member] = "many";

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => Load(section, node.ToJsonString()));

            Assert.Equal(DungeonJsonRefusal.JsonMemberUnconvertible, ex.Reason);
            Assert.Equal(section, ex.Section);
            Assert.Equal(jsonPath, ex.JsonPath);
            Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);

            node[member] = value;
            Load(section, node.ToJsonString());
        }

        static DungeonLayout ContentLayout() => DungeonGenerator.Generate(Config(), 1UL);

        // Walks a slash-separated pointer such as /edges/0/roomA into a parsed layout and sets the last step.
        static string LayoutWith(string pointer, JsonNode? value)
        {
            JsonNode root = JsonNode.Parse(DungeonJson.SaveLayout(ContentLayout()))!;
            string[] steps = pointer.TrimStart('/').Split('/');
            JsonNode parent = root;
            for (int i = 0; i < steps.Length - 1; i++)
            {
                parent = int.TryParse(steps[i], out int index) ? parent[index]! : parent[steps[i]]!;
            }

            string last = steps[^1];
            if (int.TryParse(last, out int lastIndex))
            {
                Assert.NotNull(parent[lastIndex]); // the fixture holds the entry the row replaces
                parent[lastIndex] = value;
            }
            else
            {
                Assert.NotNull(parent[last]);
                parent[last] = value;
            }

            return root.ToJsonString();
        }

        [Theory]
        [InlineData("cellSizeMeters", "$.cellSizeMeters")]
        [InlineData("floorHeightMeters", "$.floorHeightMeters")]
        public void BadLayoutDims_Throws_NamingField(string member, string jsonPath)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWith("/" + member, 0)));

            Assert.Equal(DungeonJsonRefusal.PositiveValueRequired, ex.Reason);
            Assert.Equal(jsonPath, ex.JsonPath);
            Assert.Equal(0.0, ex.ActualNumber);

            DungeonJson.LoadLayout(DungeonJson.SaveLayout(ContentLayout()));
        }

        [Theory]
        [InlineData("width", "$.width")]
        [InlineData("depth", "$.depth")]
        [InlineData("floors", "$.floors")]
        public void BadLayoutCounts_Throws_IntegerBelowMinimum(string member, string jsonPath)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWith("/" + member, 0)));

            Assert.Equal(DungeonJsonRefusal.IntegerBelowMinimum, ex.Reason);
            Assert.Equal(jsonPath, ex.JsonPath);
            Assert.Equal(0, ex.ActualInteger);
            Assert.Equal(1, ex.MinimumInteger);
            Assert.Null(ex.ActualNumber);

            var control = new DungeonLayout(member == "width" ? 1 : 2, member == "depth" ? 1 : 2,
                member == "floors" ? 1 : 2, 1f, 1f);
            DungeonJson.LoadLayout(DungeonJson.SaveLayout(control));
        }

        [Fact]
        public void BadLayoutRowLength_Throws_NamingField()
        {
            DungeonLayout layout = ContentLayout();
            int floor = layout.Floors - 1;
            JsonNode node = JsonNode.Parse(DungeonJson.SaveLayout(layout))!;
            string row = node["grid"]![floor]![1]!.GetValue<string>();
            node["grid"]![floor]![1] = row[..^1];

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(node.ToJsonString()));

            Assert.Equal(DungeonJsonRefusal.GridColumnCountMismatch, ex.Reason);
            Assert.Equal($"$.grid[{floor}][1]", ex.JsonPath);
            Assert.Equal(floor, ex.Floor);
            Assert.Equal(1, ex.Row);
            Assert.Equal(layout.Width - 1, ex.ActualCount);
            Assert.Equal(layout.Width, ex.ExpectedCount);

            node["grid"]![floor]![1] = row;
            DungeonJson.LoadLayout(node.ToJsonString());
        }

        [Fact]
        public void MissingGridRow_Throws_GridRowCountMismatch()
        {
            DungeonLayout layout = ContentLayout();
            int floor = layout.Floors - 1;
            JsonNode node = JsonNode.Parse(DungeonJson.SaveLayout(layout))!;
            JsonArray rows = node["grid"]![floor]!.AsArray();
            JsonNode lastRow = rows[^1]!;
            rows.RemoveAt(rows.Count - 1);

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(node.ToJsonString()));

            Assert.Equal(DungeonJsonRefusal.GridRowCountMismatch, ex.Reason);
            Assert.Equal($"$.grid[{floor}]", ex.JsonPath);
            Assert.Equal(floor, ex.Floor);
            Assert.Null(ex.Row);
            Assert.Equal(layout.Depth - 1, ex.ActualCount);
            Assert.Equal(layout.Depth, ex.ExpectedCount);

            rows.Add(lastRow);
            DungeonJson.LoadLayout(node.ToJsonString());
        }

        [Fact]
        public void ExtraGridFloor_Throws_GridFloorCountMismatch()
        {
            DungeonLayout layout = ContentLayout();
            JsonNode node = JsonNode.Parse(DungeonJson.SaveLayout(layout))!;
            JsonArray grid = node["grid"]!.AsArray();
            grid.Add(grid[0]!.DeepClone());

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(node.ToJsonString()));

            Assert.Equal(DungeonJsonRefusal.GridFloorCountMismatch, ex.Reason);
            Assert.Equal("$.grid", ex.JsonPath);
            Assert.Null(ex.Floor);
            Assert.Equal(layout.Floors + 1, ex.ActualCount);
            Assert.Equal(layout.Floors, ex.ExpectedCount);

            grid.RemoveAt(grid.Count - 1);
            DungeonJson.LoadLayout(node.ToJsonString());
        }

        [Theory]
        [InlineData("/edges/0/roomA", "$.edges[0].roomA")]
        [InlineData("/edges/0/roomB", "$.edges[0].roomB")]
        [InlineData("/keys/0/roomId", "$.keys[0].roomId")]
        public void BadLayoutRoomId_Throws_NamingField(string pointer, string jsonPath)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWith(pointer, 9999)));

            Assert.Equal(DungeonJsonRefusal.ReferencedRoomMissing, ex.Reason);
            Assert.Equal(jsonPath, ex.JsonPath);
            Assert.Equal(0, ex.Index);
            Assert.Equal(9999, ex.RoomId);

            DungeonJson.LoadLayout(DungeonJson.SaveLayout(ContentLayout()));
        }

        [Fact]
        public void UnmatchedKeyLock_Throws_ReferencedLockMissing()
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWith("/keys/0/lockId", 9999)));

            Assert.Equal(DungeonJsonRefusal.ReferencedLockMissing, ex.Reason);
            Assert.Equal("$.keys[0].lockId", ex.JsonPath);
            Assert.Equal(0, ex.Index);
            Assert.Equal(9999, ex.LockId);
            Assert.Null(ex.RoomId);

            DungeonJson.LoadLayout(DungeonJson.SaveLayout(ContentLayout()));
        }

        [Fact]
        public void RepeatedRoomId_Throws_RoomIdDuplicate()
        {
            DungeonLayout layout = ContentLayout();
            int firstId = layout.Rooms[0].Id;

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWith("/rooms/1/id", firstId)));

            Assert.Equal(DungeonJsonRefusal.RoomIdDuplicate, ex.Reason);
            Assert.Equal("$.rooms[1].id", ex.JsonPath);
            Assert.Equal(1, ex.Index);
            Assert.Equal(firstId, ex.RoomId);

            DungeonJson.LoadLayout(DungeonJson.SaveLayout(layout));
        }

        // Every null guard, one row each: the sections, a grid floor and row, each array's entries and each entry list.
        [Theory]
        [InlineData("/grid", "$.grid")]
        [InlineData("/rooms", "$.rooms")]
        [InlineData("/edges", "$.edges")]
        [InlineData("/keys", "$.keys")]
        [InlineData("/markers", "$.markers")]
        [InlineData("/stats", "$.stats")]
        [InlineData("/grid/0", "$.grid[0]")]
        [InlineData("/grid/0/0", "$.grid[0][0]")]
        [InlineData("/rooms/0", "$.rooms[0]")]
        [InlineData("/edges/0", "$.edges[0]")]
        [InlineData("/edges/0/path", "$.edges[0].path")]
        [InlineData("/edges/0/doors", "$.edges[0].doors")]
        [InlineData("/keys/0", "$.keys[0]")]
        [InlineData("/markers/0", "$.markers[0]")]
        [InlineData("/markers/0/tags", "$.markers[0].tags")]
        public void NullSectionOrEntry_Throws_NullValue_NamingThePath(string pointer, string jsonPath)
        {
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() =>
                DungeonJson.LoadLayout(LayoutWith(pointer, null)));

            Assert.Equal(DungeonJsonRefusal.NullValue, ex.Reason);
            Assert.Equal(jsonPath, ex.JsonPath);

            DungeonJson.LoadLayout(DungeonJson.SaveLayout(ContentLayout()));
        }

        [Fact]
        public void AbsentSection_LoadsEmpty_WhereANullOneIsRefused()
        {
            // An absent section is not a null one: it loads as empty and reaches the rule that needs its content.
            JsonObject node = JsonNode.Parse(DungeonJson.SaveLayout(ContentLayout()))!.AsObject();
            node.Remove("markers");
            Assert.Empty(DungeonJson.LoadLayout(node.ToJsonString()).Markers);

            node["markers"] = null;
            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(node.ToJsonString()));

            Assert.Equal(DungeonJsonRefusal.NullValue, ex.Reason);
            Assert.Equal("$.markers", ex.JsonPath);
        }

        [Fact]
        public void UnknownGridChar_Throws_NamingPosition()
        {
            DungeonLayout layout = ContentLayout();
            int floor = layout.Floors - 1;
            JsonNode node = JsonNode.Parse(DungeonJson.SaveLayout(layout))!;
            string row = node["grid"]![floor]![2]!.GetValue<string>();
            node["grid"]![floor]![2] = row[..3] + "X" + row[4..];

            DungeonJsonException ex = Assert.Throws<DungeonJsonException>(() => DungeonJson.LoadLayout(node.ToJsonString()));

            Assert.Equal(DungeonJsonRefusal.GridCharacterUnknown, ex.Reason);
            Assert.Equal($"$.grid[{floor}][2]", ex.JsonPath);
            Assert.Equal(floor, ex.Floor);
            Assert.Equal(2, ex.Row);
            Assert.Equal(3, ex.Column);
            Assert.Equal('X', ex.Character);

            node["grid"]![floor]![2] = row;
            DungeonJson.LoadLayout(node.ToJsonString());
        }
    }
}
