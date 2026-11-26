using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Tests.Attachments;

internal sealed class HangingPanelCut
{
    internal const int Rows = 65;
    internal const int Columns = 7;
    internal const int CenterColumn = 3;
    internal const int VerticesPerSkin = Rows * Columns;
    internal const float FittedRadius = HangingPanelFixture.LipRadius + .0025f;
    private static readonly float[] ColumnX = [-.24f, -.18f, -.12f, 0, .12f, .18f, .24f];

    internal Vector3[] Rest { get; } = new Vector3[2 * VerticesPerSkin];
    internal PanelTriangle[] Faces { get; }
    internal PanelMaterialEdge[] MaterialEdges { get; }
    internal PanelProbes Probes { get; }

    internal HangingPanelCut()
    {
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
        {
            float length = LengthAt(row);
            Vector3 midpoint = HangingPanelFixture.Seam + new Vector3(X(column), -length, Pleat(column, length));
            WriteSkins(Rest, row, column, midpoint, Vector3.UnitZ);
        }

        var faces = new List<PanelTriangle>();
        for (int row = 0; row < Rows - 1; row++)
        for (int column = 0; column < Columns - 1; column++)
        {
            int upperLeft = Index(row, column);
            int upperRight = Index(row, column + 1);
            int lowerRight = Index(row + 1, column + 1);
            int lowerLeft = Index(row + 1, column);
            AddQuad(faces, upperLeft, upperRight, lowerRight, lowerLeft, PanelFace.Lining);
            AddQuad(faces, Outer(upperRight), Outer(upperLeft), Outer(lowerLeft), Outer(lowerRight), PanelFace.Outer);
        }

        for (int column = 0; column < Columns - 1; column++)
        {
            Close(Index(0, column + 1), Index(0, column));
            Close(Index(Rows - 1, column), Index(Rows - 1, column + 1));
        }
        for (int row = 0; row < Rows - 1; row++)
        {
            Close(Index(row, 0), Index(row + 1, 0));
            Close(Index(row + 1, Columns - 1), Index(row, Columns - 1));
        }
        Faces = faces.ToArray();
        MaterialEdges = MapMaterialEdges();

        var upperSpan = new PanelMaterialPoint[31];
        for (int i = 0; i < upperSpan.Length; i++)
            upperSpan[i] = Center(i + 1);
        Probes = new PanelProbes(Point(0, 0), Center(0), Point(0, Columns - 1),
            Center(18), Center(48), Center(64), Index(36, CenterColumn), upperSpan);

        void Close(int first, int second) => AddQuad(faces, first, second, Outer(second), Outer(first), PanelFace.Rim);
    }

    private PanelMaterialEdge[] MapMaterialEdges()
    {
        var mapped = new HashSet<(int, int)>();
        var material = new List<PanelMaterialEdge>();
        foreach (PanelTriangle face in Faces)
        {
            if (face.Face != PanelFace.Lining) continue;
            Add(face.A, face.B);
            Add(face.B, face.C);
            Add(face.C, face.A);
        }
        return material.ToArray();

        void Add(int first, int second)
        {
            if (!mapped.Add((Math.Min(first, second), Math.Max(first, second)))) return;
            var a = new PanelMaterialPoint(first, Outer(first));
            var b = new PanelMaterialPoint(second, Outer(second));
            float length = Vector3.Distance(a.Read(Rest, Matrix4x4.Identity), b.Read(Rest, Matrix4x4.Identity));
            material.Add(new PanelMaterialEdge(a, b, length));
        }
    }

    internal Vector3[] Fit(in PanelDrape drape)
    {
        var fitted = new Vector3[Rest.Length];
        for (int row = 0; row < Rows; row++)
        {
            float length = LengthAt(row);
            (Vector3 center, Vector3 normal) = drape.AtLength(length);
            for (int column = 0; column < Columns; column++)
                WriteSkins(fitted, row, column, GuidePoint(center, column, length), normal);
        }
        return fitted;
    }

    internal static Vector3 GuidePoint(Vector3 center, int column, float length) =>
        center + new Vector3(X(column), 0, Pleat(column, length));

    internal static float LengthAt(int row) => row * (HangingPanelFixture.Length / (Rows - 1));
    internal static int Index(int row, int column) => row * Columns + column;
    internal static int Outer(int liningIndex) => liningIndex + VerticesPerSkin;
    private static float X(int column) => ColumnX[column];
    private static float Pleat(int column, float length) => column is 1 or 5 ? .006f * MathF.Min(length / .10f, 1) : 0;
    private static PanelMaterialPoint Center(int row) => Point(row, CenterColumn);

    private static PanelMaterialPoint Point(int row, int column)
    {
        int lining = Index(row, column);
        return new PanelMaterialPoint(lining, Outer(lining));
    }

    private static void WriteSkins(Vector3[] positions, int row, int column, Vector3 midpoint, Vector3 normal)
    {
        int lining = Index(row, column);
        Vector3 offset = normal * HangingPanelFixture.HalfThickness;
        positions[lining] = midpoint - offset;
        positions[Outer(lining)] = midpoint + offset;
    }

    private static void AddQuad(List<PanelTriangle> faces, int a, int b, int c, int d, PanelFace face)
    {
        faces.Add(new PanelTriangle(a, b, c, face));
        faces.Add(new PanelTriangle(a, c, d, face));
    }
}
