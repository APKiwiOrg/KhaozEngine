using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Cold immutable topology/table packing and allocation-free current-frame serialization.</summary>
internal static class CoverageCarryUpload
{
    internal static uint[] Host(CoverageRefinement topology, int bones)
    {
        var data = new CoverageReferenceTopology(topology);
        var skin = new CoverageReferenceSkin(topology.Source, bones);
        int[] pairs = [8, 10, 12, 16, 20, 22, 24, 26, 28, 36, 38, 40, 42];
        int[] counts = [data.SourceVertexCount, data.SourceVertexCount, data.VertexCount, data.SourceTriangles.Count,
            data.Indices.Length, data.PositionGroupCount, data.PositionGroupCount, data.VertexCount, data.PositionGroupCount,
            data.VertexFaceOffsets.Length, data.VertexCorners.Length, data.ShadingCornerOffsets.Length, data.ShadingCorners.Length];
        int[] strides = [20, 4, 10, 8, 1, 7, 3, 3, 1, 1, 1, 1, 1];
        int size = 64;
        for (int i = 0; i < counts.Length; i++) size = checked(size + counts[i] * strides[i]);
        _ = CoverageCarryLayout.Bytes(size);
        uint[] words = new uint[size];
        words[0] = (uint)data.SourceVertexCount; words[1] = (uint)data.VertexCount; words[2] = (uint)data.TriangleCount;
        words[3] = (uint)data.PositionGroupCount; words[6] = (uint)bones;
        int cursor = 64;
        for (int i = 0; i < pairs.Length; i++)
        {
            words[pairs[i]] = (uint)cursor; words[pairs[i] + 1] = (uint)counts[i];
            cursor += counts[i] * strides[i];
        }
        cursor = 64;
        foreach (var v in topology.Source.SkinVertices)
        {
            Model(new(v.Position, v.Normal, v.Color, v.Uv, v.Tangent), words.AsSpan(cursor, 16)); cursor += 16;
            Float(v.BoneWeights.X); Float(v.BoneWeights.Y); Float(v.BoneWeights.Z); Float(v.BoneWeights.W);
        }
        Ints(skin.Indices);
        var arithmetic = new CoverageReferenceArithmetic();
        foreach (var v in data.Vertices)
        {
            Int(v.SourceTriangle); Int(v.SourceVertex); Int(v.A); Int(v.B); Int(v.C); Int(v.Denominator);
            Int(v.PositionGroup); Int(v.ShadingGroup); Int(v.RegionIndex);
            // The retained CPU predicate owns activity. Kernels consume this cold flag, never reselect representatives.
            Int(v.RegionIndex >= 0 && arithmetic.IsActiveStrength(v.Strength) ? 1 : 0);
        }
        foreach (var t in data.SourceTriangles)
        { Int(t.Mesh); Int(t.Primitive); Int(t.Triangle); Int(t.Node); Int(t.MaterialSlice); Int(t.A); Int(t.B); Int(t.C); }
        foreach (uint index in data.Indices) words[cursor++] = index;
        foreach (var r in data.Recipes)
        { Int(r.A); Int(r.B); Int(r.C); Int(r.WeightA); Int(r.WeightB); Int(r.WeightC); Int(r.Denominator); }
        foreach (var w in data.GroupWeights) { Float(w.A); Float(w.B); Float(w.C); }
        foreach (var w in data.VertexWeights) { Float(w.A); Float(w.B); Float(w.C); }
        foreach (float t in data.EdgeParameters) Float(t);
        Ints(data.VertexFaceOffsets); Ints(data.VertexCorners); Ints(data.ShadingCornerOffsets); Ints(data.ShadingCorners);
        if (cursor != words.Length) throw new InvalidOperationException("Carry host layout and upload disagree.");
        return words;
        void Float(float value) => words[cursor++] = Bits(value);
        void Int(int value) => words[cursor++] = unchecked((uint)value);
        void Ints(int[] values) { foreach (int value in values) Int(value); }
    }

