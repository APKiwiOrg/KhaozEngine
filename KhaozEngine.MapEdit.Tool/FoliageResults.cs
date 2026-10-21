using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEdit;

/// <summary>The outcome of one foliage verb. An applied edit is one accepted native history step whose effects come from
/// the public document finalizer. A dry run applies nothing and carries the policy differential. Identity is the open
/// document's whole authored identity after the call. A refusal throws and changes nothing.</summary>
public sealed record MapFoliageMutationResult(bool Applied, MapFoliageStatus Status, string Identity, string Effects, bool Dirty,
    string? UndoLabel, string? RedoLabel, MapFoliageDifferentialResult? Differential);

/// <summary>Detached metadata of one foliage layer: every layer setting except its density chunks, plus the complete list
/// of advertised chunk keys in canonical row-major order. Lists are copies, so no caller reaches document state.</summary>
public sealed class MapFoliageLayerInfo
{
    public MapFoliageLayerInfo(MapFoliageLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        PayloadVersion = layer.PayloadVersion;
        Id = layer.Id;
        Support = layer.Support;
        OriginX = layer.OriginX;
        OriginZ = layer.OriginZ;
        CellSize = layer.CellSize;
        Width = layer.Width;
        Depth = layer.Depth;
        RowDirection = layer.RowDirection;
        Seed = layer.Seed;
        Spacing = layer.Spacing;
        ScaleMin = layer.ScaleMin;
        ScaleMax = layer.ScaleMax;
        RootOffset = layer.RootOffset;
        Models = Copy(layer.Models);
        AllowedVisibleMaterials = Copy(layer.AllowedVisibleMaterials);
        Materials = Copy(layer.Materials);
        Policy = layer.Policy;
        ExcludeIndoors = layer.ExcludeIndoors;
        ExcludeSolidObjects = layer.ExcludeSolidObjects;
        DoorClearance = layer.DoorClearance;
        EdgeFade = layer.EdgeFade;
        CompatibilityOrdinals = Copy(layer.CompatibilityOrdinals);
        ClearanceRules = Copy(layer.ClearanceRules);
        ExceptionMasks = Copy(layer.ExceptionMasks);
        OwnerSupports = Copy(layer.OwnerSupports);
        ChunkKeys = Copy(layer.ChunkDigests.Select(d => d.Key).ToArray());
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
    public IReadOnlyList<MapFoliageModel> Models { get; }
    public IReadOnlyList<ushort> AllowedVisibleMaterials { get; }
    public IReadOnlyList<MapFoliageMaterial> Materials { get; }
    public MapFoliagePolicy Policy { get; }
    public bool ExcludeIndoors { get; }
    public bool ExcludeSolidObjects { get; }
    public float DoorClearance { get; }
    public float EdgeFade { get; }
    public IReadOnlyList<MapFoliageCompatibilityOrdinal> CompatibilityOrdinals { get; }
    public IReadOnlyList<MapFoliageClearanceRule> ClearanceRules { get; }
    public IReadOnlyList<MapFoliageMask> ExceptionMasks { get; }
    public IReadOnlyList<MapFoliageOwnerSupport> OwnerSupports { get; }
    /// <summary>Every advertised chunk key. An absent key reads zero density.</summary>
    public IReadOnlyList<MapFoliageChunkKey> ChunkKeys { get; }

    static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> items) => Array.AsReadOnly(items.ToArray());
}

/// <summary>List reads every layer's metadata. Layer reads one layer, with only the chunks and preview it requests.</summary>
public enum MapFoliageReadMode { List, Layer }

/// <summary>One detached foliage read. List mode has every layer in authoring order, Layer null, no chunks and no preview.
/// Layer mode has Layers empty, the named layer's metadata, the stored chunks its rectangle reaches and an optional CPU
/// preview batch.</summary>
public sealed record MapFoliageReadResult(MapFoliageReadMode Mode, IReadOnlyList<MapFoliageLayerInfo> Layers, MapFoliageLayerInfo? Layer,
    IReadOnlyList<MapFoliageChunk> Chunks, MapFoliageSampleBatch? Preview);
