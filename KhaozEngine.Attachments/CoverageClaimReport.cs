using System;
using System.Collections.Generic;

namespace KhaozEngine.Attachments;

/// <summary>One body triangle: its region map part and its triangle index within that part.</summary>
public readonly record struct CoverageClaimTriangle(int PartIndex, int TriangleIndex);

/// <summary>What <see cref="CoverageClaimCheck.Verify"/> found for one region the piece claims to cover.</summary>
public sealed class CoverageClaimRegion
{
    internal CoverageClaimRegion(string name, int triangleCount, int degenerateCount, CoverageClaimTriangle[] uncovered)
    {
        Name = name;
        TriangleCount = triangleCount;
        DegenerateCount = degenerateCount;
        Uncovered = Array.AsReadOnly(uncovered);
    }

    /// <summary>The region's name in the body's region map.</summary>
    public string Name { get; }

    /// <summary>Every body triangle tagged with the region, degenerate ones included.</summary>
    public int TriangleCount { get; }

    /// <summary>The region's zero area triangles. They have no outward normal, so they are never uncovered.</summary>
    public int DegenerateCount { get; }

    /// <summary>The number of <see cref="Uncovered"/> triangles.</summary>
    public int UncoveredCount => Uncovered.Count;

    /// <summary>The region's triangles the piece does not lie over, in part order then triangle order.</summary>
    public IReadOnlyList<CoverageClaimTriangle> Uncovered { get; }
}

/// <summary>The result of checking a piece's coverage claim against a body at the rest pose.</summary>
public sealed class CoverageClaimReport
{
    internal CoverageClaimReport(CoverageClaimRegion[] regions)
    {
        Regions = Array.AsReadOnly(regions);
        bool honest = true;
        foreach (CoverageClaimRegion region in regions)
            honest &= region.UncoveredCount == 0;
        Honest = honest;
    }

    /// <summary>Each region the piece claims, in the body region map's region order. Empty for a piece with no layer.</summary>
    public IReadOnlyList<CoverageClaimRegion> Regions { get; }

    /// <summary>True when no claimed triangle is uncovered.</summary>
    public bool Honest { get; }
}
