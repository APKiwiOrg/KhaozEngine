using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc;

/// <summary>One row of a tiled document's manifest index: an occupied tile, the canonical hash of its four
/// content lists, and whether this in-memory document actually holds that content.
/// <para>This is a positional <c>readonly record struct</c>, so it is the WRONG place to put anything that
/// might grow: adding a fourth positional member changes the primary constructor AND the arity of the
/// generated <c>Deconstruct</c>, which breaks <c>var (coord, hash, loaded) = entry;</c> at the source level
/// and every compiled caller at the binary level. Anything else the index needs lives on
/// <see cref="MapTileIndex"/>, which is a class.</para></summary>
public readonly record struct MapTileEntry(MapTileCoord Coord, string Hash, bool Loaded);

/// <summary>The occupied-tile index of a tiled document: which tiles exist, what each hashes to, and which of
/// them this document actually holds. A monolithic document has none (<see cref="MapDocument.Tiles"/> is
/// null), and a windowed load has one whose unloaded entries carry the rest of the world through a partial save
/// untouched.</summary>
public sealed class MapTileIndex
{
    readonly Dictionary<MapTileCoord, MapTileEntry> _byCoord;
    readonly MapTileEntry[] _entries;

    /// <summary>Builds an index from entries in any order. They are sorted ascending (Z, then X) here, so the
    /// world hash never depends on the order the caller happened to discover tiles in.</summary>
    /// <exception cref="MapDocumentException">Two entries share a tile coordinate.</exception>
    internal MapTileIndex(float tileSize, int schemeVersion, string? sourceDirectory, IEnumerable<MapTileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = new List<MapTileEntry>(entries);
        list.Sort(static (a, b) => a.Coord.Z != b.Coord.Z ? a.Coord.Z.CompareTo(b.Coord.Z) : a.Coord.X.CompareTo(b.Coord.X));

        _byCoord = new Dictionary<MapTileCoord, MapTileEntry>(list.Count);
        int loaded = 0;
        foreach (MapTileEntry e in list)
        {
            if (!_byCoord.TryAdd(e.Coord, e))
                throw new MapDocumentException($"duplicate tile index entry for tile ({e.Coord.X}, {e.Coord.Z}).");
            if (e.Loaded) loaded++;
        }

        _entries = list.ToArray();
        TileSize = tileSize;
        SchemeVersion = schemeVersion;
        SourceDirectory = sourceDirectory;
        LoadedCount = loaded;
    }

    /// <summary>Document tile edge in world meters, as declared by the manifest this index came from.</summary>
    public float TileSize { get; }

    /// <summary>The occupied tiles, ascending (Z, then X).</summary>
    public IReadOnlyList<MapTileEntry> Entries => _entries;

    /// <summary>How many of <see cref="Entries"/> are loaded in this document.</summary>
    public int LoadedCount { get; }

    /// <summary><see cref="MapDocumentHash.SchemeVersion"/> the stored <see cref="MapTileEntry.Hash"/> values
    /// were computed under, read from the manifest. Recorded from the first release because a windowed save
    /// carries stored hashes through verbatim while recomputing the loaded ones, and mixing two
    /// canonicalizations under one label is permanently wrong rather than detectably wrong.</summary>
    public int SchemeVersion { get; }

    /// <summary>True when at least one indexed tile is NOT loaded, so this document is a WINDOW onto a larger
    /// world. Every save entry point checks this flag: a whole-document write of a window silently drops every
    /// unloaded tile and looks like a successful save.</summary>
    public bool IsPartial => LoadedCount < _entries.Length;

    /// <summary>The directory this index was read from, null for an index built in memory from a whole
    /// document. A partial document may only be written back here.</summary>
    public string? SourceDirectory { get; }

    public bool TryGet(MapTileCoord coord, out MapTileEntry entry) => _byCoord.TryGetValue(coord, out entry);

    public bool IsOccupied(MapTileCoord coord) => _byCoord.ContainsKey(coord);
}
