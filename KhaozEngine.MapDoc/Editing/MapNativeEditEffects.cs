using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

[Flags]
public enum MapNativeInvalidation
{
    None = 0,
    Terrain = 1,
    Physics = 2,
    Nav = 4,
    Material = 8,
    Residency = 16,
    Placements = 32,

    /// <summary>The edit changed geometry whose bounds are unknown, so the old and new bounds do not limit it. It is a
    /// full reset for the other flags: discard all prior derived state for this world and rebuild from the post-edit
    /// world, since pre-edit extents outside the post-edit world cannot be listed.</summary>
    Unbounded = 64,

    /// <summary>The edit changed water: a body's level, domain, covers or medium, or a bound a body reads. The bodies
    /// are named in <see cref="MapNativeEditEffects.WaterBodyIds"/>, and their residency and navigation medium
    /// identity follow.</summary>
    Medium = 128,

    /// <summary>The edit changed authored foliage. The changed density chunks and their cosmetic bounds are named in
    /// <see cref="MapNativeEditEffects.FoliageChunks"/>. Foliage never adds Physics, Nav or Residency.</summary>
    Foliage = 256,
}

public readonly record struct MapBox3(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    /// <summary>The smallest box containing this box and <paramref name="other"/>.</summary>
    public MapBox3 Union(MapBox3 other) => new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY),
        Math.Min(MinZ, other.MinZ), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY), Math.Max(MaxZ, other.MaxZ));
}
public sealed record MapDigestChange(string Key, string? Before, string? After);

/// <summary>One effective marker name change. A created marker has a null <paramref name="Before"/> and a deleted
/// marker a null <paramref name="After"/>.</summary>
public sealed record MapMarkerNameChange(string MarkerId, string? Before, string? After);

