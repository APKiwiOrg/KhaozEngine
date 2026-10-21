using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Cold CPU input encoding, plus allocation-free scalar uniform serialization. Device reads apply F.</summary>
internal static class CoverageGpuUpload
{
    internal static CoverageGpuHostUpload Host(CoverageRefinement topology, int boneCount)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boneCount);
        _ = CoverageGpuLayout.FrameWords(boneCount);
        var data = new CoverageReferenceTopology(topology);
        var skin = new CoverageReferenceSkin(topology.Source, boneCount);
        var activeOffsets = new int[data.PositionGroupCount + 1];
        var f = new CoverageReferenceArithmetic();
        foreach (var v in data.Vertices)
            if (v.RegionIndex >= 0 && f.Greater(v.Strength, 0)) activeOffsets[v.PositionGroup + 1]++;
        for (int g = 1; g < activeOffsets.Length; g++) activeOffsets[g] = checked(activeOffsets[g] + activeOffsets[g - 1]);
        var active = new int[activeOffsets[^1]];
        int[] next = (int[])activeOffsets.Clone();
        for (int v = 0; v < data.VertexCount; v++)
        {
            var record = data.Vertices[v];
            if (record.RegionIndex >= 0 && f.Greater(record.Strength, 0)) active[next[record.PositionGroup]++] = v;
        }
        int[] counts = [data.SourceVertexCount, data.SourceVertexCount, data.VertexCount, data.TriangleCount, data.SourceTriangles.Count,
            data.AliasCount, data.Indices.Length, data.Recipes.Length, data.GroupWeights.Length, data.VertexWeights.Length, data.EdgeParameters.Length,
            data.GroupRepresentatives.Length, data.CoreFaceOffsets.Length, data.CoreFaces.Length, data.VertexFaceOffsets.Length, data.VertexCorners.Length,
            data.ShadingCornerOffsets.Length, data.ShadingCorners.Length, activeOffsets.Length, active.Length, data.Indices.Length, data.Indices.Length];
        int[] strides = [20, 4, 10, 3, 8, 2, 1, 7, 3, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];
        uint[] words = Allocate(CoverageGpuLayout.HostHeaderWords, counts, strides);
        words[0] = (uint)data.SourceVertexCount; words[1] = (uint)data.VertexCount; words[2] = (uint)data.TriangleCount;
        words[3] = (uint)data.PositionGroupCount; words[4] = (uint)data.AliasCount; words[5] = (uint)topology.ShadingGroupCount; words[6] = (uint)boneCount;
        int cursor = CoverageGpuLayout.HostHeaderWords;
        for (int i = 0; i < counts.Length; i++)
        {
            words[8 + i * 2] = (uint)cursor; words[9 + i * 2] = (uint)counts[i];
            cursor = checked(cursor + counts[i] * strides[i]);
        }
        cursor = CoverageGpuLayout.HostHeaderWords;
        foreach (var v in topology.Source.SkinVertices)
        {
            WriteModel(new(v.Position, v.Normal, v.Color, v.Uv, v.Tangent), words.AsSpan(cursor, 16)); cursor += 16;
            Float(v.BoneWeights.X); Float(v.BoneWeights.Y); Float(v.BoneWeights.Z); Float(v.BoneWeights.W);
        }
        Ints(skin.Indices);
        foreach (var v in data.Vertices)
        {
            Int(v.SourceTriangle); Int(v.SourceVertex); Int(v.A); Int(v.B); Int(v.C); Int(v.Denominator);
            Int(v.PositionGroup); Int(v.ShadingGroup); Int(v.RegionIndex); Float(v.Strength);
        }
        foreach (var t in data.Triangles) { Int(t.SourceTriangle); Int(t.RegionIndex); Int((int)t.Role); }
        foreach (var t in data.SourceTriangles) { Int(t.Mesh); Int(t.Primitive); Int(t.Triangle); Int(t.Node); Int(t.MaterialSlice); Int(t.A); Int(t.B); Int(t.C); }
        foreach (var a in data.SourceAliases) { Int(a.SourceVertex); Int(a.Representative); }
        foreach (uint index in data.Indices) words[cursor++] = index;
        foreach (var r in data.Recipes) { Int(r.A); Int(r.B); Int(r.C); Int(r.WeightA); Int(r.WeightB); Int(r.WeightC); Int(r.Denominator); }
        foreach (var w in data.GroupWeights) { Float(w.A); Float(w.B); Float(w.C); }
        foreach (var w in data.VertexWeights) { Float(w.A); Float(w.B); Float(w.C); }
        foreach (float t in data.EdgeParameters) Float(t);
        Ints(data.GroupRepresentatives); Ints(data.CoreFaceOffsets); Ints(data.CoreFaces); Ints(data.VertexFaceOffsets); Ints(data.VertexCorners);
        Ints(data.ShadingCornerOffsets); Ints(data.ShadingCorners); Ints(activeOffsets); Ints(active);
        foreach (uint index in data.Indices) words[cursor++] = index;
        var seen = new bool[data.SourceTriangles.Count];
        foreach (var t in data.Triangles)
        {
            var original = data.SourceTriangles[t.SourceTriangle];
            bool first = !seen[t.SourceTriangle]; seen[t.SourceTriangle] = true;
            Int(original.A); Int(first ? original.B : original.A); Int(first ? original.C : original.A);
        }
        return new(words, data.SourceVertexCount, data.VertexCount, data.TriangleCount, data.PositionGroupCount,
            data.AliasCount, topology.ShadingGroupCount, boneCount);
        void Float(float x) => words[cursor++] = Bits(x);
        void Int(int x) => words[cursor++] = unchecked((uint)x);
        void Ints(int[] values) { foreach (int x in values) Int(x); }
    }

    internal static CoverageGpuSupportUpload Support(CoverageSupportSurface support)
    {
        ArgumentNullException.ThrowIfNull(support);
        var data = support.ReferenceData;
        int[] counts = [data.Positions.Length, data.SourceIndices.Length, data.Triangles.Length, data.BoundarySourceVertices.Length,
            data.CellOffsets.Length, data.CellTriangles.Length, data.TriangleCount, data.BoundaryCount];
        uint[] words = Allocate(CoverageGpuLayout.SupportHeaderWords, counts, [3, 1, 8, 1, 1, 1, 2, 2]);
        words[0] = Bits(data.MaxSlope); words[1] = Bits(data.GridMin.X); words[2] = Bits(data.GridMin.Y);
        words[3] = Bits(data.GridMax.X); words[4] = Bits(data.GridMax.Y);
        words[5] = (uint)data.TriangleCount; words[6] = (uint)data.BoundaryCount;
        int cursor = CoverageGpuLayout.SupportHeaderWords;
        Offset(7);
        foreach (var p in data.Positions) { words[cursor++] = Bits(p.X); words[cursor++] = Bits(p.Y); words[cursor++] = Bits(p.Z); }
        Offset(8); Ints(data.SourceIndices);
        Offset(9);
        foreach (var t in data.Triangles) Ints([t.Mesh, t.Primitive, t.Triangle, t.Node, t.MaterialSlice, t.A, t.B, t.C]);
        Offset(10); Ints(data.BoundarySourceVertices);
        Offset(11); Ints(data.CellOffsets);
        Offset(12); Ints(data.CellTriangles);
        CoverageGpuSupportInvariants.Write(data, words.AsSpan(cursor));
        return new(words, data.TriangleCount, data.BoundaryCount);
        void Offset(int header) => words[header] = (uint)cursor;
        void Ints(int[] values) { foreach (int x in values) words[cursor++] = unchecked((uint)x); }
    }

    internal static void WriteModel(in ModelVertex vertex, Span<uint> words)
    {
        Require(words, CoverageGpuLayout.ModelWords);
        words[0] = Bits(vertex.Position.X); words[1] = Bits(vertex.Position.Y); words[2] = Bits(vertex.Position.Z);
        words[3] = Bits(vertex.Normal.X); words[4] = Bits(vertex.Normal.Y); words[5] = Bits(vertex.Normal.Z);
        words[6] = Bits(vertex.Color.X); words[7] = Bits(vertex.Color.Y); words[8] = Bits(vertex.Color.Z); words[9] = Bits(vertex.Color.W);
        words[10] = Bits(vertex.Uv.X); words[11] = Bits(vertex.Uv.Y);
        words[12] = Bits(vertex.Tangent.X); words[13] = Bits(vertex.Tangent.Y); words[14] = Bits(vertex.Tangent.Z); words[15] = Bits(vertex.Tangent.W);
    }

    internal static void WriteMatrix(in Matrix4x4 matrix, Span<uint> words)
    {
        Require(words, CoverageGpuLayout.MatrixWords);
        words[0] = Bits(matrix.M11); words[1] = Bits(matrix.M12); words[2] = Bits(matrix.M13); words[3] = Bits(matrix.M14);
        words[4] = Bits(matrix.M21); words[5] = Bits(matrix.M22); words[6] = Bits(matrix.M23); words[7] = Bits(matrix.M24);
        words[8] = Bits(matrix.M31); words[9] = Bits(matrix.M32); words[10] = Bits(matrix.M33); words[11] = Bits(matrix.M34);
        words[12] = Bits(matrix.M41); words[13] = Bits(matrix.M42); words[14] = Bits(matrix.M43); words[15] = Bits(matrix.M44);
    }

    internal static void WriteCommand(in CoverageReferenceFrame frame, in CoverageCommandPose command, uint supportOffset, Span<uint> words)
    {
        Require(words, CoverageGpuLayout.CommandWords);
        words = words[..CoverageGpuLayout.CommandWords]; words.Clear();
        WriteMatrix(frame.HostToCoverage, words); WriteMatrix(frame.HostToActualSheet, words[16..]); WriteMatrix(frame.ActorLinear, words[32..]);
        words[48] = Bits(frame.Recess.X); words[49] = Bits(frame.Recess.Y); words[50] = Bits(frame.Recess.Z); words[51] = Bits(frame.ZScale);
        words[52] = Bits(frame.CapMargin); words[53] = Bits(frame.ClearanceCoverage); words[54] = Bits(frame.ActualClearanceCoverage); words[55] = Bits(frame.CorrectionRate);
        words[56] = (uint)frame.Command; words[57] = Bits(command.MinZ); words[58] = Bits(command.MaxZ); words[59] = Bits(command.CollarWidth);
        words[60] = Bits(command.Clearance); words[61] = Bits(command.MaxWorldRecession); words[62] = Bits(command.TransitionWeight); words[63] = command.Id;
        words[64] = (uint)command.EquipmentInstanceId; words[65] = (uint)(command.EquipmentInstanceId >> 32);
        words[66] = unchecked((uint)command.RegionIndex); words[67] = supportOffset;
    }

    static uint[] Allocate(int header, int[] counts, int[] strides)
    {
        long size = header;
        for (int i = 0; i < counts.Length; i++) size = checked(size + (long)counts[i] * strides[i]);
        return new uint[checked((int)CoverageGpuLayout.Words(size))];
    }
    static uint Bits(float x) => BitConverter.SingleToUInt32Bits(x);
    static void Require(Span<uint> words, int count)
    {
        if (words.Length < count) throw new ArgumentException("Coverage input destination is too short.", nameof(words));
    }
}
