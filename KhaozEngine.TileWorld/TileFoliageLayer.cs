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
        if (string.IsNullOrWhiteSpace(id))
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageIdBlank, InvalidMessage("id cannot be blank"), null)
            { LayerId = id };
        if (plane < 0)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliagePlaneNegative, InvalidMessage("plane cannot be negative"), null)
            { LayerId = id, Plane = plane };
        if (!Finite(originX) || !Finite(originZ))
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageOriginNonFinite, InvalidMessage("origin must be finite"), null)
            { LayerId = id, OriginX = originX, OriginZ = originZ };
        if (!Finite(cellSize) || cellSize <= 0f)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageCellSizeInvalid, InvalidMessage("cell size must be finite and positive"), null)
            { LayerId = id, CellSize = cellSize };
        if (width < 1 || height < 1)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageDimensionsNonPositive, InvalidMessage("width and height must be positive"), null)
            { LayerId = id, Width = width, Height = height };
        long samples = (long)width * height;
        if (samples > MaxDensitySamples)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageSampleLimitExceeded,
                InvalidMessage($"density has {samples} samples, the limit is {MaxDensitySamples}"), null)
            { LayerId = id, SampleCount = samples, MaximumSampleCount = MaxDensitySamples };
        if (density.Length != samples)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageDensityLengthMismatch,
                InvalidMessage($"density length {density.Length} does not match width times height {samples}"), null)
            { LayerId = id, ActualCount = density.Length, ExpectedCount = samples, Width = width, Height = height };
        if (!Finite(spacing) || spacing <= 0f)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageSpacingInvalid, InvalidMessage("spacing must be finite and positive"), null)
            { LayerId = id, Spacing = spacing };
        if (!Finite(scaleMin) || !Finite(scaleMax) || scaleMin <= 0f || scaleMax < scaleMin)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageScaleRangeInvalid, InvalidMessage("scale range must be finite, positive and ordered"), null)
            { LayerId = id, ScaleMin = scaleMin, ScaleMax = scaleMax };
        if (!Finite(rootOffset))
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageRootOffsetNonFinite, InvalidMessage("root offset must be finite"), null)
            { LayerId = id, RootOffset = rootOffset };
        if (!Finite(doorClearance) || doorClearance < 0f)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageDoorClearanceInvalid, InvalidMessage("door clearance must be finite and non-negative"), null)
            { LayerId = id, DoorClearance = doorClearance };
        if (!Finite(edgeFade) || edgeFade < 0f)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageEdgeFadeInvalid, InvalidMessage("edge fade must be finite and non-negative"), null)
            { LayerId = id, EdgeFade = edgeFade };

        TileFoliageArchetype[] modelRows = archetypes.ToArray();
        if (modelRows.Length == 0)
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageArchetypesEmpty, InvalidMessage("at least one archetype is required"), null)
            { LayerId = id, ArchetypeCount = modelRows.Length };
        float totalWeight = 0f;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < modelRows.Length; index++)
        {
            TileFoliageArchetype row = modelRows[index];
            if (string.IsNullOrWhiteSpace(row.Id))
                throw new TileFoliageArgumentException(TileWorldRefusal.FoliageArchetypeIdBlank, InvalidMessage("archetype ids cannot be blank"), null)
                { LayerId = id, ArchetypeId = row.Id, ArchetypeIndex = index };
            if (!ids.Add(row.Id))
                throw new TileFoliageArgumentException(TileWorldRefusal.FoliageArchetypeDuplicate, InvalidMessage($"archetype '{row.Id}' is listed twice"), null)
                { LayerId = id, ArchetypeId = row.Id };
            if (!Finite(row.Weight) || row.Weight <= 0f)
                throw new TileFoliageArgumentException(TileWorldRefusal.FoliageWeightInvalid, InvalidMessage($"archetype '{row.Id}' must have a finite positive weight"), null)
                { LayerId = id, ArchetypeId = row.Id, Weight = row.Weight };
            totalWeight += row.Weight;
        }
        if (!Finite(totalWeight))
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageTotalWeightNonFinite, InvalidMessage("archetype weights exceed the supported total"), null)
            { LayerId = id, TotalWeight = totalWeight };

        ushort[] materialRows = allowedUnderlays?.Distinct().OrderBy(x => x).ToArray() ?? Array.Empty<ushort>();
        if (materialRows.Contains((ushort)0))
            throw new TileFoliageArgumentException(TileWorldRefusal.FoliageUnderlayVoid, InvalidMessage("underlay id 0 is void and cannot accept foliage"), null)
            { LayerId = id, MaterialId = 0 };

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

    internal static string InvalidMessage(string detail) => $"Invalid foliage layer: {detail}.";
    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
