using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KhaozEngine.Serialization;

namespace KhaozEngine.Dungeon;

/// <summary>Thrown when dungeon config or layout JSON fails to parse, deserialize, or pass semantic
/// validation. <see cref="Reason"/> names the rule and the identifiers it documents name the offending member, so
/// tooling (the CLI, MCP servers) can report precisely what to fix. The message is developer prose naming the same
/// member in the JSON's own camelCase spelling, never a contract.</summary>
public sealed class DungeonJsonException : Exception
{
    /// <summary>Creates the exception with no <see cref="Reason"/>.</summary>
    public DungeonJsonException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with its reason.</summary>
    /// <param name="reason">The rule that refused the document.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    public DungeonJsonException(DungeonJsonRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates the exception with its reason and the reader's exception.</summary>
    /// <param name="reason">The rule that refused the document.</param>
    /// <param name="message">The developer-facing explanation, not a classification token.</param>
    /// <param name="innerException">The <see cref="JsonException"/> the reader or deserializer threw.</param>
    public DungeonJsonException(DungeonJsonRefusal reason, string message, Exception innerException)
        : base(message, innerException) => Reason = reason;

    /// <summary>The rule that refused the document. Null for an instance built through the message-only
    /// constructor, which code outside the engine may use and which the layout content checks (dimensions, ceiling
    /// height, grid, rooms, edges, keys and markers) still use.</summary>
    public DungeonJsonRefusal? Reason { get; }

    /// <summary>The document kind, <c>config</c> or <c>layout</c>, for <see cref="DungeonJsonRefusal.JsonMalformed"/>,
    /// <see cref="DungeonJsonRefusal.RootObjectRequired"/> and
    /// <see cref="DungeonJsonRefusal.JsonMemberUnconvertible"/>.</summary>
    public string? Section { get; init; }

    /// <summary>The refused member. For a config rule, the member in the JSON's own camelCase spelling with no root
    /// marker, such as <c>roomCountTarget</c>, where a rule over a pair names its minimum member. For
    /// <see cref="DungeonJsonRefusal.JsonMalformed"/> and <see cref="DungeonJsonRefusal.JsonMemberUnconvertible"/>,
    /// the reader's <see cref="JsonException.Path"/>, such as <c>$.roomCountTarget</c>, null when the reader reports
    /// none.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The zero-based line the reader stopped at, for <see cref="DungeonJsonRefusal.JsonMalformed"/>, null
    /// when the reader reports none.</summary>
    public long? LineNumber { get; init; }

    /// <summary>The zero-based UTF-8 byte position within that line, for
    /// <see cref="DungeonJsonRefusal.JsonMalformed"/>, null when the reader reports none.</summary>
    public long? BytePositionInLine { get; init; }

    /// <summary>The refused size, for <see cref="DungeonJsonRefusal.PositiveValueRequired"/>. The limit is zero,
    /// exclusive.</summary>
    public double? ActualNumber { get; init; }

    /// <summary>The refused integer, for <see cref="DungeonJsonRefusal.IntegerBelowMinimum"/>,
    /// <see cref="DungeonJsonRefusal.NonnegativeValueRequired"/>, <see cref="DungeonJsonRefusal.PercentageOutsideRange"/>,
    /// <see cref="DungeonJsonRefusal.RoomMarginInsufficient"/> and
    /// <see cref="DungeonJsonRefusal.HallMarginInsufficient"/>.</summary>
    public long? ActualInteger { get; init; }

    /// <summary>The inclusive minimum, for <see cref="DungeonJsonRefusal.IntegerBelowMinimum"/> (1),
    /// <see cref="DungeonJsonRefusal.NonnegativeValueRequired"/> (0), <see cref="DungeonJsonRefusal.PercentageOutsideRange"/>
    /// (0), <see cref="DungeonJsonRefusal.RoomMarginInsufficient"/> (<c>roomMaxTiles</c> + 2) and
    /// <see cref="DungeonJsonRefusal.HallMarginInsufficient"/> (<c>hallMaxLengthTiles</c> + 2).</summary>
    public long? MinimumInteger { get; init; }

    /// <summary>The inclusive maximum, for <see cref="DungeonJsonRefusal.PercentageOutsideRange"/> (100).</summary>
    public long? MaximumInteger { get; init; }

