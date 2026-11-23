using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Checks whether an attachment's declared lift footprint covers its measured surface at rest.</summary>
public static class LiftFootprintCheck
{
    /// <summary>
    /// Re-measure the footprint using the mesh contract of <see cref="LiftFootprintMeasure.Measure"/>.
    /// Compare cells on triangles present in both footprints when the body matches. Report heights below
    /// the measurement where both triangles are fixed. Extra cells and higher heights are permitted.
    /// Compare every stored normal with the face normal of its triangle when the body matches.
    /// </summary>
    /// <exception cref="ArgumentException">A nonempty swinging mask differs in length from its mesh's positions.</exception>
    /// <exception cref="CoverageClaimDataException">The geometry or claim input breaks a measurement rule.</exception>
    /// <exception cref="LiftFootprintDataException">The body triangle count cannot be represented by the footprint format.</exception>
    public static LiftFootprintReport Verify(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body,
        AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes, LiftFootprint declared)
    {
        ArgumentNullException.ThrowIfNull(declared);
        LiftFootprint measured = LiftFootprintMeasure.MeasureWithBodyIndex(bodyMeshes, body, piece, pieceMeshes, out BodyTriangleIndex index);
        bool bodyMatches = declared.BodyRegionMapSha256 == measured.BodyRegionMapSha256
            && declared.BodyTriangleCount == measured.BodyTriangleCount;
        var mismatches = new List<LiftFootprintPartMismatch>();
        var uncovered = new List<LiftFootprintUncoveredCell>();
        var understated = new List<LiftFootprintUnderstatedCell>();

        foreach (LiftFootprintPart part in measured.Parts)
        {
            LiftFootprintPart? claim = declared.PartById(part.PartId);
            if (claim is null)
            {
                mismatches.Add(new(part.PartId, LiftFootprintPartMismatchReason.MissingPart, null));
                continue;
            }

            if (part.TriangleCount != claim.TriangleCount)
                mismatches.Add(new(part.PartId, LiftFootprintPartMismatchReason.TriangleCountMismatch, null));

            int count = Math.Min(part.TriangleCount, claim.TriangleCount);
            for (int triangle = 0; triangle < count; triangle++)
            {
                LiftFootprintTriangle actual = part.Triangles[triangle];
                LiftFootprintTriangle stated = claim.Triangles[triangle];
                if (actual.Swinging != stated.Swinging)
                    mismatches.Add(new(part.PartId, actual.Swinging ? LiftFootprintPartMismatchReason.SwingingDeclaredFixed
                        : LiftFootprintPartMismatchReason.FixedDeclaredSwinging, triangle));

                if (bodyMatches)
                    CompareCells(part.PartId, triangle, actual, stated, uncovered, understated);
            }
        }

        foreach (LiftFootprintPart claim in declared.Parts)
            if (measured.PartById(claim.PartId) is null)
                mismatches.Add(new(claim.PartId, LiftFootprintPartMismatchReason.ExtraPart, null));

        var normalMismatches = new List<LiftFootprintNormalMismatch>();
        if (bodyMatches && declared.BodyNormals.Count != 0)
            CompareNormals(declared, index, normalMismatches);

        return new LiftFootprintReport(bodyMatches, mismatches, uncovered, understated, normalMismatches);
    }

    static void CompareNormals(LiftFootprint declared, BodyTriangleIndex index, List<LiftFootprintNormalMismatch> mismatches)
    {
        Dictionary<int, Vector3> normals = index.FaceNormals(declared.BodyNormals.Select(entry => entry.BodyTriangleIndex).ToHashSet());
        foreach (LiftFootprintBodyNormal entry in declared.BodyNormals)
        {
            Vector3? measured = normals.TryGetValue(entry.BodyTriangleIndex, out Vector3 normal) ? normal : null;
            if (entry.Normal != measured)
                mismatches.Add(new(entry.BodyTriangleIndex, measured, entry.Normal));
        }
    }

    static void CompareCells(uint partId, int triangle, LiftFootprintTriangle actual, LiftFootprintTriangle stated,
        List<LiftFootprintUncoveredCell> uncovered, List<LiftFootprintUnderstatedCell> understated)
    {
        ReadOnlySpan<int> actualCells = actual.CellKeys;
        ReadOnlySpan<int> statedCells = stated.CellKeys;
        bool compareHeights = !actual.Swinging && !stated.Swinging;
        ReadOnlySpan<ushort> actualHeights = actual.HeightUnits;
        ReadOnlySpan<ushort> statedHeights = stated.HeightUnits;
        int declared = 0;
        for (int measured = 0; measured < actualCells.Length; measured++)
        {
            int key = actualCells[measured];
            while (declared < statedCells.Length && statedCells[declared] < key)
                declared++;

            if (declared == statedCells.Length || statedCells[declared] != key)
                uncovered.Add(new(partId, triangle, BodyCell.FromKey(key)));
            else if (compareHeights && actualHeights[measured] > statedHeights[declared])
                understated.Add(new(partId, triangle, BodyCell.FromKey(key), actual.HeightMetres(measured), stated.HeightMetres(declared)));
        }
    }
}
