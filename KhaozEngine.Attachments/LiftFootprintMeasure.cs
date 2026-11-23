using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Measures the cells beneath an attachment and the fixed triangle heights above them at rest.</summary>
public static class LiftFootprintMeasure
{
    const double SampleSpacingMetres = 0.001;

    /// <summary>
    /// Sample every piece triangle at a maximum spacing of one millimetre along the barycentric grid.
    /// Body meshes follow the region map's part order. Supply one piece mesh per definition part with
    /// its <see cref="CoverageClaimMesh.PartId"/>. The piece meshes are in the body's rest frame and
    /// their indices follow material part order, then triangle order. A part with motion needs a
    /// swinging vertex mask. Swinging triangles record only cells, while fixed triangles record each
    /// cell's highest sample, clamped to zero and rounded up to 0.01 mm. Every body triangle referenced
    /// by those cells contributes its outward face normal.
    /// </summary>
    /// <exception cref="ArgumentException">A nonempty swinging mask differs in length from its mesh's positions.</exception>
    /// <exception cref="CoverageClaimDataException">The geometry or claim breaks a rule, a triangle's
    /// sampling grid exceeds the index range, or a fixed sample stands more than 0.25 metres above the body.</exception>
    /// <exception cref="LiftFootprintDataException">The body triangle count cannot be represented by the footprint format.</exception>
    public static LiftFootprint Measure(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body,
        AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes) =>
        MeasureWithBodyIndex(bodyMeshes, body, piece, pieceMeshes, out _);

    internal static LiftFootprint MeasureWithBodyIndex(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body,
        AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes, out BodyTriangleIndex index)
    {
        CoverageClaimValidation.CheckArguments(bodyMeshes, body, piece, pieceMeshes);
        CoverageClaimValidation.CheckBody(bodyMeshes, body);
        CoverageClaimValidation.CheckCoveredRegions(body, piece);
        foreach (RegionStandoff entry in piece.Standoff)
            CoverageClaimValidation.ResolveRegion(body, entry.Region);

        Dictionary<uint, int> pieceMeshIndices = CoverageClaimSwingEvidence.ResolveFootprintMeshes(piece, pieceMeshes);
        for (int mesh = 0; mesh < pieceMeshes.Count; mesh++)
            CoverageClaimValidation.CheckPieceMesh(pieceMeshes, mesh);

        long triangleCount = 0;
        for (int part = 0; part < body.Parts.Count; part++)
            triangleCount += body.TriangleCount(part);
        LiftFootprint.CheckBodyTriangleCount(triangleCount);

        index = new BodyTriangleIndex(bodyMeshes, body);
        var parts = new LiftFootprintPart[piece.Parts.Count];
        var cells = new Dictionary<int, double>();
        var keys = new List<int>();
        var referenced = new HashSet<int>();
        for (int i = 0; i < parts.Length; i++)
        {
            AttachmentPartDefinition part = piece.Parts[i];
            int meshIndex = pieceMeshIndices[part.Id];
            parts[i] = MeasurePart(index, part, pieceMeshes[meshIndex], meshIndex, cells, keys, referenced);
        }
        var normals = index.FaceNormals(referenced).OrderBy(face => face.Key)
            .Select(face => new LiftFootprintBodyNormal(face.Key, face.Value));
        return new LiftFootprint(body.Sha256, (int)triangleCount, parts, normals);
    }

    static LiftFootprintPart MeasurePart(BodyTriangleIndex index, AttachmentPartDefinition part, CoverageClaimMesh mesh,
        int meshIndex, Dictionary<int, double> cells, List<int> keys, HashSet<int> referenced)
    {
        ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        ReadOnlySpan<bool> swinging = mesh.Swinging.Span;
        var builder = new LiftFootprintPart.Builder();
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = (int)indices[i];
            int b = (int)indices[i + 1];
            int c = (int)indices[i + 2];
            bool moves = !swinging.IsEmpty && (swinging[a] || swinging[b] || swinging[c]);
            cells.Clear();
            if (index.HasTriangles)
                SampleTriangle(index, new(positions[a]), new(positions[b]), new(positions[c]), moves, part, meshIndex, i / 3, cells);

            keys.Clear();
            keys.AddRange(cells.Keys);
            keys.Sort();
            int previousFace = -1;
            foreach (int key in keys)
            {
                int face = key / BodyCell.CellsPerTriangle;
                if (face != previousFace)
                    referenced.Add(face);
                previousFace = face;
                builder.AddCell(key);
                if (!moves)
                    builder.AddHeight(LiftFootprintTriangle.RoundHeightUp(cells[key]));
            }
            builder.EndTriangle(moves);
        }
        return builder.Build(part.Id);
    }

    static void SampleTriangle(BodyTriangleIndex index, ClaimPoint a, ClaimPoint b, ClaimPoint c, bool swinging,
        AttachmentPartDefinition part, int meshIndex, int triangle, Dictionary<int, double> cells)
    {
        ClaimPoint ab = b - a;
        ClaimPoint ac = c - a;
        ClaimPoint bc = c - b;
        double longest = Math.Sqrt(Math.Max(ab.LengthSquared, Math.Max(ac.LengthSquared, bc.LengthSquared)));
        double divisions = Math.Max(1, Math.Ceiling(longest / SampleSpacingMetres));
        if (divisions >= int.MaxValue)
            throw new CoverageClaimDataException(CoverageClaimRefusal.FootprintSamplingGridTooLarge,
                $"Piece part {part.Id}, triangle {triangle}, has too many footprint sampling divisions.")
            {
                MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = meshIndex, PartId = part.Id,
                MeshKey = part.Mesh.Key, TriangleIndex = triangle, LongestEdgeMetres = longest,
            };

        double maxHeight = LiftFootprintTriangle.ToMetres(LiftFootprintTriangle.MaxHeightUnits);
        int n = (int)divisions;
        for (int i = 0; i <= n; i++)
        {
            int last = n - i;
            ClaimPoint row = i == n ? b : a + ab * (i / divisions);
            for (int j = 0; j <= last; j++)
            {
                // Use the original corners and interpolate the BC edge from its own endpoints.
                ClaimPoint point = j == 0 ? row : j == n ? c
                    : j == last ? b + bc * (j / divisions) : row + ac * (j / divisions);
                BodyTriangleHit hit = index.Nearest(point);
                int key = BodyCell.At(hit.Triangle, hit.W1, hit.W2).Key;
                if (swinging)
                {
                    cells.TryAdd(key, 0);
                    continue;
                }

                double height = Math.Max(0, hit.HeightMetres);
                if (height > maxHeight)
                    throw new CoverageClaimDataException(CoverageClaimRefusal.FootprintHeightAboveLimit,
                        $"Piece part {part.Id}, triangle {triangle}, measures {height} metres above the body, beyond the {maxHeight} metre limit.")
                    {
                        MeshKind = CoverageClaimMeshKind.Piece, MeshIndex = meshIndex, PartId = part.Id,
                        MeshKey = part.Mesh.Key, TriangleIndex = triangle, HeightMetres = height,
                    };

                if (!cells.TryGetValue(key, out double prior) || height > prior)
                    cells[key] = height;
            }
        }
    }
}
