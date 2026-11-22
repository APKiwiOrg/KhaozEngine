using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

internal static class CoverageClaimFixtures
{
    internal const string Rig = "rig-v1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // A flat body in the z = 0 plane facing +z: "upper" is the square x 0 to 1, "lower" the square x 1 to 2, two
    // triangles each. The map lists upper first, the reverse of ordinal order, so report order is the map's.
    internal static readonly Vector3[] FlatPositions =
        [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(0, 1, 0), new(1, 1, 0), new(2, 1, 0)];
    internal static readonly uint[] FlatIndices = [0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4];
    internal static readonly BodyRegionMap FlatMap = new(Rig, ["upper", "lower"], [[new RegionRun(0, 2), new RegionRun(1, 2)]]);
    internal static readonly CoverageClaimMesh[] FlatBody = [new(FlatPositions, FlatIndices)];

    public static TheoryData<string, CoverageClaimRefusal> InvalidGeometry => new()
    {
        { "a second body mesh", CoverageClaimRefusal.BodyMeshCountMismatch },
        { "a body triangle short", CoverageClaimRefusal.BodyTriangleCountMismatch },
        { "a covered region the body lacks", CoverageClaimRefusal.RegionUnknown },
        { "a body index count off three", CoverageClaimRefusal.IndexCountNotTriangles },
        { "a piece index count off three", CoverageClaimRefusal.IndexCountNotTriangles },
        { "a body index past its positions", CoverageClaimRefusal.IndexOutOfRange },
        { "a piece index past its positions", CoverageClaimRefusal.IndexOutOfRange },
        { "a body position not a number", CoverageClaimRefusal.PositionNotFinite },
        { "a piece position infinite", CoverageClaimRefusal.PositionNotFinite },
    };

    // The passing flat body check, with exactly one rule broken when rule is not null.
    internal static (CoverageClaimMesh[] BodyMeshes, AttachmentSetDefinition Piece, CoverageClaimMesh[] PieceMeshes)
        GeometryInputs(string? rule)
    {
        Vector3[] bodyPositions = (Vector3[])FlatPositions.Clone();
        uint[] bodyIndices = (uint[])FlatIndices.Clone();
        CoverageClaimMesh plate = PlateOverUpper(0.01f);
        Vector3[] piecePositions = plate.Positions.ToArray();
        uint[] pieceIndices = plate.Indices.ToArray();
        AttachmentSetDefinition piece = Piece("upper");
        int bodyMeshes = 1;
        switch (rule)
        {
            case "a second body mesh": bodyMeshes = 2; break;
            case "a body triangle short": bodyIndices = bodyIndices[..9]; break;
            case "a covered region the body lacks": piece = Piece("upper", "middle"); break;
            case "a body index count off three": bodyIndices = bodyIndices[..11]; break;
            case "a piece index count off three": pieceIndices = pieceIndices[..5]; break;
            case "a body index past its positions": bodyIndices[4] = 99; break;
            case "a piece index past its positions": pieceIndices[4] = 99; break;
            case "a body position not a number": bodyPositions[2] = new(float.NaN, 0, 0); break;
            case "a piece position infinite": piecePositions[2] = new(0, float.PositiveInfinity, 0); break;
        }
        CoverageClaimMesh body = new(bodyPositions, bodyIndices);
        return (Enumerable.Repeat(body, bodyMeshes).ToArray(), piece, [new(piecePositions, pieceIndices)]);
    }

    // A plate over the upper square only, wide enough to cover its edges, at the given height above the skin. Its
    // triangles face away from the skin unless facingTheSkin, the way a garment's inner surface is wound.
    internal static CoverageClaimMesh PlateOverUpper(float height, bool facingTheSkin = false, float right = 1)
    {
        Vector3[] positions = [new(-1, -1, height), new(right, -1, height), new(right, 2, height), new(-1, 2, height)];
        uint[] indices = facingTheSkin ? [0, 2, 1, 0, 3, 2] : [0, 1, 2, 0, 2, 3];
        return new(positions, indices);
    }

    internal static AttachmentSetDefinition Piece(params string[] covers) =>
        new([new RigidAttachmentPartDefinition(1, new("parts/rigid.glb", Hash), "root", Matrix4x4.Identity) { Region = covers[0] }],
            AttachmentLayer.Armour, covers);

    internal static AttachmentSetDefinition ChainedPiece()
    {
        string json = $$"""
            {"formatVersion":4,"layer":"garment","covers":["upper"],"parts":[
              {"id":12,"kind":"skinned","mesh":{"key":"parts/chain.glb","sha256":"{{Hash}}"},"regions":[["upper",2]],
               "motion":{"kind":"chains","chains":[{{AttachmentFixtures.Chain0Json}}],"links":[]} },
              {"id":3,"kind":"rigid","mesh":{"key":"parts/fixed.glb","sha256":"{{Hash}}"},
               "joint":"root","local":{{AttachmentFixtures.Identity}},"region":"lower"}]}
            """;
        return AttachmentSetJson.Parse(Encoding.UTF8.GetBytes(json), "piece.json");
    }
}
