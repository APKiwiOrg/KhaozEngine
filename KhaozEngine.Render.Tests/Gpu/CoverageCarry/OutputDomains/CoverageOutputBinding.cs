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

internal static class CoverageOutputBinding
{
    internal static string Inputs(CoverageOutputCell c)
    {
        var t = c.Anchor.Topology;
        using var data = new MemoryStream(); using var w = new BinaryWriter(data, Encoding.UTF8, true);
        w.Write("carry-output-domain/1"); w.Write(t.Source.SourceSha256); w.Write(HostCoverageMapJson.SemanticSha256(t.Map));
        w.Write(MemoryMarshal.AsBytes(t.Source.SkinVertices));
        w.Write(MemoryMarshal.AsBytes(t.Source.Positions));
        w.Write(MemoryMarshal.AsBytes(t.Source.Influences));
        w.Write(MemoryMarshal.AsBytes(t.Indices));
        w.Write(MemoryMarshal.AsBytes(t.Vertices.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.Triangles.ToArray().AsSpan()));
        w.Write(MemoryMarshal.AsBytes(t.SourceAliases.ToArray().AsSpan()));
        for (int g = 0; g < t.PositionGroupCount; g++) w.Write(MemoryMarshal.AsBytes(new[] { t.GetPositionRecipe(g) }.AsSpan()));
        w.Write(MemoryMarshal.AsBytes(c.InverseBind.AsSpan()));
        w.Write(MemoryMarshal.AsBytes(c.Anchor.Recession)); w.Write(MemoryMarshal.AsBytes(c.Anchor.Representatives));
        var p = c.Command;
        w.Write(p.Id); w.Write(p.EquipmentInstanceId); w.Write(p.RegionIndex); w.Write(p.Support.Identity);
        w.Write(MemoryMarshal.AsBytes(new[] { p.Support.CoverageLocal, p.Support.SourceToCoverage }.AsSpan()));
        w.Write(MemoryMarshal.AsBytes(p.Support.Source.Positions));
        w.Write(MemoryMarshal.AsBytes(p.Support.Source.Triangles.ToArray().AsSpan()));
        foreach (float f in new[] { p.MinZ, p.MaxZ, p.CollarWidth, p.Clearance, p.MaxWorldRecession, p.TransitionWeight }) w.Write(f);
        w.Flush(); return Convert.ToHexStringLower(SHA256.HashData(data.ToArray()));
    }

    internal static string Domain(CoverageOutputCell c) => Convert.ToHexStringLower(SHA256.HashData(Proof(c with { Digest = "" })));
    internal static byte[] Proof(CoverageOutputCell c) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        c.Binding,
        c.Digest,
        c.GramDefect,
        c.Layer,
        c.Before,
        c.After,
        c.Copies,
        c.Planes,
        scalars = c.Anchor.Recession.ToArray(),
        representatives = c.Anchor.Representatives.ToArray()
    }, new JsonSerializerOptions { IncludeFields = true });
}
