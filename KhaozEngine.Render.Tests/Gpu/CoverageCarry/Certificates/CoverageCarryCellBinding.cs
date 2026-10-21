using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageCarryCellBinding
{
    internal static string Inputs(CoverageRefinement t, CoverageCarryAnchor anchor, CoverageCommandPose[] commands)
    {
        using var data = new MemoryStream(); using var w = new BinaryWriter(data, Encoding.UTF8, true);
        w.Write("F0-E2-inputs-v1"); w.Write(t.Source.SourceSha256);
        w.Write(CoverageCostGeometry.Observe(t, commands.Length).Fingerprint);
        w.Write(MemoryMarshal.AsBytes(t.Source.SkinVertices));
        w.Write(MemoryMarshal.AsBytes(t.Source.Influences));
        w.Write(MemoryMarshal.AsBytes(t.Source.Vertices.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.Source.Triangles.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.Source.Occurrences.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.Source.Materials.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.Vertices.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.Triangles.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.SourceAliases.ToArray().AsSpan()));
        for (int g = 0; g < t.PositionGroupCount; g++)
            w.Write(MemoryMarshal.AsBytes(new[] { t.GetPositionRecipe(g) }.AsSpan()));
        w.Write(MemoryMarshal.AsBytes(anchor.Recession)); w.Write(MemoryMarshal.AsBytes(anchor.Representatives));
        foreach (var c in commands)
        {
            w.Write(c.Id); w.Write(c.EquipmentInstanceId); w.Write(c.RegionIndex); w.Write(c.Support.Identity);
            Matrix(w, c.EquipmentWorld); Matrix(w, c.CoverageWorld);
            foreach (float s in new[] { c.MinZ, c.MaxZ, c.CollarWidth, c.Clearance, c.MaxWorldRecession, c.TransitionWeight }) w.Write(s);
        }
        w.Flush(); return Convert.ToHexStringLower(SHA256.HashData(data.ToArray()));
    }

    internal static string Domain(CoverageCarryCell cell)
    {
        using var data = new MemoryStream(); using var w = new BinaryWriter(data, Encoding.UTF8, true);
        w.Write(cell.Binding); w.Write(cell.Radius); w.Write(cell.ReserveWorld); w.Write(cell.Motion);
        foreach (var matrix in cell.Center) Matrix(w, matrix);
        foreach (var p in cell.Planes)
        { w.Write(p.Triangle); w.Write(p.Other); w.Write(p.Command); w.Write(p.Axis.X); w.Write(p.Axis.Y); w.Write(p.Axis.Z); }
        w.Flush(); return Convert.ToHexStringLower(SHA256.HashData(data.ToArray()));
    }

    static void Matrix(BinaryWriter w, Matrix4x4 m)
    {
        w.Write(MemoryMarshal.AsBytes(new[] { m }.AsSpan()));
    }

    internal static int ProofBytes(CoverageCarryCell cell) => Proof(cell).Length;
    internal static byte[] Proof(CoverageCarryCell cell) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "public-f0-continuous-cell/1",
        cell.Motion,
        cell.Binding,
        cell.DomainDigest,
        cell.Center,
        cell.Radius,
        cell.ReserveWorld,
        scalars = cell.Anchor.Recession.ToArray(),
        representatives = cell.Anchor.Representatives.ToArray(),
        cell.Planes,
        commands = cell.Commands.Select(c => new
        {
            c.Id,
            c.EquipmentInstanceId,
            c.RegionIndex,
            support = c.Support.Identity,
            c.EquipmentWorld,
            c.CoverageWorld,
            c.MinZ,
            c.MaxZ,
            c.CollarWidth,
            c.Clearance,
            c.MaxWorldRecession,
            c.TransitionWeight
        }),
    }, new JsonSerializerOptions { IncludeFields = true });

    internal static bool Admits(CoverageCarryCell cell, Matrix4x4[] palette, CoverageCommandPose[] commands, int layer = 0)
    {
        if (layer != 0 || palette.Length != cell.Center.Length || !float.IsFinite(cell.Radius) || cell.Radius < 0
            || commands.Any(c => !ReferenceEquals(cell.Anchor.Topology, c.Topology))
            || commands.Any(c => c.TransitionWeight != 1 || c.CoverageWorld.M31 != 0 || c.CoverageWorld.M32 != 0)
            || commands.Length != cell.Commands.Length || Inputs(cell.Anchor.Topology, cell.Anchor, commands) != cell.Binding
            || Domain(cell) != cell.DomainDigest) return false;
        for (int k = 0; k < palette.Length; k++)
        {
            if (!CoverageAffine.TryFrom(palette[k], out _)) return false;
            float[] a = CoverageCarryEnclosure.Coefficients(palette[k]), b = CoverageCarryEnclosure.Coefficients(cell.Center[k]);
            for (int j = 0; j < a.Length; j++)
                if (j < 9 ? BitConverter.SingleToInt32Bits(a[j]) != BitConverter.SingleToInt32Bits(b[j])
                    : Math.Abs((double)a[j] - b[j]) > cell.Radius) return false;
        }
        return true;
    }
}