    /// <summary>The minimum of the pair, for <see cref="DungeonJsonRefusal.RangeUnordered"/>.</summary>
    public long? ActualMinimum { get; init; }

    /// <summary>The maximum of the pair it exceeds, for <see cref="DungeonJsonRefusal.RangeUnordered"/>.</summary>
    public long? ActualMaximum { get; init; }
}

/// <summary>
/// JSON round-trip for <see cref="DungeonConfig"/> and <see cref="DungeonLayout"/>. Follows the KhaozEngine.MapDoc
/// conventions: <c>System.Text.Json</c> with camelCase property names, JSONC-tolerant reads via
/// <see cref="Jsonc.ParseNode"/>, and a single embedded schema (<see cref="DungeonSchema"/>) covering both shapes.
///
/// <para><see cref="DungeonConfig"/> serializes directly (every property is a plain settable primitive), so
/// <see cref="SaveConfig"/>/<see cref="LoadConfig"/> round-trip it as-is. <see cref="DungeonLayout"/> needs a
/// private DTO: its raster is exposed only through <see cref="DungeonLayout.GetCell"/>, and the JSON encodes it
/// as per-floor arrays of per-row strings (one character per cell) rather than the raw enum array, per the
/// normative layout shape. Rooms, edges, keys, markers, and stats are plain settable types and serialize directly
/// once wrapped in the DTO.</para>
///
/// <para>Serialization is deterministic: property order follows type declaration order (a fixed, stable
/// reflection order for a type that never changes shape at runtime), and <c>System.Text.Json</c> writes numbers
/// in an invariant, culture-independent format. The same layout or config always produces byte-identical JSON.</para>
/// </summary>
public static class DungeonJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            NewLine = "\n",
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Serializes <paramref name="config"/> to JSON.</summary>
    public static string SaveConfig(DungeonConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, SerializerOptions);
    }

    /// <summary>Parses and deserializes a <see cref="DungeonConfig"/> from JSON (JSONC-tolerant: comments and
    /// trailing commas are accepted), then applies the <see cref="DungeonConfig.Validate"/> rules. Throws
    /// <see cref="DungeonJsonException"/> with its <see cref="DungeonJsonException.Reason"/> and the offending
    /// member on a parse, deserialization, or validation failure.</summary>
    public static DungeonConfig LoadConfig(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? node;
        try
        {
            node = Jsonc.ParseNode(json);
        }
        catch (JsonException ex)
        {
            throw new DungeonJsonException(DungeonJsonRefusal.JsonMalformed, $"config: invalid JSON. {ex.Message}", ex)
            {
                Section = "config",
                JsonPath = ex.Path,
                LineNumber = ex.LineNumber,
                BytePositionInLine = ex.BytePositionInLine,
            };
        }

        if (node is not JsonObject)
        {
            throw new DungeonJsonException(DungeonJsonRefusal.RootObjectRequired, "config: document root must be a JSON object.")
            {
                Section = "config",
            };
        }

        DungeonConfig config;
        try
        {
            // An internal invariant no input reaches: node is a JsonObject and the private options register no
            // converter for DungeonConfig, so the built-in object converter always constructs an instance. It
            // returns null only for a JSON null token.
            config = node.Deserialize<DungeonConfig>(SerializerOptions)
                ?? throw new InvalidOperationException("config: deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new DungeonJsonException(DungeonJsonRefusal.JsonMemberUnconvertible, $"config: {ex.Message}", ex)
            {
                Section = "config",
                JsonPath = ex.Path,
            };
        }

        // Validate's rules, read as a typed finding so the refusal carries the rule's reason and values. Every
        // finding names its property, so the field is always that property in camelCase.
        if (config.FirstFinding() is { } finding)
        {
            throw ConfigRefused(finding);
        }

        return config;
    }

    // The message is the camelCase field followed by the message of the ArgumentException Validate throws for the
    // same finding, parameter suffix included, as it was when this adapter caught that exception.
    private static DungeonJsonException ConfigRefused(DungeonConfigFinding finding)
    {
        string field = JsonNamingPolicy.CamelCase.ConvertName(finding.PropertyName);
        return new DungeonJsonException(finding.Reason, $"{field}: {finding.ToArgumentException().Message}")
        {
            JsonPath = field,
            ActualNumber = finding.ActualNumber,
            ActualInteger = finding.ActualInteger,
            MinimumInteger = finding.MinimumInteger,
            MaximumInteger = finding.MaximumInteger,
            ActualMinimum = finding.ActualMinimum,
            ActualMaximum = finding.ActualMaximum,
        };
    }

    /// <summary>Serializes <paramref name="layout"/> to JSON: the raster as per-floor arrays of per-row
    /// strings (one character per cell, see <see cref="CellToChar"/>), plus rooms/edges/keys/markers/stats as
    /// plain camelCase objects.</summary>
    public static string SaveLayout(DungeonLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var dto = new LayoutDto
        {
            CellSizeMeters = layout.CellSizeMeters,
            FloorHeightMeters = layout.FloorHeightMeters,
            CeilingMode = layout.CeilingMode,
            CeilingHeightMeters = layout.CeilingHeightMeters,
            Width = layout.Width,
            Depth = layout.Depth,
            Floors = layout.Floors,
            Grid = EncodeGrid(layout),
            Rooms = layout.Rooms.ToList(),
            Edges = layout.Edges.ToList(),
            Keys = layout.Keys.ToList(),
            Markers = layout.Markers.ToList(),
            Stats = layout.Stats,
        };

        return JsonSerializer.Serialize(dto, SerializerOptions);
    }

    /// <summary>Parses and deserializes a <see cref="DungeonLayout"/> from JSON (JSONC-tolerant), then applies
    /// semantic checks (dimensions positive, no null sections or array entries, grid string lengths matching
    /// the declared dimensions, and room/lock ids referenced by edges and keys actually existing) before
    /// rebuilding the layout. Throws <see cref="DungeonJsonException"/> naming the offending field on any
    /// failure. The rebuilt layout's <see cref="DungeonLayout.LayoutHash"/> equals the saved one's.</summary>
    public static DungeonLayout LoadLayout(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonNode? node;
        try
        {
            node = Jsonc.ParseNode(json);
        }
        catch (JsonException ex)
        {
            throw new DungeonJsonException(DungeonJsonRefusal.JsonMalformed, $"layout: invalid JSON. {ex.Message}", ex)
            {
                Section = "layout",
                JsonPath = ex.Path,
                LineNumber = ex.LineNumber,
                BytePositionInLine = ex.BytePositionInLine,
            };
        }

        if (node is not JsonObject)
        {
            throw new DungeonJsonException(DungeonJsonRefusal.RootObjectRequired, "layout: document root must be a JSON object.")
            {
                Section = "layout",
            };
        }

        LayoutDto dto;
        try
        {
            // An internal invariant no input reaches: node is a JsonObject and the private options register no
            // converter for the private LayoutDto, so the built-in object converter always constructs an instance.
            // It returns null only for a JSON null token.
            dto = node.Deserialize<LayoutDto>(SerializerOptions)
                ?? throw new InvalidOperationException("layout: deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new DungeonJsonException(DungeonJsonRefusal.JsonMemberUnconvertible, $"layout: {ex.Message}", ex)
            {
                Section = "layout",
                JsonPath = ex.Path,
            };
        }

        ValidateLayoutDto(dto);

        var layout = new DungeonLayout(dto.Width, dto.Depth, dto.Floors, dto.CellSizeMeters, dto.FloorHeightMeters)
        {
            CeilingMode = dto.CeilingMode,
            CeilingHeightMeters = dto.CeilingHeightMeters,
            Rooms = dto.Rooms,
            Edges = dto.Edges,
            Keys = dto.Keys,
            Markers = dto.Markers,
            Stats = dto.Stats,
        };

        DecodeGrid(layout, dto);

        return layout;
    }

    // The wire shape of a saved layout. DungeonLayout itself cannot be the deserialization target: its
    // constructor is internal (it allocates the raster) and its cell array is exposed only for writing
    // in-place, never as a settable property, so the grid must be decoded manually after construction.
    private sealed class LayoutDto
    {
        public float CellSizeMeters { get; set; }
        public float FloorHeightMeters { get; set; }
        public DungeonCeilingMode CeilingMode { get; set; } = DungeonCeilingMode.Open;
        public float CeilingHeightMeters { get; set; }
        public int Width { get; set; }
        public int Depth { get; set; }
        public int Floors { get; set; }
        public List<List<string>> Grid { get; set; } = new();
        public List<DungeonRoom> Rooms { get; set; } = new();
        public List<DungeonEdge> Edges { get; set; } = new();
        public List<DungeonKeyPlacement> Keys { get; set; } = new();
        public List<DungeonMarker> Markers { get; set; } = new();
        public LayoutStats Stats { get; set; } = new();
    }

    private static List<List<string>> EncodeGrid(DungeonLayout layout)
    {
        var grid = new List<List<string>>(layout.Floors);
        for (int floor = 0; floor < layout.Floors; floor++)
        {
            var rows = new List<string>(layout.Depth);
            for (int z = 0; z < layout.Depth; z++)
            {
                var row = new StringBuilder(layout.Width);
                for (int x = 0; x < layout.Width; x++)
                {
                    row.Append(CellToChar(layout.GetCell(x, z, floor)));
                }

                rows.Add(row.ToString());
            }

            grid.Add(rows);
        }

        return grid;
    }

    private static void DecodeGrid(DungeonLayout layout, LayoutDto dto)
    {
        for (int floor = 0; floor < dto.Floors; floor++)
        {
            List<string> rows = dto.Grid[floor];
            for (int z = 0; z < dto.Depth; z++)
            {
                string row = rows[z];
                for (int x = 0; x < dto.Width; x++)
                {
                    DungeonCellKind kind = CharToCell(row[x], floor, z, x);
                    layout.CellsMutable[(floor * dto.Depth + z) * dto.Width + x] = kind;
                }
            }
        }
    }

    private static char CellToChar(DungeonCellKind kind) => kind switch
    {
        DungeonCellKind.Empty => '.',
        DungeonCellKind.RoomFloor => 'R',
        DungeonCellKind.Corridor => 'C',
        DungeonCellKind.Wall => 'W',
        DungeonCellKind.DoorFrame => 'D',
        DungeonCellKind.StairLower => 'l',
        DungeonCellKind.StairMid => 'm',
        DungeonCellKind.StairUpper => 'u',
        DungeonCellKind.StairTop => 't',
        DungeonCellKind.StairVoid => 'v',
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown DungeonCellKind."),
    };

    private static DungeonCellKind CharToCell(char c, int floor, int row, int col) => c switch
    {
        '.' => DungeonCellKind.Empty,
        'R' => DungeonCellKind.RoomFloor,
        'C' => DungeonCellKind.Corridor,
        'W' => DungeonCellKind.Wall,
        'D' => DungeonCellKind.DoorFrame,
        'l' => DungeonCellKind.StairLower,
        'm' => DungeonCellKind.StairMid,
        'u' => DungeonCellKind.StairUpper,
        't' => DungeonCellKind.StairTop,
        'v' => DungeonCellKind.StairVoid,
        _ => throw new DungeonJsonException($"grid[{floor}][{row}][{col}]: unknown cell character '{c}'."),
    };

    // Every section (grid, rooms, edges, keys, markers, stats) and every entry inside its arrays is checked
    // for JSON null before use: the DTO declares them non-nullable, but System.Text.Json happily assigns
    // null from an explicit "grid": null or a null array element, and a corrupted document must always
    // surface as DungeonJsonException naming the field, never as a NullReferenceException.
    private static void ValidateLayoutDto(LayoutDto dto)
    {
        if (dto.Width <= 0)
        {
            throw new DungeonJsonException("width: must be greater than zero.");
        }

        if (dto.Depth <= 0)
        {
            throw new DungeonJsonException("depth: must be greater than zero.");
        }

        if (dto.Floors <= 0)
        {
            throw new DungeonJsonException("floors: must be greater than zero.");
        }

        if (dto.CellSizeMeters <= 0f)
        {
            throw new DungeonJsonException("cellSizeMeters: must be greater than zero.");
        }

        if (dto.FloorHeightMeters <= 0f)
        {
            throw new DungeonJsonException("floorHeightMeters: must be greater than zero.");
        }

        if (dto.CeilingHeightMeters < 0f || !float.IsFinite(dto.CeilingHeightMeters))
        {
            throw new DungeonJsonException("ceilingHeightMeters: must be non-negative and finite.");
        }

        if (dto.Grid is null)
        {
            throw new DungeonJsonException("grid: must not be null.");
        }

        if (dto.Rooms is null)
        {
            throw new DungeonJsonException("rooms: must not be null.");
        }

        if (dto.Edges is null)
        {
            throw new DungeonJsonException("edges: must not be null.");
        }

        if (dto.Keys is null)
        {
            throw new DungeonJsonException("keys: must not be null.");
        }

        if (dto.Markers is null)
        {
            throw new DungeonJsonException("markers: must not be null.");
        }

        if (dto.Stats is null)
        {
            throw new DungeonJsonException("stats: must not be null.");
        }

        if (dto.Grid.Count != dto.Floors)
        {
            throw new DungeonJsonException($"grid: expected {dto.Floors} floor(s), found {dto.Grid.Count}.");
        }

        for (int floor = 0; floor < dto.Grid.Count; floor++)
        {
            List<string> rows = dto.Grid[floor];
            if (rows is null)
            {
                throw new DungeonJsonException($"grid[{floor}]: must not be null.");
            }

            if (rows.Count != dto.Depth)
            {
                throw new DungeonJsonException($"grid[{floor}]: expected {dto.Depth} row(s), found {rows.Count}.");
            }

            for (int z = 0; z < rows.Count; z++)
            {
                if (rows[z] is null)
                {
                    throw new DungeonJsonException($"grid[{floor}][{z}]: must not be null.");
                }

                if (rows[z].Length != dto.Width)
                {
                    throw new DungeonJsonException(
                        $"grid[{floor}][{z}]: expected length {dto.Width}, found {rows[z].Length}.");
                }
            }
        }

        var roomIds = new HashSet<int>();
        for (int i = 0; i < dto.Rooms.Count; i++)
        {
            DungeonRoom room = dto.Rooms[i];
            if (room is null)
            {
                throw new DungeonJsonException($"rooms[{i}]: must not be null.");
            }

            if (!roomIds.Add(room.Id))
            {
                throw new DungeonJsonException($"rooms[{i}]: duplicate room id {room.Id}.");
            }
        }

        var lockIds = new HashSet<int>();
        for (int i = 0; i < dto.Edges.Count; i++)
        {
            DungeonEdge edge = dto.Edges[i];
            if (edge is null)
            {
                throw new DungeonJsonException($"edges[{i}]: must not be null.");
            }

            if (edge.Path is null)
            {
                throw new DungeonJsonException($"edges[{i}].path: must not be null.");
            }

            if (edge.Doors is null)
            {
                throw new DungeonJsonException($"edges[{i}].doors: must not be null.");
            }

            if (!roomIds.Contains(edge.RoomA))
            {
                throw new DungeonJsonException($"edges[{i}].roomA: room id {edge.RoomA} does not exist.");
            }

            if (!roomIds.Contains(edge.RoomB))
            {
                throw new DungeonJsonException($"edges[{i}].roomB: room id {edge.RoomB} does not exist.");
            }

            if (edge.LockId.HasValue)
            {
                lockIds.Add(edge.LockId.Value);
            }
        }

        for (int i = 0; i < dto.Keys.Count; i++)
        {
            DungeonKeyPlacement key = dto.Keys[i];
            if (key is null)
            {
                throw new DungeonJsonException($"keys[{i}]: must not be null.");
            }

            if (!roomIds.Contains(key.RoomId))
            {
                throw new DungeonJsonException($"keys[{i}].roomId: room id {key.RoomId} does not exist.");
            }

            if (!lockIds.Contains(key.LockId))
            {
                throw new DungeonJsonException($"keys[{i}].lockId: lock id {key.LockId} has no matching locked edge.");
            }
        }

        for (int i = 0; i < dto.Markers.Count; i++)
        {
            DungeonMarker marker = dto.Markers[i];
            if (marker is null)
            {
                throw new DungeonJsonException($"markers[{i}]: must not be null.");
            }

            if (marker.Tags is null)
            {
                throw new DungeonJsonException($"markers[{i}].tags: must not be null.");
            }
        }
    }
}
