using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Terrain;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Streams and verifies one tile's canonical identity while retaining only its placement metadata.</summary>
internal static class MapSceneMetadataReader
{
    internal static IReadOnlyList<MapPlacement> Read(MapDocumentSource source, MapTileEntry entry, MapSceneScan scan)
    {
        scan.TileReads++;
        if (source.SceneDirectory is null)
        {
            if (!entry.Loaded) throw new MapSceneSourceException(MapSceneStatus.Incomplete, "Unloaded tile metadata cannot attest a reverse index.");
            return source.ScenePlacements(entry.Coord);
        }
        string path = MapTileFile.PathOf(source.SceneDirectory, entry.Coord, entry.Hash);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        scan.Scan(file.Length);
        using var tokens = new Tokens(file, scan.Limits.MaxMetadataBytes);
        var placements = new List<MapPlacement>();
        var spawns = new List<MapSpawn>();
        var players = new List<MapPlayerSpawn>();
        var sculpt = new List<Sculpt>();
        var sculptKeys = new HashSet<(int, int)>();
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tokens.Require(JsonTokenType.StartObject);
        while (tokens.Read() && tokens.Type != JsonTokenType.EndObject)
        {
            if (tokens.Type != JsonTokenType.PropertyName) throw new MapDocumentException("Invalid tile metadata member.");
            string name = tokens.String().ToLowerInvariant();
            if (!members.Add(name)) throw new MapDocumentException("Duplicate tile metadata member.");
            tokens.ReadRequired();
            if (name == "$schema")
            {
                if (tokens.Type is not (JsonTokenType.String or JsonTokenType.Null)) throw new MapDocumentException("Invalid tile schema annotation.");
                continue;
            }
            if (name is not ("placements" or "spawns" or "playerspawns" or "sculpt") || tokens.Type != JsonTokenType.StartArray)
                throw new MapDocumentException("Unknown tile metadata member or invalid array.");
            while (tokens.Read() && tokens.Type != JsonTokenType.EndArray)
            {
                if (tokens.Type != JsonTokenType.StartObject) throw new MapDocumentException("Invalid tile metadata row.");
                if (name == "sculpt")
                {
                    var value = ReadSculpt(tokens, source, entry);
                    if (!sculptKeys.Add((value.X, value.Z))) throw new MapDocumentException("Duplicate sculpt metadata coordinates.");
                    sculpt.Add(value);
                }
                else
                {
                    // Bound the transient row before its JSON buffer or DTO is allocated.
                    if (DemandDiagnostics.Check(scan, "MapSceneMetadataReader.MaxMetadataRows.1", "MaxMetadataRows", (decimal)placements.Count + spawns.Count + players.Count, 1, scan.Limits.MaxMetadataRows, (long)placements.Count + spawns.Count + players.Count >= scan.Limits.MaxMetadataRows, CapacityDemandLifetime.Inventory))
                        throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataRows), (long)placements.Count + spawns.Count + players.Count + 1, scan.Limits.MaxMetadataRows);
                    var options = MapDocumentFile.CreateOptions(source.SceneRegistry, write: false);
                    if (name == "placements") placements.Add(tokens.Value<MapPlacement>(options));
                    else if (name == "spawns") spawns.Add(tokens.Value<MapSpawn>(options));
                    else players.Add(tokens.Value<MapPlayerSpawn>(options));
                }
            }
        }
        if (tokens.Type != JsonTokenType.EndObject || tokens.Read()) throw new MapDocumentException("Invalid tile metadata ending.");
        MapTileValidator.Validate(new(entry.Coord, placements, spawns, players, Array.Empty<MapSculptTile>()), path, path,
            source.Tiles.TileSize, MapCanonical.SculptCellSizeOf(source.Manifest));
        var compact = MapDocumentFile.CreateCompactOptions(source.SceneRegistry);
        string digest = MapCanonical.HashHex(w =>
        {
            w.WriteStartObject();
            Write(w, "placements", placements.OrderBy(p => p.Id, StringComparer.Ordinal), compact);
            Write(w, "spawns", spawns.OrderBy(p => p.Id, StringComparer.Ordinal), compact);
            Write(w, "playerSpawns", players.OrderBy(p => p.Id, StringComparer.Ordinal), compact);
            w.WriteStartArray("sculpt");
            foreach (var row in sculpt.OrderBy(s => s.Z).ThenBy(s => s.X))
            {
                w.WriteStartObject(); w.WriteNumber("tileX", row.X); w.WriteNumber("tileZ", row.Z); w.WriteStartArray("deltas");
                // Canonical sorting needs a seek back to this array, never retention of the sculpt values.
                scan.Scan(row.Bytes);
                tokens.Seek(row.Offset);
                tokens.Require(JsonTokenType.StartArray);
                while (tokens.Read() && tokens.Type != JsonTokenType.EndArray) w.WriteNumberValue(tokens.Single());
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        });
        if (digest != entry.Hash) throw new MapDocumentException("Tile metadata canonical hash differs from its pinned manifest.");
        return Array.AsReadOnly(placements.ToArray());
    }

    internal static IReadOnlyList<MapSurfaceIndexEntry> ReadSurfaces(IMapSurfaceSource source, MapSceneScan scan)
    {
        IEnumerable<MapSurfaceIndexEntry> entries;
        if (source is MapStoredSurfaceSource stored)
        {
            foreach (var directory in stored.Index.Directory)
            {
                string path = MapSurfaceStorageLayout.PathOf(stored.SceneDirectory, 'd', directory.Sha256);
                long bytes = new FileInfo(path).Length;
                scan.Scratch(checked(bytes * 4)); scan.Scan(bytes);
                var status = stored.Directory(directory, new MapPageBudget(1));
                if (status != MapPatchStatus.Present) throw new MapSceneSourceException(status == MapPatchStatus.Missing ? MapSceneStatus.Missing : MapSceneStatus.Corrupt, "Unavailable scene surface directory metadata.");
                foreach (var page in stored.Index.DirectoryPages[directory.Sha256])
                {
                    bytes = new FileInfo(MapSurfaceStorageLayout.PathOf(stored.SceneDirectory, 'i', page.Sha256)).Length;
                    scan.Scratch(checked(bytes * 4)); scan.Scan(bytes);
                    status = stored.Page(directory, page, new MapPageBudget(1));
                    if (status != MapPatchStatus.Present) throw new MapSceneSourceException(status == MapPatchStatus.Missing ? MapSceneStatus.Missing : MapSceneStatus.Corrupt, "Unavailable scene surface index metadata.");
                }
            }
            entries = stored.Index.ByKey.Values;
        }
        else if (source is MapDocumentSurfaceSource resident)
        {
            var index = resident.SceneMetadataIndex;
            if (index.ReadDirectoryPages.Count != index.Directory.Count || index.UnreadIndexPages != 0)
                throw new MapSceneSourceException(MapSceneStatus.Incomplete, "Unloaded surface metadata cannot attest a reverse index.");
            entries = index.ByKey.Values;
        }
        else throw new MapSceneSourceException(MapSceneStatus.Invalid, "Unknown native scene metadata producer.");
        var result = new List<MapSurfaceIndexEntry>();
        foreach (var entry in entries)
        {
            long bytes = checked(128 + entry.Dependencies.Count * 32L + entry.IncidentRecords.Count * 32L);
            foreach (string id in entry.RecordIds.Concat(entry.SpaceIds)) bytes = checked(bytes + System.Text.Encoding.UTF8.GetByteCount(id));
            scan.Retain(bytes, checked(1 + entry.Dependencies.Count + entry.IncidentRecords.Count));
            result.Add(entry with
            {
                Dependencies = Array.AsReadOnly(entry.Dependencies.ToArray()),
                RecordIds = Array.AsReadOnly(entry.RecordIds.ToArray()),
                IncidentRecords = Array.AsReadOnly(entry.IncidentRecords.ToArray()),
                SpaceIds = Array.AsReadOnly(entry.SpaceIds.ToArray())
            });
        }
        return Array.AsReadOnly(result.ToArray());
    }

    static void Write<T>(Utf8JsonWriter writer, string name, IEnumerable<T> rows, JsonSerializerOptions options)
    {
        writer.WriteStartArray(name);
        foreach (var row in rows) JsonSerializer.Serialize(writer, row, options);
        writer.WriteEndArray();
    }

    sealed record Sculpt(int X, int Z, long Offset, long Bytes);
    static Sculpt ReadSculpt(Tokens tokens, MapDocumentSource source, MapTileEntry entry)
    {
        int x = 0, z = 0;
        long offset = -1, bytes = 0;
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (tokens.Read() && tokens.Type != JsonTokenType.EndObject)
        {
            if (tokens.Type != JsonTokenType.PropertyName) throw new MapDocumentException("Invalid sculpt metadata.");
            string name = tokens.String().ToLowerInvariant();
            if (!members.Add(name)) throw new MapDocumentException("Duplicate sculpt metadata.");
            tokens.ReadRequired();
            if (name == "tilex") x = tokens.Int32();
            else if (name == "tilez") z = tokens.Int32();
            else if (name == "deltas")
            {
                if (tokens.Type != JsonTokenType.StartArray) throw new MapDocumentException("Invalid sculpt deltas.");
                offset = tokens.TokenOffset;
                int count = 0;
                while (tokens.Read() && tokens.Type != JsonTokenType.EndArray)
                {
                    if (!float.IsFinite(tokens.Single()) || ++count > TerrainSculpt.TileSize * TerrainSculpt.TileSize)
                        throw new MapDocumentException("Invalid sculpt delta.");
                }
                if (count != TerrainSculpt.TileSize * TerrainSculpt.TileSize) throw new MapDocumentException("Invalid sculpt delta count.");
                bytes = tokens.Position - offset;
            }
            else throw new MapDocumentException("Unknown sculpt metadata.");
        }
        if (offset < 0 || MapTileGrid.OwnerOfSculptTile(x, z,
            MapCanonical.SculptCellSizeOf(source.Manifest), source.Tiles.TileSize) != entry.Coord)
            throw new MapDocumentException("Invalid sculpt owner metadata.");
        return new(x, z, offset, bytes);
    }

    /// <summary>A bounded refill reader. Skipping still validates and consumes every token and every input byte.</summary>
    sealed class Tokens : IDisposable
    {
        readonly Stream _stream;
        readonly int _maxToken;
        byte[] _buffer;
        int _used, _available, _start, _length;
        long _base;
        bool _final;
        JsonReaderState _state;
        static JsonReaderState Initial => new(new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        internal Tokens(Stream stream, long maxToken)
        {
            _stream = stream; _maxToken = (int)Math.Min(Math.Max(1, maxToken), int.MaxValue);
            _buffer = new byte[Math.Min(16384, _maxToken)]; _state = Initial;
        }
        internal JsonTokenType Type { get; private set; }
        internal long Position => _base + _used;
        internal long TokenOffset => _base + _start;
        internal void Seek(long offset) { _stream.Position = offset; _base = offset; _used = _available = 0; _final = false; _state = Initial; }
        internal bool Read()
        {
            while (true)
            {
                var reader = new Utf8JsonReader(_buffer.AsSpan(_used, _available - _used), _final, _state);
                if (reader.Read())
                {
                    _start = checked(_used + (int)reader.TokenStartIndex);
                    _length = checked((int)(reader.BytesConsumed - reader.TokenStartIndex));
                    _used = checked(_used + (int)reader.BytesConsumed); _state = reader.CurrentState; Type = reader.TokenType;
                    return true;
                }
                _used += (int)reader.BytesConsumed; _state = reader.CurrentState;
                if (_final) return false;
                int left = _available - _used;
                if (left == _buffer.Length)
                {
                    if (DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxMetadataBytes.1", "MaxMetadataBytes", _buffer.Length, 1, _maxToken, _buffer.Length >= _maxToken, CapacityDemandLifetime.Transient)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataBytes), (long)_buffer.Length + 1, _maxToken);
                    Array.Resize(ref _buffer, (int)Math.Min(_maxToken, (long)_buffer.Length * 2));
                }
                _buffer.AsSpan(_used, left).CopyTo(_buffer); _base += _used; _used = 0; _available = left;
                int read = _stream.Read(_buffer, left, _buffer.Length - left); _available += read; _final = read == 0;
                if (_base == 0 && _available >= 3 && _buffer[0] == 239 && _buffer[1] == 187 && _buffer[2] == 191) _used = 3;
            }
        }
        Utf8JsonReader Scalar()
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _length), true, default);
            reader.Read(); return reader;
        }
        internal string String() { var reader = Scalar(); return reader.GetString()!; }
        internal int Int32()
        {
            var reader = Scalar();
            return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value)
                ? value : throw new MapDocumentException("Invalid sculpt integer.");
        }
        internal float Single()
        {
            var reader = Scalar();
            return reader.TokenType == JsonTokenType.Number && reader.TryGetSingle(out float value) && float.IsFinite(value)
                ? value : throw new MapDocumentException("Invalid sculpt delta.");
        }
        internal void ReadRequired() { if (!Read()) throw new MapDocumentException("Truncated tile metadata."); }
        internal void Require(JsonTokenType type) { ReadRequired(); if (Type != type) throw new MapDocumentException("Unexpected tile metadata token."); }
        internal void SkipValue()
        {
            if (Type is not (JsonTokenType.StartObject or JsonTokenType.StartArray)) return;
            int depth = 1;
            while (depth != 0) { ReadRequired(); if (Type is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++; else if (Type is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--; }
        }
        internal T Value<T>(JsonSerializerOptions options)
        {
            using var stream = new LimitedRow(_maxToken);
            using (var writer = new Utf8JsonWriter(stream, MapJsonTextOptions.IndentedWriter with { Indented = false }))
            {
                int depth = 0;
                var members = new Stack<HashSet<string>>();
                do
                {
                    switch (Type)
                    {
                        case JsonTokenType.StartObject: writer.WriteStartObject(); depth++; members.Push(new(StringComparer.OrdinalIgnoreCase)); break;
                        case JsonTokenType.EndObject: writer.WriteEndObject(); depth--; members.Pop(); break;
                        case JsonTokenType.StartArray: writer.WriteStartArray(); depth++; break;
                        case JsonTokenType.EndArray: writer.WriteEndArray(); depth--; break;
                        case JsonTokenType.PropertyName:
                            string name = String();
                            if (!members.Peek().Add(name)) throw new MapDocumentException("Duplicate tile row member.");
                            writer.WritePropertyName(name); break;
                        case JsonTokenType.String: writer.WriteStringValue(String()); break;
                        default: writer.WriteRawValue(_buffer.AsSpan(_start, _length)); break;
                    }
                    if (depth != 0) ReadRequired();
                } while (depth != 0);
            }
            return JsonSerializer.Deserialize<T>(stream.GetBuffer().AsSpan(0, (int)stream.Length), options) ?? throw new MapDocumentException("Null tile metadata row.");
        }
        public void Dispose() { }
    }

    sealed class LimitedRow(int limit) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) { if (DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxMetadataBytes.2", "MaxMetadataBytes", Length, buffer.Length, limit, buffer.Length > limit - Length, CapacityDemandLifetime.Transient)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataBytes), Length + buffer.Length, limit); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { if (DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxMetadataBytes.3", "MaxMetadataBytes", Length, count, limit, count > limit - Length, CapacityDemandLifetime.Transient)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataBytes), Length + count, limit); base.Write(buffer, offset, count); }
    }
}

