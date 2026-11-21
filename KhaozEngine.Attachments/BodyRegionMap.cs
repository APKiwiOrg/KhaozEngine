using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace KhaozEngine.Attachments;

/// <summary>A run of <see cref="TriangleCount"/> consecutive triangles that all belong to region <see cref="Region"/>.</summary>
public readonly record struct RegionRun(int Region, int TriangleCount);

/// <summary>
/// Names a body's regions and tags every triangle of each skinned material part with one of them. Parts follow the
/// body GLB's skinned material part order and runs follow each part's triangle order. Adjacent runs of one region
/// merge, so two maps that tag every triangle alike share their canonical bytes and <see cref="Sha256"/>.
/// </summary>
public sealed class BodyRegionMap
{
    const string RigPrefix = "rig-v1:";

    readonly Dictionary<string, int> regionIndices;
    readonly int[] triangleCounts;

    /// <summary>Admit a map, refusing with <see cref="BodyRegionMapDataException"/> when a rule is broken.</summary>
    public BodyRegionMap(string rigIdentity, IEnumerable<string> regions, IEnumerable<IEnumerable<RegionRun>> parts)
    {
        ArgumentNullException.ThrowIfNull(rigIdentity);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(parts);
        if (!rigIdentity.StartsWith(RigPrefix, StringComparison.Ordinal)
            || !AttachmentSetDefinition.ValidHash(rigIdentity[RigPrefix.Length..]))
            throw new BodyRegionMapDataException(BodyRegionMapRefusal.RigIdentityInvalid, "rig must be a complete lowercase rig-v1 identity.")
            { RigIdentity = rigIdentity };

        string[] names = regions.ToArray();
        if (names.Length > RegionSet.MaxRegions)
            throw new BodyRegionMapDataException(BodyRegionMapRefusal.TooManyRegions, $"A body carries at most {RegionSet.MaxRegions} regions.")
            { RegionCount = names.Length };
        regionIndices = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
        for (int index = 0; index < names.Length; index++)
        {
            string name = names[index];
            if (string.IsNullOrWhiteSpace(name))
                throw new BodyRegionMapDataException(BodyRegionMapRefusal.RegionNameEmpty, $"regions[{index}]: a region name is required.")
                { RegionIndex = index, RegionName = name };
            if (!AttachmentText.IsWellFormedUtf16(name))
                throw new BodyRegionMapDataException(BodyRegionMapRefusal.RegionNameMalformedUtf16, $"regions[{index}]: a region name must contain well-formed UTF-16.")
                { RegionIndex = index, RegionName = name };
            if (!regionIndices.TryAdd(name, index))
                throw new BodyRegionMapDataException(BodyRegionMapRefusal.RegionNameDuplicate, $"regions[{index}]: region '{name}' is already declared.")
                { RegionIndex = index, RegionName = name };
        }

        var ownedParts = new List<IReadOnlyList<RegionRun>>();
        var counts = new List<int>();
        foreach (IEnumerable<RegionRun> part in parts)
        {
            int partIndex = ownedParts.Count;
            ArgumentNullException.ThrowIfNull(part, nameof(parts));
            var runs = new List<RegionRun>();
            long triangles = 0;
            int runIndex = 0;
            foreach (RegionRun run in part)
            {
                if (run.Region < 0 || run.Region >= names.Length)
                    throw new BodyRegionMapDataException(BodyRegionMapRefusal.RegionIndexOutOfRange, $"parts[{partIndex}] run {runIndex}: region {run.Region} is not declared.")
                    { PartIndex = partIndex, RunIndex = runIndex, RegionIndex = run.Region, RegionCount = names.Length };
                if (run.TriangleCount < 1)
                    throw new BodyRegionMapDataException(BodyRegionMapRefusal.RunEmpty, $"parts[{partIndex}] run {runIndex}: a run covers at least one triangle.")
                    { PartIndex = partIndex, RunIndex = runIndex };
                triangles += run.TriangleCount;
                if (triangles > int.MaxValue)
                    throw new BodyRegionMapDataException(BodyRegionMapRefusal.TriangleCountOverflow, $"parts[{partIndex}] run {runIndex}: runs exceed the Int32 triangle range.")
                    { PartIndex = partIndex, RunIndex = runIndex, ActualTriangleCount = triangles };
                if (runs.Count > 0 && runs[^1].Region == run.Region)
                    runs[^1] = runs[^1] with { TriangleCount = runs[^1].TriangleCount + run.TriangleCount };
                else runs.Add(run);
                runIndex++;
            }
            ownedParts.Add(Array.AsReadOnly(runs.ToArray()));
            counts.Add((int)triangles);
        }

        RigIdentity = rigIdentity;
        Regions = Array.AsReadOnly(names);
        Parts = ownedParts.AsReadOnly();
        triangleCounts = counts.ToArray();
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(BodyRegionMapJson.Write(this)));
    }

    /// <summary>The complete <c>rig-v1:</c> identity of the body this map tags.</summary>
    public string RigIdentity { get; }

    /// <summary>Region names in index order. A run's region is an index into this list.</summary>
    public IReadOnlyList<string> Regions { get; }

    /// <summary>Each skinned material part's runs, in triangle order.</summary>
    public IReadOnlyList<IReadOnlyList<RegionRun>> Parts { get; }

    /// <summary>The lowercase SHA-256 of the canonical bytes <see cref="BodyRegionMapJson.Write"/> produces.</summary>
    public string Sha256 { get; }

    /// <summary>Find a region's index by its ordinal name.</summary>
    public bool TryRegion(string name, out int index)
    {
        ArgumentNullException.ThrowIfNull(name);
        return regionIndices.TryGetValue(name, out index);
    }

    /// <summary>Write the region index of each triangle of <paramref name="part"/>, one byte per triangle.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The map has no such part.</exception>
    /// <exception cref="ArgumentException">The span length differs from the part's triangle count.</exception>
    public void WriteTriangleRegions(int part, Span<byte> regionPerTriangle)
    {
        int triangles = TriangleCount(part);
        if (regionPerTriangle.Length != triangles)
            throw new ArgumentException($"Part {part} has {triangles} triangles, not {regionPerTriangle.Length}.", nameof(regionPerTriangle));
        IReadOnlyList<RegionRun> runs = Parts[part];
        int offset = 0;
        for (int index = 0; index < runs.Count; index++)
        {
            RegionRun run = runs[index];
            regionPerTriangle.Slice(offset, run.TriangleCount).Fill((byte)run.Region);
            offset += run.TriangleCount;
        }
    }

    /// <summary>The number of triangles in <paramref name="part"/>, the sum of its runs.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The map has no such part.</exception>
    public int TriangleCount(int part)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(part);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(part, Parts.Count);
        return triangleCounts[part];
    }
}
