using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Serializes <see cref="MapTerrainOverrides"/> as
/// <c>{ "cellSize": n, "tiles": [ { "tileX": i, "tileZ": j, "deltas": [ ... ] } ] }</c>: only the touched
/// tiles, in deterministic (tileZ, tileX) order, each delta grid a flat row-major array. Reading rebuilds
/// the sparse tile map and validates every delta array's length, so a malformed block fails loudly at load
/// (the loud-fail stance for dev-authored content) rather than deserializing to garbage.</summary>
internal sealed class MapTerrainOverridesConverter : JsonConverter<MapTerrainOverrides>
{
    const int CellsPerTile = TerrainSculpt.TileSize * TerrainSculpt.TileSize;

    public override MapTerrainOverrides Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("terrainOverrides must be a JSON object.");

        float cellSize = MapTerrainOverrides.DefaultCellSize;
        var tiles = new System.Collections.Generic.List<(int TileX, int TileZ, float[] Deltas)>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("unexpected token in terrainOverrides.");
            string prop = reader.GetString()!;
            reader.Read();
            switch (prop)
            {
                case "cellSize":
                    cellSize = reader.GetSingle();
                    break;
                case "tiles":
                    ReadTiles(ref reader, tiles);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        var overrides = new MapTerrainOverrides(cellSize);
        foreach ((int tileX, int tileZ, float[] deltas) in tiles)
            overrides.PutTile(new MapSculptTile(tileX, tileZ, deltas));
        return overrides;
    }

    static void ReadTiles(ref Utf8JsonReader reader, System.Collections.Generic.List<(int, int, float[])> tiles)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("terrainOverrides.tiles must be an array.");

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("each terrainOverrides tile must be a JSON object.");

            int tileX = 0, tileZ = 0;
            float[]? deltas = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("unexpected token in a terrainOverrides tile.");
                string prop = reader.GetString()!;
                reader.Read();
                switch (prop)
                {
                    case "tileX": tileX = reader.GetInt32(); break;
                    case "tileZ": tileZ = reader.GetInt32(); break;
                    case "deltas": deltas = ReadDeltas(ref reader); break;
                    default: reader.Skip(); break;
                }
            }

            if (deltas is null)
                throw new JsonException($"terrainOverrides tile ({tileX}, {tileZ}) has no deltas array.");
            tiles.Add((tileX, tileZ, deltas));
        }
    }

    static float[] ReadDeltas(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("a terrainOverrides tile's deltas must be an array.");
        var deltas = new float[CellsPerTile];
        int count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;
            if (count >= CellsPerTile)
                throw new JsonException($"a terrainOverrides tile needs exactly {CellsPerTile} deltas.");
            deltas[count++] = reader.GetSingle();
        }
        if (count != CellsPerTile)
            throw new JsonException($"a terrainOverrides tile needs exactly {CellsPerTile} deltas, got {count}.");
        return deltas;
    }

    public override void Write(Utf8JsonWriter writer, MapTerrainOverrides value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartObject();
        writer.WriteNumber("cellSize", value.CellSize);
        writer.WriteStartArray("tiles");
        foreach (MapSculptTile tile in value.Tiles)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tileX", tile.TileX);
            writer.WriteNumber("tileZ", tile.TileZ);
            writer.WriteStartArray("deltas");
            float[] deltas = tile.Deltas;
            for (int i = 0; i < deltas.Length; i++)
                writer.WriteNumberValue(deltas[i]);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