/// <summary>Reported consequences of one accepted native document edit.</summary>
public sealed record MapNativeEditEffects(MapBox3? OldBounds, MapBox3? NewBounds,
    IReadOnlyList<MapPatchKey> Patches, IReadOnlyList<string> SpaceIds, IReadOnlyList<string> DependencyIds,
    IReadOnlyList<MapDigestChange> DigestChanges, MapNativeInvalidation Invalidates)
{
    MapBox3? _oldSpatialBounds, _newSpatialBounds;

    /// <summary>The conservative old extent used by Physics, Nav and Residency. Without an explicit spatial extent
    /// it follows <see cref="OldBounds"/>, including record copies that change the report. It always includes the report.</summary>
    public MapBox3? OldSpatialBounds
    {
        get => Spatial(OldBounds, _oldSpatialBounds);
        init => _oldSpatialBounds = value;
    }

    /// <summary>The conservative new extent used by Physics, Nav and Residency, with the same fallback and union
    /// rule as <see cref="OldSpatialBounds"/>.</summary>
    public MapBox3? NewSpatialBounds
    {
        get => Spatial(NewBounds, _newSpatialBounds);
        init => _newSpatialBounds = value;
    }

    /// <summary>The water bodies the edit changes, empty when it changes none.</summary>
    public IReadOnlyList<string> WaterBodyIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> InstanceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ChildIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RoofIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LightIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MarkerIds { get; init; } = Array.Empty<string>();

    /// <summary>The effective marker name changes of the edit, empty when no name changes.</summary>
    public IReadOnlyList<MapMarkerNameChange> MarkerNameChanges { get; init; } = Array.Empty<MapMarkerNameChange>();

    /// <summary>The directly changed foliage density chunks, empty when the edit changes none.</summary>
    public IReadOnlyList<MapFoliageChunkChange> FoliageChunks { get; init; } = Array.Empty<MapFoliageChunkChange>();

    /// <summary>Compact canonical JSON with fixed field order, ordinal IDs and invariant numbers. <c>waterBodyIds</c>
    /// follows <c>dependencyIds</c> only when <see cref="WaterBodyIds"/> is not empty, and <c>markerNameChanges</c>
    /// follows <c>markerIds</c> only when <see cref="MarkerNameChanges"/> is not empty, and <c>foliageChunks</c> follows
    /// them only when <see cref="FoliageChunks"/> is not empty. Spatial bounds follow the reported
    /// bounds only when they differ, preserving descriptions whose extents already agree.</summary>
    public string Describe()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            WriteBounds(writer, "oldBounds", OldBounds);
            WriteBounds(writer, "newBounds", NewBounds);
            if (OldSpatialBounds != OldBounds) WriteBounds(writer, "oldSpatialBounds", OldSpatialBounds);
            if (NewSpatialBounds != NewBounds) WriteBounds(writer, "newSpatialBounds", NewSpatialBounds);
            writer.WriteStartArray("patches");
            foreach (MapPatchKey patch in Patches.Order())
            {
                writer.WriteStartArray();
                writer.WriteStringValue(patch.SurfaceId);
                writer.WriteStringValue(patch.SlotX.ToString(CultureInfo.InvariantCulture));
                writer.WriteStringValue(patch.SlotZ.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            WriteIds(writer, "spaceIds", SpaceIds);
            WriteIds(writer, "dependencyIds", DependencyIds);
            // Written only when present, so every description without water keeps its bytes.
            if (WaterBodyIds.Count != 0) WriteIds(writer, "waterBodyIds", WaterBodyIds);
            if (InstanceIds.Count != 0) WriteIds(writer, "instanceIds", InstanceIds);
            if (ChildIds.Count != 0) WriteIds(writer, "childIds", ChildIds);
            if (RoofIds.Count != 0) WriteIds(writer, "roofIds", RoofIds);
            if (LightIds.Count != 0) WriteIds(writer, "lightIds", LightIds);
            if (MarkerIds.Count != 0) WriteIds(writer, "markerIds", MarkerIds);
            if (MarkerNameChanges.Count != 0)
            {
                writer.WriteStartArray("markerNameChanges");
                foreach (MapMarkerNameChange change in MarkerNameChanges.OrderBy(c => c.MarkerId, StringComparer.Ordinal)
                    .ThenBy(c => c.Before, StringComparer.Ordinal).ThenBy(c => c.After, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("markerId", change.MarkerId);
                    writer.WriteString("before", change.Before);
                    writer.WriteString("after", change.After);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (FoliageChunks.Count != 0) WriteFoliageChunks(writer, FoliageChunks);
            writer.WriteStartArray("digestChanges");
            foreach (MapDigestChange change in DigestChanges.OrderBy(c => c.Key, StringComparer.Ordinal)
                .ThenBy(c => c.Before, StringComparer.Ordinal).ThenBy(c => c.After, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("key", change.Key);
                writer.WriteString("before", change.Before);
                writer.WriteString("after", change.After);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("invalidates", (int)Invalidates);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    static MapBox3? Spatial(MapBox3? reported, MapBox3? geometry) =>
        reported is { } bounds ? geometry is { } spatial ? bounds.Union(spatial) : bounds : geometry;

    static void WriteBounds(Utf8JsonWriter writer, string name, MapBox3? bounds)
    {
        writer.WritePropertyName(name);
        if (bounds is not { } box)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        writer.WriteNumberValue(box.MinX);
        writer.WriteNumberValue(box.MinY);
        writer.WriteNumberValue(box.MinZ);
        writer.WriteNumberValue(box.MaxX);
        writer.WriteNumberValue(box.MaxY);
        writer.WriteNumberValue(box.MaxZ);
        writer.WriteEndArray();
    }

    static void WriteFoliageChunks(Utf8JsonWriter writer, IReadOnlyList<MapFoliageChunkChange> changes)
    {
        writer.WriteStartArray("foliageChunks");
        foreach (MapFoliageChunkChange change in changes.OrderBy(c => c.LayerId, StringComparer.Ordinal)
            .ThenBy(c => c.Chunk.Z).ThenBy(c => c.Chunk.X))
        {
            writer.WriteStartObject();
            writer.WriteString("layerId", change.LayerId);
            writer.WriteNumber("x", change.Chunk.X);
            writer.WriteNumber("z", change.Chunk.Z);
            WriteRect(writer, "oldBounds", change.OldBounds);
            WriteRect(writer, "newBounds", change.NewBounds);
            writer.WriteString("before", change.BeforeDigest);
            writer.WriteString("after", change.AfterDigest);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static void WriteRect(Utf8JsonWriter writer, string name, MapFoliageRect? bounds)
    {
        writer.WritePropertyName(name);
        if (bounds is not { } rect)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        writer.WriteNumberValue(rect.MinX);
        writer.WriteNumberValue(rect.MinZ);
        writer.WriteNumberValue(rect.MaxX);
        writer.WriteNumberValue(rect.MaxZ);
        writer.WriteEndArray();
    }

    static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<string> ids)
    {
        writer.WriteStartArray(name);
        foreach (string id in ids.Order(StringComparer.Ordinal)) writer.WriteStringValue(id);
        writer.WriteEndArray();
    }
}
