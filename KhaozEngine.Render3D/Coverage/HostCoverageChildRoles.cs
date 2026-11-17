using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Render3D;

/// <summary>One overridden cell of a region triangle. <see cref="Triangle"/> is a source triangle ordinal in
/// (<see cref="Mesh"/>, <see cref="Primitive"/>) that the region owns. <see cref="Level"/> must equal that triangle's
/// effective refinement level, <see cref="Cell"/> indexes its lattice in <see cref="CoverageChildCells"/> numbering, and
/// <see cref="Role"/> must be the opposite of the parent range role.</summary>
public readonly record struct CoverageChildRole(int Mesh, int Primitive, int Triangle, int Level, int Cell, CoverageTriangleRole Role);

/// <summary>The member of one child role input entry a refusal path names.</summary>
internal enum CoverageChildRoleField { Entry, Level, Cell, Role }

/// <summary>Region and map rules for child role overrides. Region checks run in input order so paths name the caller's
/// entries. Map checks need every region and the sorted refinement.</summary>
internal static class HostCoverageChildRoleRules
{
    internal static string ProgrammaticPath(int index, CoverageChildRoleField field) => field switch
    {
        CoverageChildRoleField.Level => $"$.childRoles[{index}].level",
        CoverageChildRoleField.Cell => $"$.childRoles[{index}].cell",
        CoverageChildRoleField.Role => $"$.childRoles[{index}].role",
        _ => $"$.childRoles[{index}]",
    };

