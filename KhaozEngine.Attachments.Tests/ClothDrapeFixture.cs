using System;
using System.Numerics;
using KhaozEngine.Attachments;

namespace KhaozEngine.Tests.Attachments;

// The same dense cut is reconstructed from proxy node positions for every sample.
internal sealed class ClothDrapeFixture
{
    internal const int Columns = 9;
    private static readonly int[] Rows = [0, 5, 8, 16, 21, 27, 32, 37, 43, 48, 53, 59, 64];
    private static readonly float[] Across = [-.24f, -.18f, -.12f, -.06f, 0, .06f, .12f, .18f, .24f];
    private static readonly Vector3 HalfAxis = HangingPanelFixture.LipHalfSpan * Vector3.UnitX;
    private readonly HangingPanelCut _cut = new();
    private readonly VertexEmbedding[] _embedding = new VertexEmbedding[HangingPanelCut.VerticesPerSkin];
    private readonly Vector3[] _surface = new Vector3[2 * HangingPanelCut.VerticesPerSkin];
    private readonly Vector3[] _normals = new Vector3[Rows.Length * Columns];
    private readonly Vector3 _fittedSeamNormal;

    internal ClothRig Rig { get; }
    internal Vector3[] Fitted { get; } = new Vector3[Rows.Length * Columns];

    internal ClothDrapeFixture()
    {
        HangingPanelPose neutral = HangingPanelFixture.PoseAt(0);
        var initial = new PanelDrape(HangingPanelFixture.Seam, neutral.Support.Translation, HangingPanelCut.FittedRadius);
        Vector3[] source = _cut.Fit(initial);
        _fittedSeamNormal = Vector3.Normalize(source[HangingPanelCut.Outer(0)] - source[0]);
        var rest = new Vector3[Fitted.Length];
        var coordinates = new Vector2[rest.Length];
        var faces = new ClothTriangle[(Rows.Length - 1) * (Columns - 1) * 2];
        int[] pins = new int[Columns];
        for (int row = 0; row < Rows.Length; row++)
        for (int column = 0; column < Columns; column++)
        {
            int node = row * Columns + column;
            float x = Across[column];
            rest[node] = CutPoint(_cut.Rest, Rows[row], x);
            Fitted[node] = CutPoint(source, Rows[row], x);
            coordinates[node] = new Vector2(x, HangingPanelCut.LengthAt(Rows[row]));
            if (row == 0) pins[column] = node;
            if (row + 1 == Rows.Length || column + 1 == Columns) continue;
            int cell = 2 * (row * (Columns - 1) + column);
            faces[cell] = new ClothTriangle(node, node + 1, node + Columns + 1);
            faces[cell + 1] = new ClothTriangle(node, node + Columns + 1, node + Columns);
        }

        Rig = ClothRig.Create(rest, coordinates, faces, pins, [], ClothDrapeTests.Woven);
        for (int row = 0; row < HangingPanelCut.Rows; row++)
        for (int column = 0; column < HangingPanelCut.Columns; column++)
        {
            int vertex = HangingPanelCut.Index(row, column);
            _embedding[vertex] = Embed(row, _cut.Rest[vertex].X, faces);
        }
    }

    internal void PosePins(in HangingPanelPose pose, Span<Vector3> target)
    {
        for (int column = 0; column < Columns; column++)
            target[column] = Vector3.Transform(Rig.RestPositions[column], pose.Chest);
    }

    internal static void PoseSupports(in HangingPanelPose pose, Span<ClothSupport> target)
    {
        var lip = new ColliderCapsule(-HalfAxis, HalfAxis, HangingPanelFixture.LipRadius);
        Vector3 recess = HangingPanelFixture.RecessCenter;
        var body = new ColliderCapsule(recess - HalfAxis, recess + HalfAxis, HangingPanelFixture.RecessRadius);
        target[0] = ClothSupport.Capsule(lip, Quaternion.Identity, pose.Support.Translation);
        target[1] = ClothSupport.Capsule(body, Quaternion.CreateFromRotationMatrix(pose.Chest), pose.Chest.Translation);
    }

    internal PresentedPanel Present(ReadOnlySpan<Vector3> positions, in HangingPanelPose pose, int recoveries)
    {
        Array.Clear(_normals);
        foreach (ClothTriangle face in Rig.Triangles)
        {
            Vector3 cross = Vector3.Cross(positions[face.C] - positions[face.A], positions[face.B] - positions[face.A]);
            _normals[face.A] += cross;
            _normals[face.B] += cross;
            _normals[face.C] += cross;
        }
        for (int node = 0; node < _normals.Length; node++)
            _normals[node] = node < Columns ? Vector3.TransformNormal(_fittedSeamNormal, pose.Chest) :
                Vector3.Normalize(_normals[node]);

        for (int i = 0; i < _embedding.Length; i++)
        {
            (ClothTriangle face, Vector3 weights) = _embedding[i];
            Vector3 midpoint = face.Blend(positions, weights);
            Vector3 normal = Vector3.Normalize(face.Blend(_normals, weights));
            Vector3 offset = normal * HangingPanelFixture.HalfThickness;
            _surface[i] = midpoint - offset;
            _surface[HangingPanelCut.Outer(i)] = midpoint + offset;
        }
        return new PresentedPanel(_surface, _cut.Faces, _cut.MaterialTriangles, _cut.Probes, recoveries);
    }

    private static Vector3 CutPoint(Vector3[] source, int row, float x)
    {
        int lower = HangingPanelCut.Index(row, 0);
        int last = lower + HangingPanelCut.Columns - 1;
        while (lower + 1 < last && source[lower + 1].X < x)
            lower++;
        float fraction = (x - source[lower].X) / (source[lower + 1].X - source[lower].X);
        return Vector3.Lerp(Midpoint(lower), Midpoint(lower + 1), fraction);

        Vector3 Midpoint(int i) => (source[i] + source[HangingPanelCut.Outer(i)]) * .5f;
    }

    private static VertexEmbedding Embed(int denseRow, float x, ClothTriangle[] faces)
    {
        int row = 0, col = 0;
        while (row + 1 < Rows.Length - 1 && Rows[row + 1] < denseRow) row++;
        while (col + 1 < Columns - 1 && Across[col + 1] < x) col++;
        float u = (x - Across[col]) / (Across[col + 1] - Across[col]);
        float v = (float)(denseRow - Rows[row]) / (Rows[row + 1] - Rows[row]);
        int cell = 2 * (row * (Columns - 1) + col);
        return u >= v ? new VertexEmbedding(faces[cell], new Vector3(1 - u, u - v, v)) :
            new VertexEmbedding(faces[cell + 1], new Vector3(1 - v, u, v - u));
    }

    private readonly record struct VertexEmbedding(ClothTriangle Face, Vector3 Weights);
}