    internal static uint[] Table(CoverageRecessionTable table)
    {
        uint[] words = new uint[checked(table.PositionGroupCount * (table.EntryCount + 1))];
        for (int group = 0; group < table.PositionGroupCount; group++)
            words[group] = unchecked((uint)table.Representatives[group]);
        for (int entry = 0; entry < table.EntryCount; entry++)
        {
            var row = table.Entry(entry);
            for (int group = 0; group < row.Length; group++)
                words[table.PositionGroupCount * (entry + 1) + group] = Bits(row[group]);
        }
        return words;
    }

    internal static void Frame(CoverageCarryActor actor, int entryIndex, ReadOnlySpan<Matrix4x4> palette,
        ReadOnlySpan<CoverageCarryCommand> commands)
    {
        var topology = actor.Owner.Topology;
        Span<uint> frame = actor.Frame;
        frame.Clear();
        frame[1] = actor.WorkBase; frame[2] = actor.Slots[actor.Pending].Base; frame[4] = actor.ResultBase;
        frame[5] = (uint)topology.SourceVertexCount; frame[6] = (uint)topology.VertexCount; frame[7] = (uint)topology.TriangleCount;
        frame[8] = (uint)topology.PositionGroupCount; frame[11] = (uint)palette.Length; frame[12] = (uint)commands.Length;
        frame[13] = (uint)actor.Owner.Request.Generation; frame[14] = checked((uint)(topology.PositionGroupCount * (entryIndex + 1)));
        for (int bone = 0; bone < palette.Length; bone++) Matrix(palette[bone], frame[(16 + bone * 16)..]);
        int commandBase = 16 + palette.Length * 16, regionBase = commandBase + 4 * CoverageCarryLayout.CommandWords;
        frame.Slice(regionBase, CoverageCarryLayout.MaxRegions).Fill(uint.MaxValue);
        for (int slot = 0; slot < commands.Length; slot++)
        {
            var command = commands[slot]; int offset = commandBase + slot * CoverageCarryLayout.CommandWords;
            frame[offset] = Bits(command.RecessionDirection.X); frame[offset + 1] = Bits(command.RecessionDirection.Y);
            frame[offset + 2] = Bits(command.RecessionDirection.Z); frame[offset + 3] = Bits(command.TransitionWeight);
            frame[offset + 4] = (uint)command.RegionIndex;
            frame[regionBase + command.RegionIndex] = (uint)slot;
        }
    }

    static void Model(in ModelVertex v, Span<uint> words)
    {
        words[0] = Bits(v.Position.X); words[1] = Bits(v.Position.Y); words[2] = Bits(v.Position.Z);
        words[3] = Bits(v.Normal.X); words[4] = Bits(v.Normal.Y); words[5] = Bits(v.Normal.Z);
        words[6] = Bits(v.Color.X); words[7] = Bits(v.Color.Y); words[8] = Bits(v.Color.Z); words[9] = Bits(v.Color.W);
        words[10] = Bits(v.Uv.X); words[11] = Bits(v.Uv.Y);
        words[12] = Bits(v.Tangent.X); words[13] = Bits(v.Tangent.Y); words[14] = Bits(v.Tangent.Z); words[15] = Bits(v.Tangent.W);
    }
    static void Matrix(in Matrix4x4 m, Span<uint> words)
    {
        words[0] = Bits(m.M11); words[1] = Bits(m.M12); words[2] = Bits(m.M13); words[3] = Bits(m.M14);
        words[4] = Bits(m.M21); words[5] = Bits(m.M22); words[6] = Bits(m.M23); words[7] = Bits(m.M24);
        words[8] = Bits(m.M31); words[9] = Bits(m.M32); words[10] = Bits(m.M33); words[11] = Bits(m.M34);
        words[12] = Bits(m.M41); words[13] = Bits(m.M42); words[14] = Bits(m.M43); words[15] = Bits(m.M44);
    }
    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