    /// <summary>Validates one region's overrides against its sorted triangle ranges and returns them sorted by
    /// (Mesh, Primitive, Triangle, Cell) with the input index of each sorted entry.</summary>
    internal static (CoverageChildRole[] Sorted, int[] InputIndex) ValidateRegion(string key, IEnumerable<CoverageChildRole> childRoles,
        IReadOnlyList<HostCoverageRegionTriangles> sortedTriangles, Func<int, CoverageChildRoleField, string> path)
    {
        string context = $"Region '{key}'";
        CoverageChildRole[] owned = BoundedCollections.Snapshot(childRoles, HostCoverageMap.MaxChildCells, count =>
            new HostCoverageDataException(HostCoverageRefusal.ChildRoleCapacity, "Coverage collection exceeds its capacity.")
            { Path = "$.childRoles", RegionKey = key, Count = count, Limit = HostCoverageMap.MaxChildCells });
        for (int index = 0; index < owned.Length; index++)
        {
            CoverageChildRole entry = owned[index];
            if (entry.Role is not (CoverageTriangleRole.Core or CoverageTriangleRole.Collar))
                throw new HostCoverageDataException(HostCoverageRefusal.InvalidRole, $"{context}: unsupported child role {(int)entry.Role}.")
                { Path = path(index, CoverageChildRoleField.Role), RegionKey = key };
            if (entry.Level is < HostCoverageMap.MinRefinementLevel or > HostCoverageMap.MaxRefinementLevel)
                throw new HostCoverageDataException(HostCoverageRefusal.RefinementLevel,
                    $"Child role level must be {HostCoverageMap.MinRefinementLevel} to {HostCoverageMap.MaxRefinementLevel}.")
                { Path = path(index, CoverageChildRoleField.Level), Mesh = entry.Mesh, Primitive = entry.Primitive, FirstTriangle = entry.Triangle };
            if (entry.Cell < 0 || entry.Cell >= CoverageChildCells.CellCount(entry.Level))
                throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleCell, $"{context}: cell {entry.Cell} is outside level {entry.Level}.")
                {
                    Path = path(index, CoverageChildRoleField.Cell), RegionKey = key, Mesh = entry.Mesh, Primitive = entry.Primitive,
                    FirstTriangle = entry.Triangle, Level = entry.Level, Cell = entry.Cell,
                };
            if (FindRole(sortedTriangles, entry) is not { } parent)
                throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleOutsideRegion, $"{context}: child role triangle {entry.Triangle} is not in the region.")
                { Path = path(index, CoverageChildRoleField.Entry), RegionKey = key, Mesh = entry.Mesh, Primitive = entry.Primitive, FirstTriangle = entry.Triangle, Cell = entry.Cell };
            if (parent == entry.Role)
                throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleSameAsParent, $"{context}: child role equals the parent role of triangle {entry.Triangle}.")
                { Path = path(index, CoverageChildRoleField.Role), RegionKey = key, Mesh = entry.Mesh, Primitive = entry.Primitive, FirstTriangle = entry.Triangle, Cell = entry.Cell };
        }

        var cells = new HashSet<(int, int, int, int)>();
        for (int index = 0; index < owned.Length; index++)
        {
            CoverageChildRole entry = owned[index];
            if (!cells.Add((entry.Mesh, entry.Primitive, entry.Triangle, entry.Cell)))
                throw new HostCoverageDataException(HostCoverageRefusal.DuplicateChildRole, $"{context}: duplicate child role cell {entry.Cell} on triangle {entry.Triangle}.")
                { Path = path(index, CoverageChildRoleField.Cell), RegionKey = key, Mesh = entry.Mesh, Primitive = entry.Primitive, FirstTriangle = entry.Triangle, Cell = entry.Cell };
        }
        var perTriangle = new Dictionary<(int, int, int), int>();
        for (int index = 0; index < owned.Length; index++)
        {
            CoverageChildRole entry = owned[index];
            var triangle = (entry.Mesh, entry.Primitive, entry.Triangle);
            int count = perTriangle.GetValueOrDefault(triangle) + 1;
            perTriangle[triangle] = count;
            if (count > HostCoverageMap.MaxChildCellsPerTriangle)
                throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleCapacity,
                    $"{context}: more than {HostCoverageMap.MaxChildCellsPerTriangle} child cells on triangle {entry.Triangle}.")
                { Path = path(index, CoverageChildRoleField.Cell), RegionKey = key, Count = count, Limit = HostCoverageMap.MaxChildCellsPerTriangle };
        }
        var perLevel = new Dictionary<(int, int, int, int), int>();
        for (int index = 0; index < owned.Length; index++)
        {
            CoverageChildRole entry = owned[index];
            var triangle = (entry.Mesh, entry.Primitive, entry.Triangle, entry.Level);
            int count = perLevel.GetValueOrDefault(triangle) + 1;
            perLevel[triangle] = count;
            if (count == CoverageChildCells.CellCount(entry.Level))
                throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleCoversTriangle,
                    $"{context}: every cell of triangle {entry.Triangle} is overridden. Change the parent range role instead.")
                { Path = path(index, CoverageChildRoleField.Entry), RegionKey = key, Mesh = entry.Mesh, Primitive = entry.Primitive, FirstTriangle = entry.Triangle, Level = entry.Level };
        }

        int[] inputIndex = [.. Enumerable.Range(0, owned.Length)];
        Array.Sort(inputIndex, (left, right) => Compare(owned[left], owned[right]));
        return ([.. inputIndex.Select(index => owned[index])], inputIndex);
    }

    /// <summary>Checks the map total and each override level against the sorted, disjoint refinement ranges. Regions are
    /// in input order so paths name the caller's region.</summary>
    internal static void ValidateMap(IReadOnlyList<HostCoverageRegion> regions, IReadOnlyList<HostCoverageRefinement> sortedRefinement)
    {
        long total = regions.Sum(region => (long)region.ChildRoles.Count);
        if (total > HostCoverageMap.MaxChildCells)
            throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleCapacity, $"Regions override more than {HostCoverageMap.MaxChildCells} child cells.")
            { Path = "$.regions", Count = total, Limit = HostCoverageMap.MaxChildCells };
        for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            HostCoverageRegion region = regions[regionIndex];
            for (int index = 0; index < region.ChildRoles.Count; index++)
            {
                CoverageChildRole entry = region.ChildRoles[index];
                int level = EffectiveLevel(sortedRefinement, entry);
                if (entry.Level != level)
                    throw new HostCoverageDataException(HostCoverageRefusal.ChildRoleLevelMismatch,
                        $"Region '{region.Key}': child role level {entry.Level} differs from triangle {entry.Triangle} refinement level {level}.")
                    {
                        Path = $"$.regions[{regionIndex}]" + region.ChildRolePath(index, CoverageChildRoleField.Level)[1..],
                        RegionKey = region.Key, Mesh = entry.Mesh, Primitive = entry.Primitive, FirstTriangle = entry.Triangle, Level = entry.Level,
                    };
            }
        }
    }

    static int Compare(CoverageChildRole left, CoverageChildRole right) =>
        (left.Mesh, left.Primitive, left.Triangle, left.Cell).CompareTo((right.Mesh, right.Primitive, right.Triangle, right.Cell));

    static CoverageTriangleRole? FindRole(IReadOnlyList<HostCoverageRegionTriangles> sorted, CoverageChildRole entry)
    {
        int found = FindContaining(sorted.Count, index => sorted[index].Triangles, entry);
        return found < 0 ? null : sorted[found].Role;
    }

    static int EffectiveLevel(IReadOnlyList<HostCoverageRefinement> sorted, CoverageChildRole entry)
    {
        int found = FindContaining(sorted.Count, index => sorted[index].Triangles, entry);
        return found < 0 ? HostCoverageMap.MinRefinementLevel : sorted[found].Level;
    }

    /// <summary>Binary search over ranges sorted by source ordinal and disjoint. Returns the range holding the entry's
    /// triangle, or -1.</summary>
    static int FindContaining(int count, Func<int, CoverageTriangleRange> range, CoverageChildRole entry)
    {
        int low = 0, high = count - 1, candidate = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            CoverageTriangleRange probe = range(middle);
            if ((probe.Mesh, probe.Primitive, probe.FirstTriangle).CompareTo((entry.Mesh, entry.Primitive, entry.Triangle)) <= 0)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
                high = middle - 1;
        }
        if (candidate < 0) return -1;
        CoverageTriangleRange found = range(candidate);
        return found.Mesh == entry.Mesh && found.Primitive == entry.Primitive && (long)found.FirstTriangle + found.Count > entry.Triangle ? candidate : -1;
    }
}
