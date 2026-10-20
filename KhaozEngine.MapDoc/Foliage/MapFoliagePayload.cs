using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The fixed ceilings of foliage payload 1. The closed decoder checks counts and sizes against these before it
/// materializes nested records or decodes density, and validation applies the same values to in-memory sets.</summary>
internal static class MapFoliagePayloadLimits
{
    internal const int MaxExtent = 1_048_576;
    internal const int MaxChunksPerLayer = 16_384;
    internal const int MaxLayers = 256;
    internal const long MaxDensityBytes = 67_108_864;
    internal const int MaxDocumentChunks = (int)(MaxDensityBytes / MapFoliageChunk.ByteLength);
    internal const int MaxModels = 256;
    internal const int MaxMaterials = 65_535;
    internal const int MaxClearanceRules = 4096;
    internal const int MaxExceptionMasks = 4096;
    internal const int MaxOwnerSupports = 65_536;
    internal const long MaxUtf8Bytes = 134_217_728;
    internal const int MaxTokenChars = 1368;
}

/// <summary>A density chunk address. Chunk (X, Z) holds sample indices [32X, 32X + 32) by [32Z, 32Z + 32).</summary>
public readonly record struct MapFoliageChunkKey(int X, int Z);

/// <summary>One advertised logical chunk, independently of packing and residency.</summary>
public sealed record MapFoliageChunkDigest(MapFoliageChunkKey Key, string Sha256);

/// <summary>One immutable 32 by 32 block of density bytes. Rows advance positive world Z and X advances within a row.
/// The bytes are copied in and out, so no caller can reach the stored array.</summary>
public sealed class MapFoliageChunk
{
    /// <summary>Samples along each chunk edge.</summary>
    public const int Size = 32;

    /// <summary>Density bytes in one chunk.</summary>
    public const int ByteLength = Size * Size;

    readonly byte[] _density;

    public MapFoliageChunk(MapFoliageChunkKey key, ReadOnlySpan<byte> density)
    {
        if (density.Length != ByteLength)
            throw new ArgumentException($"A foliage chunk holds exactly {ByteLength} density bytes, not {density.Length}.", nameof(density));
        Key = key;
        _density = density.ToArray();
        if (MapFoliageStorageWork.Current is { } work) work.ChunkHashes++;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(_density));
    }

    public MapFoliageChunkKey Key { get; }
    internal string Sha256 { get; }

    /// <summary>Reads one density byte at a local column and row.</summary>
    public byte DensityAt(int localX, int localZ)
    {
        if ((uint)localX >= Size) throw new ArgumentOutOfRangeException(nameof(localX));
        if ((uint)localZ >= Size) throw new ArgumentOutOfRangeException(nameof(localZ));
        return _density[localZ * Size + localX];
    }

    /// <summary>A detached row-major copy of the chunk's bytes.</summary>
    public byte[] CopyDensity() => (byte[])_density.Clone();

    internal ReadOnlySpan<byte> Density => _density;
}

/// <summary>One weighted model asset. Model order is authored and selects models deterministically.</summary>
public sealed record MapFoliageModel(string AssetId, float Weight);

public enum MapFoliageMaterialKind { Ordinary, Water }

/// <summary>A visible material ID and the verified Material resource it names.</summary>
public sealed record MapFoliageMaterial(ushort Id, string ResourceId, MapFoliageMaterialKind Kind);

/// <summary>The named, versioned sampling policy. Payload 1 supports legacy-tile version 1 only.</summary>
public sealed record MapFoliagePolicy(string Id, int Version, float NormalBoundaryHeight);

/// <summary>The root foliage section: an ordered list of density layers. Present only when a document authors foliage.
/// Layers keep their authored order and their IDs are unique under ordinal comparison. Every serializer uses the closed
/// codec, never member reflection, since a chunk's span constructor has no reflective form.</summary>
[JsonConverter(typeof(MapFoliageSetJsonConverter))]
public sealed class MapFoliageSet
{
    readonly ReadOnlyCollection<MapFoliageLayer> _layers;

    public MapFoliageSet(int payloadVersion, IReadOnlyList<MapFoliageLayer> layers)
    {
        PayloadVersion = payloadVersion;
        _layers = MapFoliageLayer.Copy(layers, nameof(layers));
    }

    public int PayloadVersion { get; }
    public IReadOnlyList<MapFoliageLayer> Layers => _layers;

    /// <summary>A copy with new set and layer objects and freshly copied collections. Chunks are immutable and shared.</summary>
    public MapFoliageSet Clone() => new(PayloadVersion, _layers.Select(l => l.Copy()).ToArray());
}

