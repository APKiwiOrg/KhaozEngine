using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>The difference between a measured part and its declaration.</summary>
public enum LiftFootprintPartMismatchReason
{
    /// <summary>A measured definition part has no declared footprint.</summary>
    MissingPart,

    /// <summary>A declared footprint part does not exist in the measured definition.</summary>
    ExtraPart,

    /// <summary>A part's declared and measured triangle counts differ.</summary>
    TriangleCountMismatch,

    /// <summary>A measured fixed triangle is declared to be swinging.</summary>
    FixedDeclaredSwinging,

    /// <summary>A measured swinging triangle is declared to be fixed.</summary>
    SwingingDeclaredFixed,
}

/// <summary>A part or triangle whose declared shape or motion differs from the measurement.</summary>
/// <param name="PartId">The attachment definition's part ID.</param>
/// <param name="Reason">The kind of mismatch.</param>
/// <param name="TriangleIndex">The zero-based triangle index in upload order for a triangle-specific finding, otherwise null.</param>
public readonly record struct LiftFootprintPartMismatch(uint PartId, LiftFootprintPartMismatchReason Reason, int? TriangleIndex);

/// <summary>A measured footprint cell missing from a triangle's declaration.</summary>
/// <param name="PartId">The attachment definition's part ID.</param>
/// <param name="TriangleIndex">The zero-based triangle index in upload order.</param>
/// <param name="Cell">The missing body cell.</param>
public readonly record struct LiftFootprintUncoveredCell(uint PartId, int TriangleIndex, BodyCell Cell);

/// <summary>A body cell whose declared fixed height falls short of its measurement.</summary>
/// <param name="PartId">The attachment definition's part ID.</param>
/// <param name="TriangleIndex">The zero-based triangle index in upload order.</param>
/// <param name="Cell">The body cell with the understated height.</param>
/// <param name="MeasuredHeightMetres">The measured height rounded up to a footprint unit, in metres.</param>
/// <param name="DeclaredHeightMetres">The declared height in metres.</param>
public readonly record struct LiftFootprintUnderstatedCell(uint PartId, int TriangleIndex, BodyCell Cell,
    double MeasuredHeightMetres, double DeclaredHeightMetres);

/// <summary>A declared body face normal that differs from its measurement.</summary>
/// <param name="BodyTriangleIndex">The zero-based triangle index across the body map's parts.</param>
/// <param name="MeasuredNormal">The measured face normal, or null for a degenerate triangle.</param>
/// <param name="DeclaredNormal">The face normal stored in the footprint.</param>
public readonly record struct LiftFootprintNormalMismatch(int BodyTriangleIndex, Vector3? MeasuredNormal, Vector3 DeclaredNormal);

/// <summary>The findings from re-measuring a declared lift footprint over its body at rest.</summary>
public sealed class LiftFootprintReport
{
    internal LiftFootprintReport(bool bodyMatches, List<LiftFootprintPartMismatch> partMismatches,
        List<LiftFootprintUncoveredCell> uncoveredCells, List<LiftFootprintUnderstatedCell> understatedCells,
        List<LiftFootprintNormalMismatch> normalMismatches)
    {
        BodyMatches = bodyMatches;
        PartMismatches = ImmutableArray.CreateRange(partMismatches);
        UncoveredCells = ImmutableArray.CreateRange(uncoveredCells);
        UnderstatedCells = ImmutableArray.CreateRange(understatedCells);
        NormalMismatches = ImmutableArray.CreateRange(normalMismatches);
    }

    /// <summary>Whether the declaration names the measured body's map digest and triangle count.</summary>
    public bool BodyMatches { get; }

    /// <summary>Parts or triangles whose presence, triangle count or swinging status differs.</summary>
    public IReadOnlyList<LiftFootprintPartMismatch> PartMismatches { get; }

    /// <summary>Every measured cell missing from a matching part and triangle. Empty if the body differs.</summary>
    public IReadOnlyList<LiftFootprintUncoveredCell> UncoveredCells { get; }

    /// <summary>Every shared cell whose declared fixed height is too low. Empty if the body differs.</summary>
    public IReadOnlyList<LiftFootprintUnderstatedCell> UnderstatedCells { get; }

    /// <summary>Every stored face normal that differs from measurement. Empty if the body differs.</summary>
    public IReadOnlyList<LiftFootprintNormalMismatch> NormalMismatches { get; }

    /// <summary>Whether the body matches and the check found no mismatches, missing cells or understated heights.</summary>
    public bool IsHonest => BodyMatches && PartMismatches.Count == 0 && UncoveredCells.Count == 0 &&
        UnderstatedCells.Count == 0 && NormalMismatches.Count == 0;
}
