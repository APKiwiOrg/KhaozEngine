using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Checks a piece's regional standoff claim offline at the body's rest pose.</summary>
public static class StandoffClaimCheck
{
    /// <summary>
    /// Finds the nearest nondegenerate body triangle for each fixed piece vertex. It supplies the region and height,
    /// which is the signed distance in metres from its plane along its outward normal.
    /// Body meshes follow the region map's part order. Piece meshes are already in the body's rest frame.
    /// For a piece with motion, supply one mesh per definition part in any order with its
    /// <see cref="CoverageClaimMesh.PartId"/> and a swinging mask for each part with motion.
    /// </summary>
    /// <remarks>
    /// Triangles within one micrometre of the nearest distance compete by height. Equal heights choose the first
    /// triangle in body part and triangle order. A body without nondegenerate triangles yields no measurements.
    /// </remarks>
    /// <exception cref="ArgumentException">A nonempty swinging mask has a different length than its mesh's positions.</exception>
    /// <exception cref="CoverageClaimDataException">The geometry or the claim breaks a rule.</exception>
    public static StandoffClaimReport Verify(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body,
        AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes)
    {
        CoverageClaimValidation.CheckArguments(bodyMeshes, body, piece, pieceMeshes);
        CoverageClaimValidation.CheckBody(bodyMeshes, body);
        CoverageClaimValidation.CheckCoveredRegions(body, piece);

        var declared = new float?[body.Regions.Count];
        foreach (RegionStandoff entry in piece.Standoff)
            declared[CoverageClaimValidation.ResolveRegion(body, entry.Region)] = entry.Metres;

        CoverageClaimSwingEvidence.Validate(piece, pieceMeshes);
        for (int mesh = 0; mesh < pieceMeshes.Count; mesh++)
            CoverageClaimValidation.CheckPieceMesh(pieceMeshes, mesh);

        var triangles = new BodyTriangleIndex(bodyMeshes, body);
        if (!triangles.HasTriangles)
            return new StandoffClaimReport([]);

        var counts = new int[declared.Length];
        var highest = new double[declared.Length];
        foreach (CoverageClaimMesh mesh in pieceMeshes)
        {
            ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
            ReadOnlySpan<bool> swinging = mesh.Swinging.Span;
            for (int vertex = 0; vertex < positions.Length; vertex++)
            {
                if (!swinging.IsEmpty && swinging[vertex])
                    continue;

                BodyTriangleHit hit = triangles.Nearest(new ClaimPoint(positions[vertex]));
                if (counts[hit.Region] == 0 || hit.HeightMetres > highest[hit.Region])
                    highest[hit.Region] = hit.HeightMetres;
                counts[hit.Region]++;
            }
        }

        var regions = new List<StandoffClaimRegion>();
        for (int region = 0; region < counts.Length; region++)
            if (counts[region] > 0)
                regions.Add(new StandoffClaimRegion(body.Regions[region], highest[region], counts[region], declared[region]));
        return new StandoffClaimReport(regions.ToArray());
    }
}