/// <summary>One sparse density raster over sample indices [0, Width) by [0, Depth). Sample (x, z) sits at world
/// (OriginX + x * CellSize, OriginZ + z * CellSize). Absent chunks read as zero, and an explicit all-zero chunk is
/// kept as authored. No whole-domain array exists.</summary>
public sealed class MapFoliageLayer
{
    readonly ReadOnlyCollection<MapFoliageModel> _models;
    readonly ReadOnlyCollection<ushort> _allowed;
    readonly ReadOnlyCollection<MapFoliageMaterial> _materials;
    readonly ReadOnlyCollection<MapFoliageCompatibilityOrdinal> _ordinals;
    readonly ReadOnlyCollection<MapFoliageClearanceRule> _rules;
    readonly ReadOnlyCollection<MapFoliageMask> _masks;
    readonly ReadOnlyCollection<MapFoliageOwnerSupport> _owners;
    readonly ReadOnlyCollection<MapFoliageChunk> _chunks;
    readonly ReadOnlyCollection<MapFoliageChunkDigest> _digests;
    readonly HashSet<MapFoliageChunkKey> _advertisedKeys;
    readonly Dictionary<MapFoliageChunkKey, MapFoliageChunk> _byKey = new();

    public MapFoliageLayer(int PayloadVersion, string Id, MapSupportBinding Support, float OriginX, float OriginZ,
        float CellSize, int Width, int Depth, MapRowDirection RowDirection, int Seed, float Spacing, float ScaleMin,
        float ScaleMax, float RootOffset, IReadOnlyList<MapFoliageModel> Models, IReadOnlyList<ushort> AllowedVisibleMaterials,
        IReadOnlyList<MapFoliageMaterial> Materials, MapFoliagePolicy Policy, bool ExcludeIndoors, bool ExcludeSolidObjects,
        float DoorClearance, float EdgeFade, IReadOnlyList<MapFoliageCompatibilityOrdinal> CompatibilityOrdinals,
        IReadOnlyList<MapFoliageClearanceRule> ClearanceRules, IReadOnlyList<MapFoliageMask> ExceptionMasks,
        IReadOnlyList<MapFoliageOwnerSupport> OwnerSupports, IReadOnlyList<MapFoliageChunk> Chunks)
    {
        ArgumentNullException.ThrowIfNull(Support);
        ArgumentNullException.ThrowIfNull(Policy);
        ArgumentNullException.ThrowIfNull(AllowedVisibleMaterials);
        this.PayloadVersion = PayloadVersion;
        this.Id = Id;
        this.Support = Support;
        this.OriginX = OriginX;
        this.OriginZ = OriginZ;
        this.CellSize = CellSize;
        this.Width = Width;
        this.Depth = Depth;
        this.RowDirection = RowDirection;
        this.Seed = Seed;
        this.Spacing = Spacing;
        this.ScaleMin = ScaleMin;
        this.ScaleMax = ScaleMax;
        this.RootOffset = RootOffset;
        _models = Copy(Models, nameof(Models));
        _allowed = Array.AsReadOnly(AllowedVisibleMaterials.ToArray());
        _materials = Copy(Materials, nameof(Materials));
        this.Policy = Policy;
        this.ExcludeIndoors = ExcludeIndoors;
        this.ExcludeSolidObjects = ExcludeSolidObjects;
        this.DoorClearance = DoorClearance;
        this.EdgeFade = EdgeFade;
        _ordinals = Copy(CompatibilityOrdinals, nameof(CompatibilityOrdinals));
        _rules = Copy(ClearanceRules, nameof(ClearanceRules));
        _masks = Copy(ExceptionMasks, nameof(ExceptionMasks));
        _owners = Copy(OwnerSupports, nameof(OwnerSupports));
        // Canonical chunk order is row-major by key, so identity never depends on construction order. Validation reports
        // a repeated key, and reads use the first.
        _chunks = Array.AsReadOnly(Copy(Chunks, nameof(Chunks)).OrderBy(c => c.Key.Z).ThenBy(c => c.Key.X).ToArray());
        foreach (MapFoliageChunk chunk in _chunks) _byKey.TryAdd(chunk.Key, chunk);
        _digests = Array.AsReadOnly(_chunks.Select(c => new MapFoliageChunkDigest(c.Key, Digest(c))).ToArray());
        _advertisedKeys = _digests.Select(d => d.Key).ToHashSet();
    }

