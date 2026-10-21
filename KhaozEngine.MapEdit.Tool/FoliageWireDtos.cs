using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEdit;

/// <summary>One stored density chunk on the wire. Density is a copy of its 1024 row-major bytes, serialized as base64.</summary>
public sealed record FoliageChunkWire(int X, int Z, byte[] Density, string Sha256);

/// <summary>A binary32 vector as x, y and z.</summary>
public sealed record FoliageVectorWire(float X, float Y, float Z);

/// <summary>A binary32 quaternion as x, y, z and w.</summary>
public sealed record FoliageQuaternionWire(float X, float Y, float Z, float W);

/// <summary>A binary32 row-major matrix as m11 through m44, every member written even when zero.</summary>
public sealed record FoliageMatrixWire(float M11, float M12, float M13, float M14, float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34, float M41, float M42, float M43, float M44);

/// <summary>One CPU sample with its complete pose: position, normalized orientation, trace scale, the unchanged candidate
/// matrix and the thinning rank.</summary>
public sealed record FoliageSampleWire(MapFoliageSampleKey Key, string? OccupiedSpaceId, MapFaceKey? Face, string AssetId,
    string[] MaterialResourceIds, int VisibleMaterial, FoliageVectorWire Position, FoliageQuaternionWire Orientation, float Scale,
    FoliageMatrixWire Transform, float ThinningRank);

/// <summary>One preview batch. Only a Complete batch carries samples and a dependency digest.</summary>
public sealed record FoliageBatchWire(string LayerId, int ChunkX, int ChunkZ, string Status, string DependencyDigest,
    FoliageSampleWire[] Samples, MapFoliageWork Work);

/// <summary>The detached metadata of one layer, copied from <see cref="MapFoliageLayerInfo"/>. It carries chunk keys and
/// never chunk memory.</summary>
public sealed class FoliageLayerWire
{
    public FoliageLayerWire(MapFoliageLayerInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        PayloadVersion = info.PayloadVersion;
        Id = info.Id;
        Support = info.Support;
        OriginX = info.OriginX;
        OriginZ = info.OriginZ;
        CellSize = info.CellSize;
        Width = info.Width;
        Depth = info.Depth;
        RowDirection = info.RowDirection;
        Seed = info.Seed;
        Spacing = info.Spacing;
        ScaleMin = info.ScaleMin;
        ScaleMax = info.ScaleMax;
        RootOffset = info.RootOffset;
        Models = info.Models.ToArray();
        AllowedVisibleMaterials = info.AllowedVisibleMaterials.ToArray();
        Materials = info.Materials.ToArray();
        Policy = info.Policy;
        ExcludeIndoors = info.ExcludeIndoors;
        ExcludeSolidObjects = info.ExcludeSolidObjects;
        DoorClearance = info.DoorClearance;
        EdgeFade = info.EdgeFade;
        CompatibilityOrdinals = info.CompatibilityOrdinals.ToArray();
        ClearanceRules = info.ClearanceRules.ToArray();
        ExceptionMasks = info.ExceptionMasks.ToArray();
        OwnerSupports = info.OwnerSupports.ToArray();
        ChunkKeys = info.ChunkKeys.ToArray();
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
    public MapFoliageModel[] Models { get; }
    public ushort[] AllowedVisibleMaterials { get; }
    public MapFoliageMaterial[] Materials { get; }
    public MapFoliagePolicy Policy { get; }
    public bool ExcludeIndoors { get; }
    public bool ExcludeSolidObjects { get; }
    public float DoorClearance { get; }
    public float EdgeFade { get; }
    public MapFoliageCompatibilityOrdinal[] CompatibilityOrdinals { get; }
    public MapFoliageClearanceRule[] ClearanceRules { get; }
    public MapFoliageMask[] ExceptionMasks { get; }
    public MapFoliageOwnerSupport[] OwnerSupports { get; }
    public MapFoliageChunkKey[] ChunkKeys { get; }
}

/// <summary>The foliage_get reply. Mode is "list" or "layer".</summary>
public sealed record FoliageReadWireResult(string Mode, FoliageLayerWire[] Layers, FoliageLayerWire? Layer, FoliageChunkWire[] Chunks,
    FoliageBatchWire? Preview);
