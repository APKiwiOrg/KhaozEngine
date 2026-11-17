using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.Render3D;

/// <summary>
/// The grouped <c>childRoles</c> member of a format 2 host coverage region. JSON groups the cells of one source triangle
/// in one entry, while <see cref="HostCoverageRegion"/> holds one record per cell. Reading flattens entries in source
/// order and keeps each record's entry and cell position, so refusals name source JSON positions after the region sorts
/// its records. Writing groups the sorted records back into one canonical entry per triangle.
/// </summary>
internal static class HostCoverageChildRoleJson
{
    internal const string Member = "childRoles";

    /// <summary>Flattens one region's <c>childRoles</c> array. <paramref name="context"/> is the region's rooted path, and
    /// the returned path function is region-rooted, as <see cref="HostCoverageRegion"/> expects.</summary>
    internal static (CoverageChildRole[] Roles, Func<int, CoverageChildRoleField, string> Path) Read(JsonElement entries,
        string context, string regionKey)
    {
        int entryCount = entries.GetArrayLength();
        if (entryCount == 0)
            throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleCapacity, "A childRoles member needs at least one entry.")
            { Path = $"{context}.{Member}", RegionKey = regionKey, Count = 0, Limit = HostCoverageMap.MaxTriangles };
        var roles = new List<CoverageChildRole>();
        var positions = new List<(int Entry, int Cell)>();
        int entryIndex = 0;
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string entryContext = $"{context}.{Member}[{entryIndex}]";
            HostCoverageMapJson.Members(entry, entryContext, "mesh", "primitive", "triangle", "level", "cells", "role");
            int mesh = HostCoverageMapJson.Integer(entry, "mesh", entryContext);
            int primitive = HostCoverageMapJson.Integer(entry, "primitive", entryContext);
            int triangle = HostCoverageMapJson.Integer(entry, "triangle", entryContext);
            int level = HostCoverageMapJson.Integer(entry, "level", entryContext);
            JsonElement cells = HostCoverageMapJson.BoundedArray(entry, "cells", entryContext, HostCoverageMap.MaxChildCellsPerTriangle);
            if (cells.GetArrayLength() == 0)
                throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleCapacity, "A child role entry needs at least one cell.")
                { Path = $"{entryContext}.cells", RegionKey = regionKey, Count = 0, Limit = HostCoverageMap.MaxChildCellsPerTriangle };
            CoverageTriangleRole role = HostCoverageMapJson.Role(entry, entryContext, regionKey);
            int cellIndex = 0;
            foreach (JsonElement cell in cells.EnumerateArray())
            {
                if (cell.ValueKind != JsonValueKind.Number || !cell.TryGetInt32(out int value))
                    throw new HostCoverageDataException(HostCoverageRefusal.InvalidInteger, "Expected a 32-bit integer.")
                    { Path = $"{entryContext}.cells[{cellIndex}]" };
                roles.Add(new(mesh, primitive, triangle, level, value, role));
                positions.Add((entryIndex, cellIndex++));
            }
            entryIndex++;
        }
        return (roles.ToArray(), (index, field) =>
        {
            (int entry, int cell) = positions[index];
            return field switch
            {
                CoverageChildRoleField.Level => $"$.{Member}[{entry}].level",
                CoverageChildRoleField.Cell => $"$.{Member}[{entry}].cells[{cell}]",
                CoverageChildRoleField.Role => $"$.{Member}[{entry}].role",
                _ => $"$.{Member}[{entry}]",
            };
        });
    }

    /// <summary>Writes the region's records as one entry per source triangle in ascending order, with cells ascending.
    /// Writes nothing for a region without child roles. Region and map rules give every cell of one triangle the same
    /// level and role, so grouping by triangle loses nothing.</summary>
    internal static void Write(Utf8JsonWriter writer, IReadOnlyList<CoverageChildRole> sorted)
    {
        if (sorted.Count == 0) return;
        writer.WriteStartArray(Member);
        for (int first = 0; first < sorted.Count;)
        {
            CoverageChildRole head = sorted[first];
            writer.WriteStartObject();
            writer.WriteNumber("mesh", head.Mesh);
            writer.WriteNumber("primitive", head.Primitive);
            writer.WriteNumber("triangle", head.Triangle);
            writer.WriteNumber("level", head.Level);
            writer.WriteStartArray("cells");
            int next = first;
            for (; next < sorted.Count && SameTriangle(sorted[next], head); next++) writer.WriteNumberValue(sorted[next].Cell);
            writer.WriteEndArray();
            writer.WriteString("role", HostCoverageMapJson.RoleText(head.Role));
            writer.WriteEndObject();
            first = next;
        }
        writer.WriteEndArray();
    }

    static bool SameTriangle(CoverageChildRole left, CoverageChildRole right) =>
        left.Mesh == right.Mesh && left.Primitive == right.Primitive && left.Triangle == right.Triangle;
}
