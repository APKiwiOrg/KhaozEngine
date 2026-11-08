using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Physics;
using static KhaozEngine.Movement.TerrainEvidenceBinary;

namespace KhaozEngine.Movement;

/// <summary>Canonical sparse evidence beside the existing columns. Original raw ordinals and bits are reused.</summary>
internal static class TerrainEvidenceWriter
{
    internal static byte[] Encode(PhysicsNavDescentEvidence evidence, PhysicsNavColumns columns)
    {
        var writer = new NavBakeWriter();
        var strings = new SortedSet<string>(StringComparer.Ordinal);
        var vertices = new List<PhysicsEvidencePoint>();
        var indices = new Dictionary<(long, long, long), int>();
        static (long, long, long) Key(PhysicsEvidencePoint p) => (BitConverter.DoubleToInt64Bits(p.X), BitConverter.DoubleToInt64Bits(p.Y), BitConverter.DoubleToInt64Bits(p.Z));
        for (int i = 0; i < evidence.FaceCount; i++)
        {
            var face = evidence.Face(i);
            strings.Add(face.Key.Source); strings.Add(face.CanonicalOwner.WorldId); strings.Add(face.CanonicalOwner.LocalId);
            foreach (var p in face.Points)
                if (indices.TryAdd(Key(p), vertices.Count)) vertices.Add(p);
        }
        var names = strings.Select((text, index) => (text, index)).ToDictionary(p => p.text, p => p.index, StringComparer.Ordinal);
        Unsigned(writer, (uint)strings.Count);
        foreach (var text in strings) Text(writer, text);
        Unsigned(writer, (uint)vertices.Count);
        foreach (var p in vertices)
        {
            bool single = SingleExact(p.X) && SingleExact(p.Y) && SingleExact(p.Z);
            writer.WriteUInt8(single ? (byte)1 : (byte)0);
            if (single) { writer.WriteSingle((float)p.X); writer.WriteSingle((float)p.Y); writer.WriteSingle((float)p.Z); }
            else { writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(p.X)); writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(p.Y)); writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(p.Z)); }
        }
        Unsigned(writer, (uint)evidence.FaceCount);
        for (int i = 0; i < evidence.FaceCount; i++)
        {
            var face = evidence.Face(i);
            Unsigned(writer, (uint)names[face.Key.Source]); writer.WriteUInt64(unchecked((ulong)face.Key.Address)); Unsigned(writer, (uint)face.Key.Part);
            Unsigned(writer, (uint)names[face.CanonicalOwner.WorldId]); Unsigned(writer, (uint)names[face.CanonicalOwner.LocalId]);
            writer.WriteUInt8(face.Analytic ? (byte)1 : (byte)0); writer.WriteUInt8((byte)face.Kind); Unsigned(writer, (uint)face.Points.Length);
            foreach (var p in face.Points) Unsigned(writer, (uint)indices[Key(p)]);
            if (face.Points.Length == 0)
            {
                writer.WriteSingle(face.Pose.Position.X); writer.WriteSingle(face.Pose.Position.Y); writer.WriteSingle(face.Pose.Position.Z);
                writer.WriteSingle(face.Pose.Orientation.X); writer.WriteSingle(face.Pose.Orientation.Y); writer.WriteSingle(face.Pose.Orientation.Z); writer.WriteSingle(face.Pose.Orientation.W);
                writer.WriteSingle(face.Radius); writer.WriteSingle(face.HalfLength);
            }
            Unsigned(writer, (uint)evidence.Joins(i).Length);
            foreach (int next in evidence.Joins(i)) Unsigned(writer, (uint)next);
        }
        int included = 0;
        for (int z = 0; z < evidence.Height; z++) for (int x = 0; x < evidence.Width; x++)
            if (evidence.StoredCoverage(x, z) != DescentCoverage.Excluded) included++;
        Unsigned(writer, (uint)included);
        int previous = 0;
        for (int z = 0; z < evidence.Height; z++) for (int x = 0; x < evidence.Width; x++)
        {
            var coverage = evidence.StoredCoverage(x, z);
            if (coverage == DescentCoverage.Excluded) continue;
            int cell = z * evidence.Width + x;
            Unsigned(writer, (uint)(cell - previous)); previous = cell;
            writer.WriteUInt8((byte)coverage); Unsigned(writer, (uint)evidence.Column(x, z).Length);
            foreach (var row in evidence.Column(x, z)) { Unsigned(writer, (uint)row.Face); Unsigned(writer, row.Areas); }
            foreach (var raw in columns.GetColumn(x, z))
                Unsigned(writer, evidence.TryRaw(x, z, BitConverter.SingleToInt32Bits(raw.Height), out var binding) ? (uint)binding.Face + 1 : 0);
        }
        return writer.ToArray();
    }
}