    public int PayloadVersion { get; }
    public string Id { get; }
    public MapSupportBinding Support { get; }
    public float OriginX { get; }
    public float OriginZ { get; }
    public float CellSize { get; }
    public int Width { get; }
    public int Depth { get; }
    public MapRowDirection RowDirection { get; }
    public int Seed { get; }
    public float Spacing { get; }
    public float ScaleMin { get; }
    public float ScaleMax { get; }
    public float RootOffset { get; }
    public IReadOnlyList<MapFoliageModel> Models => _models;
    public IReadOnlyList<ushort> AllowedVisibleMaterials => _allowed;
    public IReadOnlyList<MapFoliageMaterial> Materials => _materials;
    public MapFoliagePolicy Policy { get; }
    public bool ExcludeIndoors { get; }
    public bool ExcludeSolidObjects { get; }
    public float DoorClearance { get; }
    public float EdgeFade { get; }
    public IReadOnlyList<MapFoliageCompatibilityOrdinal> CompatibilityOrdinals => _ordinals;
    public IReadOnlyList<MapFoliageClearanceRule> ClearanceRules => _rules;
    public IReadOnlyList<MapFoliageMask> ExceptionMasks => _masks;
    public IReadOnlyList<MapFoliageOwnerSupport> OwnerSupports => _owners;
    public IReadOnlyList<MapFoliageChunk> Chunks => _chunks;
    public IReadOnlyList<MapFoliageChunkDigest> ChunkDigests => _digests;

    /// <summary>Reads one sample. X is the column and Z the row, inside [0, Width) by [0, Depth). An absent chunk reads
    /// zero. An advertised nonresident chunk refuses rather than publishing zero.</summary>
    public byte DensityAt(int x, int z)
    {
        if (x < 0 || x >= Width) throw new ArgumentOutOfRangeException(nameof(x));
        if (z < 0 || z >= Depth) throw new ArgumentOutOfRangeException(nameof(z));
        var key = new MapFoliageChunkKey(x / MapFoliageChunk.Size, z / MapFoliageChunk.Size);
        if (_byKey.TryGetValue(key, out MapFoliageChunk? chunk))
            return chunk.DensityAt(x % MapFoliageChunk.Size, z % MapFoliageChunk.Size);
        if (_advertisedKeys.Contains(key)) throw new MapDocumentException($"foliage layer '{Id}' chunk {key} is unavailable.");
        return 0;
    }

    internal MapFoliageLayer Copy() => FromStorage(this, _digests, _chunks);

    internal static MapFoliageLayer FromStorage(MapFoliageLayer metadata,
        IReadOnlyList<MapFoliageChunkDigest> advertised, IReadOnlyList<MapFoliageChunk> resident) => new(metadata, advertised, resident);

    MapFoliageLayer(MapFoliageLayer m, IReadOnlyList<MapFoliageChunkDigest> advertised, IReadOnlyList<MapFoliageChunk> resident)
        : this(m.PayloadVersion, m.Id, m.Support, m.OriginX, m.OriginZ, m.CellSize, m.Width, m.Depth, m.RowDirection,
            m.Seed, m.Spacing, m.ScaleMin, m.ScaleMax, m.RootOffset, m.Models, m.AllowedVisibleMaterials, m.Materials,
            m.Policy, m.ExcludeIndoors, m.ExcludeSolidObjects, m.DoorClearance, m.EdgeFade, m.CompatibilityOrdinals,
            m.ClearanceRules, m.ExceptionMasks, m.OwnerSupports, resident)
    {
        var digests = new Dictionary<MapFoliageChunkKey, MapFoliageChunkDigest>();
        foreach (MapFoliageChunkDigest row in Copy(advertised, nameof(advertised)))
        {
            MapFoliageRegionPack.RequireDigest(row.Sha256);
            if (!digests.TryAdd(row.Key, row)) throw new MapDocumentException("foliage: duplicate advertised chunk key.");
        }
        foreach (MapFoliageChunk chunk in _chunks) digests[chunk.Key] = new(chunk.Key, Digest(chunk));
        _digests = Array.AsReadOnly(digests.Values.OrderBy(d => d.Key.Z).ThenBy(d => d.Key.X).ToArray());
        _advertisedKeys = digests.Keys.ToHashSet();
    }

    internal static string Digest(MapFoliageChunk chunk) => chunk.Sha256;

    internal static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> items, string name) where T : class
    {
        ArgumentNullException.ThrowIfNull(items, name);
        T[] copy = items.ToArray();
        if (Array.IndexOf(copy, null) >= 0) throw new ArgumentException("Foliage collections must not contain null entries.", name);
        return Array.AsReadOnly(copy);
    }
}
