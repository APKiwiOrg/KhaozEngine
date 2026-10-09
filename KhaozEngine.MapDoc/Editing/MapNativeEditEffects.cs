using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
}

public readonly record struct MapBox3(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);
public sealed record MapDigestChange(string Key, string? Before, string? After);

/// <summary>Reported consequences of one accepted native document edit.</summary>
public sealed record MapNativeEditEffects(MapBox3? OldBounds, MapBox3? NewBounds,
    IReadOnlyList<MapPatchKey> Patches, IReadOnlyList<string> SpaceIds, IReadOnlyList<string> DependencyIds,
    IReadOnlyList<MapDigestChange> DigestChanges, MapNativeInvalidation Invalidates)
{
    /// <summary>Compact canonical JSON with fixed field order, ordinal IDs and invariant numbers.</summary>
    public string Describe()
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            WriteBounds(writer, "oldBounds", OldBounds);
            WriteBounds(writer, "newBounds", NewBounds);
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

    static void WriteIds(Utf8JsonWriter writer, string name, IReadOnlyList<string> ids)
    {
        writer.WriteStartArray(name);
        foreach (string id in ids.Order(StringComparer.Ordinal)) writer.WriteStringValue(id);
        writer.WriteEndArray();
    }
}
