using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Render3D;

/// <summary>Cold topology/table compatibility. Selection is solely an opaque key lookup.</summary>
internal sealed class CoverageTableBinding
{
    readonly CoverageRefinement topology;
    internal readonly CoverageRecessionTable Table;
    internal static string ArithmeticSha256 => CoverageArithmeticContract.Sha256;

    internal CoverageTableBinding(CoverageRefinement topology, CoverageRecessionTable table)
    {
        this.topology = topology;
        Table = table;
        if (table.Identity.HostSha256 != topology.Source.SourceSha256
            || table.Identity.HostMapSha256 != HostCoverageMapJson.SemanticSha256(topology.Map)
            || table.Identity.RefinementSha256 != RefinementSha256(topology)
            || table.Identity.ArithmeticSha256 != ArithmeticSha256)
            throw new CoverageTableDataException(CoverageTableRefusal.HostIdentityMismatch, "Table identity differs from its host or carry arithmetic.")
            { Identity = topology.HostIdentity };
        if (table.PositionGroupCount != topology.PositionGroupCount)
            throw new CoverageTableDataException(CoverageTableRefusal.GroupCountMismatch, "Table groups differ from the host topology.")
            { Identity = topology.HostIdentity, Count = table.PositionGroupCount, Limit = topology.PositionGroupCount };
        for (int group = 0; group < table.PositionGroupCount; group++)
        {
            int representative = table.Representatives[group];
            if (representative >= topology.VertexCount) Refuse(CoverageTableRefusal.RepresentativeOutsideTopology);
            if (representative >= 0 && topology.Vertices[representative].PositionGroup != group)
                Refuse(CoverageTableRefusal.RepresentativeGroupMismatch);
            void Refuse(CoverageTableRefusal reason) => throw new CoverageTableDataException(reason, "Representative does not belong to its host group.")
            { Identity = topology.HostIdentity, GroupIndex = group, Representative = representative };
        }
    }

    internal bool MatchesRepresentatives(ReadOnlySpan<int> regions)
    {
        int[] representatives = new int[topology.PositionGroupCount];
        CoverageReferenceTopology.FillActiveRepresentatives(topology, regions, representatives, new());
        return Table.Representatives.SequenceEqual(representatives);
    }

    internal static string RefinementSha256(CoverageRefinement topology)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write("KhaozEngine.CoverageRefinement/1");
        writer.Write(topology.Source.SourceSha256);
        writer.Write(topology.VertexCount); writer.Write(topology.TriangleCount); writer.Write(topology.PositionGroupCount);
        foreach (var v in topology.Source.Vertices) { writer.Write(v.Mesh); writer.Write(v.Primitive); writer.Write(v.Vertex); writer.Write(v.Node); writer.Write(v.MaterialSlice); writer.Write(v.SliceVertex); }
        foreach (var t in topology.Source.Triangles) { writer.Write(t.Mesh); writer.Write(t.Primitive); writer.Write(t.Triangle); writer.Write(t.Node); writer.Write(t.MaterialSlice); writer.Write(t.A); writer.Write(t.B); writer.Write(t.C); }
        foreach (var v in topology.Vertices) { writer.Write(v.SourceTriangle); writer.Write(v.SourceVertex); writer.Write(v.A); writer.Write(v.B); writer.Write(v.C); writer.Write(v.Denominator); writer.Write(v.PositionGroup); writer.Write(v.ShadingGroup); writer.Write(v.RegionIndex); writer.Write(BitConverter.SingleToUInt32Bits(v.Strength)); }
        for (int i = 0; i < topology.PositionGroupCount; i++)
        { var r = topology.GetPositionRecipe(i); writer.Write(r.A); writer.Write(r.B); writer.Write(r.C); writer.Write(r.WeightA); writer.Write(r.WeightB); writer.Write(r.WeightC); writer.Write(r.Denominator); }
        foreach (uint i in topology.Indices) writer.Write(i);
        foreach (var t in topology.Triangles) { writer.Write(t.SourceTriangle); writer.Write(t.RegionIndex); writer.Write((int)t.Role); }
        return Hash(stream.ToArray());
    }
    static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
