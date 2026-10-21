using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

// Fixed public workload, separate from proof construction/checking. No result feeds a certificate margin.
internal static class CoverageCarryFalsifier
{
    internal static int Run(CoverageCarryCell cell)
    {
        var t = cell.Anchor.Topology; var scratch = new CoverageReferenceScratch(t);
        var geometry = new CoverageScratch(t); var result = new ModelVertex[t.VertexCount];
        var enclosure = new CoverageCarryEnclosure(cell);
        Vector3[]? first = null; bool differentPose = false;
        for (int sample = 0; sample < 321; sample++)
        {
            var palette = (Matrix4x4[])cell.Center.Clone();
            for (int k = 0; k < 2; k++)
                for (int j = 0; j < 3; j++)
                {
                    int axis = k * 3 + j;
                    float center = j == 0 ? palette[k].M41 : j == 1 ? palette[k].M42 : palette[k].M43;
                    var bounds = CoverageCarryFloat.Around(center, cell.Radius);
                    // All 64 corners, then a 257-point nonlinear curve with independent phase offsets.
                    float value = sample < 64 ? ((sample & (1 << axis)) == 0 ? bounds.Min : bounds.Max)
                        : center + cell.Radius * .99f * MathF.Sin((sample - 64) * MathF.Tau / 256 + axis * .73f);
                    CoverageCarryCertificateTests.SetTranslation(palette, k, j, value);
                }
            if (!CoverageCarryCellBinding.Admits(cell, palette, cell.Commands)) throw new InvalidOperationException("Falsifier left its input domain.");
            if (!CoverageCarryReference.TryCarry(t, palette, cell.Commands, cell.Anchor, scratch, result, out var failure))
                throw new InvalidOperationException($"Carry refused sample {sample}: {failure}");
            for (int v = 0; v < result.Length; v++)
            {
                var p = result[v].Position; var b = enclosure.After[v];
                if (first != null && p != first[v]) differentPose = true;
                if (p.X < b.X.Min || p.X > b.X.Max || p.Y < b.Y.Min || p.Y > b.Y.Max || p.Z < b.Z.Min || p.Z > b.Z.Max)
                    throw new InvalidOperationException($"RN32 enclosure missed sample {sample}, vertex {v}.");
            }
            if (first == null)
            {
                first = new Vector3[result.Length];
                for (int v = 0; v < result.Length; v++) first[v] = result[v].Position;
            }
            if (!CoverageCertification.ValidateGeometry(t, scratch.Before, result, Matrix4x4.Identity, cell.Commands, geometry, out failure))
                throw new InvalidOperationException($"Exact geometry falsified sample {sample}: {failure}");
        }
        if (!differentPose) throw new InvalidOperationException("Point-only geometry cannot pass the positive-domain gate.");
        return 321;
    }
}