internal sealed class MapSceneScan(MapSceneLimits limits)
{
    internal MapSceneLimits Limits => limits;
    internal long TileReads, MetadataRows, Memberships, ScannedBytes, RetainedBytes;
    internal void Scan(long bytes)
    {
        if (!DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxScannedBytes.1", "MaxScannedBytes", ScannedBytes, bytes, limits.MaxScannedBytes, !(bytes < 0 || bytes > limits.MaxScannedBytes - ScannedBytes), CapacityDemandLifetime.Cumulative)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxScannedBytes), (decimal)ScannedBytes + bytes, limits.MaxScannedBytes);
        ScannedBytes = checked(ScannedBytes + bytes);
    }
    internal void Scratch(long bytes)
    {
        if (!DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxMetadataBytes.4", "MaxMetadataBytes", RetainedBytes, bytes, limits.MaxMetadataBytes, !(bytes < 0 || bytes > limits.MaxMetadataBytes - RetainedBytes), CapacityDemandLifetime.Transient)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataBytes), (decimal)RetainedBytes + bytes, limits.MaxMetadataBytes);
    }
    internal void RetainBytes(long bytes)
    {
        Scratch(bytes);
        RetainedBytes = checked(RetainedBytes + bytes);
    }
    internal void Retain(long bytes, int memberships = 1)
    {
        bytes = checked(bytes + 512 + memberships * 16L);
        if (DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxMetadataRows.2", "MaxMetadataRows", MetadataRows, 1, limits.MaxMetadataRows, MetadataRows >= limits.MaxMetadataRows, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMetadataRows), MetadataRows + 1, limits.MaxMetadataRows);
        if (DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxMembershipsPerResource.1", "MaxMembershipsPerResource", 0, memberships, limits.MaxMembershipsPerResource, memberships > limits.MaxMembershipsPerResource, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMembershipsPerResource), memberships, limits.MaxMembershipsPerResource);
        if (DemandDiagnostics.Check(this, "MapSceneMetadataReader.MaxMemberships.1", "MaxMemberships", Memberships, memberships, limits.MaxMemberships, memberships > limits.MaxMemberships - Memberships, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapSceneLimits.MaxMemberships), Memberships + memberships, limits.MaxMemberships);
        Scratch(bytes);
        MetadataRows++; Memberships += memberships; RetainedBytes += bytes;
    }
    internal MapSceneWork Work(bool accepted) => new(TileReads, MetadataRows, Memberships, ScannedBytes, accepted ? RetainedBytes : 0, 0, 0, 0);
}
