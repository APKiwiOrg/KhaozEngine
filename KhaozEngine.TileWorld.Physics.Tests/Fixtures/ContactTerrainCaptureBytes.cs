using System.IO;
using KhaozEngine.Movement;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>Full field-wise capture receipt, including original identities and all floating-point bits.</summary>
internal static class ContactTerrainCaptureBytes
{
    internal static byte[] Encode(PhysicsNavDescentEvidence e)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write(e.Width); w.Write(e.Height); w.Write(e.FaceCount);
        for (int i = 0; i < e.FaceCount; i++)
        {
            var f = e.Face(i); w.Write(f.Key.Source); w.Write(f.Key.Address); w.Write(f.Key.Part);
            w.Write(f.Owner.WorldId); w.Write(f.Owner.LocalId); w.Write(f.CanonicalOwner.WorldId); w.Write(f.CanonicalOwner.LocalId);
            w.Write(f.Analytic); w.Write((byte)f.Kind); w.Write(f.Points.Length);
            foreach (var p in f.Points) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            w.Write(f.Pose.Position.X); w.Write(f.Pose.Position.Y); w.Write(f.Pose.Position.Z);
            w.Write(f.Pose.Orientation.X); w.Write(f.Pose.Orientation.Y); w.Write(f.Pose.Orientation.Z); w.Write(f.Pose.Orientation.W);
            w.Write(f.Radius); w.Write(f.HalfLength); w.Write(e.Joins(i).Length); foreach (int next in e.Joins(i)) w.Write(next);
        }
        for (int z = 0; z < e.Height; z++) for (int x = 0; x < e.Width; x++)
        {
            w.Write((byte)e.Coverage(x, z)); w.Write(e.Column(x, z).Length);
            foreach (var r in e.Column(x, z)) { w.Write(r.Face); w.Write(r.Areas); }
        }
        w.Write(e.RawCount);
        foreach (var raw in e.RawBindings) { w.Write(raw.X); w.Write(raw.Z); w.Write(raw.Ordinal); w.Write(raw.HeightBits); w.Write(raw.Face); }
        w.Flush(); return stream.ToArray();
    }
}
