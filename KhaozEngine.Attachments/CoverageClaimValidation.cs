using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

internal static class CoverageClaimValidation
{
    internal static void CheckArguments(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body,
        AttachmentSetDefinition piece, IReadOnlyList<CoverageClaimMesh> pieceMeshes)
    {
        ArgumentNullException.ThrowIfNull(bodyMeshes);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(pieceMeshes);
    }

    internal static void CheckBody(IReadOnlyList<CoverageClaimMesh> bodyMeshes, BodyRegionMap body)
    {
        if (bodyMeshes.Count != body.Parts.Count)
            throw new CoverageClaimDataException(CoverageClaimRefusal.BodyMeshCountMismatch,
                $"The region map has {body.Parts.Count} parts, not {bodyMeshes.Count} body meshes.")
            { ExpectedMeshCount = body.Parts.Count, ActualMeshCount = bodyMeshes.Count };
        for (int part = 0; part < bodyMeshes.Count; part++)
        {
            CoverageClaimMesh mesh = bodyMeshes[part];
            mesh.ValidateSwingingMask(nameof(bodyMeshes), part);
            CheckMesh(CoverageClaimMeshKind.Body, part, mesh);
            int triangles = mesh.Indices.Length / 3;
            int expected = body.TriangleCount(part);
            if (triangles != expected)
                throw new CoverageClaimDataException(CoverageClaimRefusal.BodyTriangleCountMismatch,
                    $"Body mesh {part} has {triangles} triangles, not the region map part's {expected}.")
                { MeshKind = CoverageClaimMeshKind.Body, MeshIndex = part, ExpectedTriangleCount = expected, ActualTriangleCount = triangles };
        }
    }

    internal static RegionSet CheckCoveredRegions(BodyRegionMap body, AttachmentSetDefinition piece)
    {
        RegionSet claimed = RegionSet.Empty;
        foreach (string name in piece.Covers)
            claimed = claimed.With(ResolveRegion(body, name));
        return claimed;
    }

    internal static int ResolveRegion(BodyRegionMap body, string name) =>
        body.TryRegion(name, out int region)
            ? region
            : throw new CoverageClaimDataException(CoverageClaimRefusal.RegionUnknown, $"The body has no region '{name}'.") { RegionName = name };

    internal static CoverageClaimMesh CheckPieceMesh(IReadOnlyList<CoverageClaimMesh> pieceMeshes, int meshIndex)
    {
        CoverageClaimMesh mesh = pieceMeshes[meshIndex];
        mesh.ValidateSwingingMask(nameof(pieceMeshes), meshIndex);
        CheckMesh(CoverageClaimMeshKind.Piece, meshIndex, mesh);
        return mesh;
    }

    static void CheckMesh(CoverageClaimMeshKind kind, int meshIndex, CoverageClaimMesh mesh)
    {
        ReadOnlySpan<Vector3> positions = mesh.Positions.Span;
        ReadOnlySpan<uint> indices = mesh.Indices.Span;
        if (indices.Length % 3 != 0)
            throw new CoverageClaimDataException(CoverageClaimRefusal.IndexCountNotTriangles, $"{kind} mesh {meshIndex}: {indices.Length} indices is not a whole number of triangles.")
            { MeshKind = kind, MeshIndex = meshIndex, IndexCount = indices.Length };
        for (int position = 0; position < indices.Length; position++)
            if (indices[position] >= (uint)positions.Length)
                throw new CoverageClaimDataException(CoverageClaimRefusal.IndexOutOfRange, $"{kind} mesh {meshIndex}: index {position} names vertex {indices[position]} of {positions.Length}.")
                { MeshKind = kind, MeshIndex = meshIndex, IndexPosition = position, VertexIndex = indices[position], VertexCount = positions.Length };
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            Vector3 value = positions[vertex];
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                throw new CoverageClaimDataException(CoverageClaimRefusal.PositionNotFinite, $"{kind} mesh {meshIndex}: vertex {vertex} is not finite.")
                { MeshKind = kind, MeshIndex = meshIndex, VertexIndex = (uint)vertex };
        }
    }
}
