using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace KhaozEngine.TileWorld;

/// <summary>One weighted object archetype used as a cosmetic foliage mesh.</summary>
public readonly record struct TileFoliageArchetype(string Id, float Weight);

/// <summary>An immutable authored density raster and its ground-cover placement rules.</summary>
public sealed class TileFoliageLayer
{
    /// <summary>Maximum density samples in one layer.</summary>
    public const int MaxDensitySamples = 16_777_216;

    readonly byte[] _density;
    readonly ReadOnlyCollection<TileFoliageArchetype> _archetypes;
    readonly ReadOnlyCollection<ushort> _allowedUnderlays;

    public string Id { get; }
    public int Plane { get; }
    public float OriginX { get; }
    public float OriginZ { get; }
    public float CellSize { get; }
    public int Width { get; }
    public int Height { get; }
    public int Seed { get; }
    public float Spacing { get; }
    public float ScaleMin { get; }
    public float ScaleMax { get; }
    public float RootOffset { get; }
    public IReadOnlyList<TileFoliageArchetype> Archetypes => _archetypes;
    /// <summary>Underlay ids that accept this layer. Empty accepts every ordinary ground material.</summary>
    public IReadOnlyList<ushort> AllowedUnderlays => _allowedUnderlays;
    public bool ExcludeIndoors { get; }
    public bool ExcludeSolidObjects { get; }
    public float DoorClearance { get; }
    public float EdgeFade { get; }

    public TileFoliageLayer(
        string id,
        int plane,
        float originX,
        float originZ,
        float cellSize,
        int width,
        int height,
        byte[] density,
        int seed,
        float spacing,
        float scaleMin,
        float scaleMax,
        float rootOffset,
        IEnumerable<TileFoliageArchetype> archetypes,
        IEnumerable<ushort>? allowedUnderlays = null,
        bool excludeIndoors = true,
        bool excludeSolidObjects = true,
        float doorClearance = 0f,
        float edgeFade = 0f)
    {
        ArgumentNullException.ThrowIfNull(density);
        ArgumentNullException.ThrowIfNull(archetypes);
        if (string.IsNullOrWhiteSpace(id)) throw Invalid("id cannot be blank");
        if (plane < 0) throw Invalid("plane cannot be negative");
        if (!Finite(originX) || !Finite(originZ)) throw Invalid("origin must be finite");
        if (!Finite(cellSize) || cellSize <= 0f) throw Invalid("cell size must be finite and positive");
        if (width < 1 || height < 1) throw Invalid("width and height must be positive");
        long samples = (long)width * height;
        if (samples > MaxDensitySamples) throw Invalid($"density has {samples} samples, the limit is {MaxDensitySamples}");
        if (density.Length != samples) throw Invalid($"density length {density.Length} does not match width times height {samples}");
        if (!Finite(spacing) || spacing <= 0f) throw Invalid("spacing must be finite and positive");
        if (!Finite(scaleMin) || !Finite(scaleMax) || scaleMin <= 0f || scaleMax < scaleMin)
            throw Invalid("scale range must be finite, positive and ordered");
        if (!Finite(rootOffset)) throw Invalid("root offset must be finite");
        if (!Finite(doorClearance) || doorClearance < 0f) throw Invalid("door clearance must be finite and non-negative");
        if (!Finite(edgeFade) || edgeFade < 0f) throw Invalid("edge fade must be finite and non-negative");

        TileFoliageArchetype[] modelRows = archetypes.ToArray();
        if (modelRows.Length == 0) throw Invalid("at least one archetype is required");
        float totalWeight = 0f;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (TileFoliageArchetype row in modelRows)
        {
            if (string.IsNullOrWhiteSpace(row.Id)) throw Invalid("archetype ids cannot be blank");
            if (!ids.Add(row.Id)) throw Invalid($"archetype '{row.Id}' is listed twice");
            if (!Finite(row.Weight) || row.Weight <= 0f) throw Invalid($"archetype '{row.Id}' must have a finite positive weight");
            totalWeight += row.Weight;
        }
        if (!Finite(totalWeight)) throw Invalid("archetype weights exceed the supported total");

        ushort[] materialRows = allowedUnderlays?.Distinct().OrderBy(x => x).ToArray() ?? Array.Empty<ushort>();
        if (materialRows.Contains((ushort)0)) throw Invalid("underlay id 0 is void and cannot accept foliage");

        Id = id;
        Plane = plane;
        OriginX = originX;
        OriginZ = originZ;
        CellSize = cellSize;
        Width = width;
        Height = height;
        _density = (byte[])density.Clone();
        Seed = seed;
        Spacing = spacing;
        ScaleMin = scaleMin;
        ScaleMax = scaleMax;
        RootOffset = rootOffset;
        _archetypes = Array.AsReadOnly(modelRows);
        _allowedUnderlays = Array.AsReadOnly(materialRows);
        ExcludeIndoors = excludeIndoors;
        ExcludeSolidObjects = excludeSolidObjects;
        DoorClearance = doorClearance;
        EdgeFade = edgeFade;
    }

    /// <summary>Reads one density sample. X is the column and Z is the row.</summary>
    public byte DensityAt(int x, int z)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)z >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(z));
        return _density[z * Width + x];
    }

    /// <summary>Returns a detached row-major density copy. X advances within each Z row.</summary>
    public byte[] CopyDensity() => (byte[])_density.Clone();

    /// <summary>Copies this layer with replacement row-major density.</summary>
    public TileFoliageLayer WithDensity(byte[] density) => new(
        Id, Plane, OriginX, OriginZ, CellSize, Width, Height, density, Seed, Spacing, ScaleMin, ScaleMax,
        RootOffset, _archetypes, _allowedUnderlays, ExcludeIndoors, ExcludeSolidObjects, DoorClearance, EdgeFade);

    internal static ArgumentException Invalid(string detail) => new($"Invalid foliage layer: {detail}.");
    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
