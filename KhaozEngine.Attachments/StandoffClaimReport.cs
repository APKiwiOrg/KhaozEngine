using System;
using System.Collections.Generic;

namespace KhaozEngine.Attachments;

/// <summary>The measurements for one region the piece's fixed vertices fit over.</summary>
/// <param name="Name">The region's name in the body map.</param>
/// <param name="HighestHeightMetres">The largest signed distance from a fixed vertex to its nearest body point.</param>
/// <param name="VertexCount">The number of fixed vertices assigned to the region.</param>
/// <param name="DeclaredStandoffMetres">The piece's declared standoff in metres, if present.</param>
public readonly record struct StandoffClaimRegion(string Name, double HighestHeightMetres, int VertexCount, float? DeclaredStandoffMetres);

/// <summary>The result of checking a piece's standoff claim against a body at the rest pose.</summary>
public sealed class StandoffClaimReport
{
    internal StandoffClaimReport(StandoffClaimRegion[] regions)
    {
        Regions = Array.AsReadOnly(regions);
        var understated = new List<string>();
        var undeclared = new List<string>();
        foreach (StandoffClaimRegion region in regions)
        {
            if (region.DeclaredStandoffMetres is float declared)
            {
                if (region.HighestHeightMetres > declared)
                    understated.Add(region.Name);
            }
            else if (region.HighestHeightMetres > 0)
            {
                undeclared.Add(region.Name);
            }
        }
        UnderstatedRegions = understated.AsReadOnly();
        UndeclaredRegions = undeclared.AsReadOnly();
    }

    /// <summary>Regions with measured vertices, in the body region map's region order.</summary>
    public IReadOnlyList<StandoffClaimRegion> Regions { get; }

    /// <summary>Regions whose highest vertex exceeds the declared standoff, in body region map order.</summary>
    public IReadOnlyList<string> UnderstatedRegions { get; }

    /// <summary>Regions with a positive height and no declared standoff, in body region map order.</summary>
    public IReadOnlyList<string> UndeclaredRegions { get; }
}
