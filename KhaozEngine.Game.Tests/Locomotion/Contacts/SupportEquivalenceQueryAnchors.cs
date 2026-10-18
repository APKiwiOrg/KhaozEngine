using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

internal sealed record SupportEquivalenceQueryAnchor(string Row, Pose Pose, float Radius, float Length, float Band,
    Vector2 Axis, float Slope, float AlternateSlope, int? TargetId, string SceneDigest);

// Owns the historical questions independently of the support geometry oracle. The fixture has only integer
// float encodings. Capture still compares the original answer fixture and never enables digest replacement.
internal sealed class SupportEquivalenceQueryAnchors(bool verifyOnly = false)
{
    const string CaptureVariable = "KHAOZENGINE_SUPPORT_ANCHOR_CAPTURE";
    const string FixtureName = "support-equivalence-queries.json";
    readonly List<Bits> _observed = [];
    readonly Dictionary<IPhysicsWorld, string> _scenes = [];
    static readonly Lazy<Dictionary<string, Bits>> Frozen = new(() =>
        JsonSerializer.Deserialize<Bits[]>(File.ReadAllText(FixturePath))!.ToDictionary(row => row.Row));
    internal bool VerifyOnly { get; } = verifyOnly;
    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Locomotion", "Contacts", "Fixtures", FixtureName);
    internal static SupportEquivalenceQueryAnchor Read(string row) => Frozen.Value[row].Decode();

    internal StaticHandle AddStatic(BepuPhysicsWorld world, PhysicsShape shape, Pose pose)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WritePose(writer, pose);
        WriteShape(writer, shape);
        writer.Flush();
        _scenes.Add(world, Convert.ToHexString(SHA256.HashData(stream.ToArray())));
        return world.AddStatic(shape, pose);
    }

    internal SupportEquivalenceQueryAnchor Resolve(string row, IPhysicsWorld world, CapsuleShape probe,
        Pose pose, int? targetId = null)
    {
        var generated = new SupportEquivalenceQueryAnchor(row, pose, probe.Radius, probe.Length,
            SupportCertification.ContactBand, new(pose.Position.X, pose.Position.Z), 0.70710677f, 0.5f,
            targetId, _scenes[world]);
        Bits bits = Bits.Encode(generated);
        _observed.Add(bits);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CaptureVariable))) return generated;
        Bits frozen = Frozen.Value[row];
        Assert.Equal(frozen.SceneDigest, bits.SceneDigest);
        if (VerifyOnly) Assert.Equal(JsonSerializer.Serialize(frozen), JsonSerializer.Serialize(bits));
        return frozen.Decode();
    }

    internal void Complete()
    {
        Assert.Equal(1952, _observed.Count);
        Assert.Equal(_observed.Count, _observed.Select(row => row.Row).Distinct().Count());
        string? capture = Environment.GetEnvironmentVariable(CaptureVariable);
        if (!string.IsNullOrEmpty(capture))
            File.WriteAllText(CapturePath(capture), JsonSerializer.Serialize(_observed, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        else Assert.Equal(Frozen.Value.Keys.Order(), _observed.Select(row => row.Row).Order());
    }

    internal static string CapturePath(string path) => Path.GetFullPath(path, Path.GetFullPath("../../../../", AppContext.BaseDirectory));

    static int F(float value) => BitConverter.SingleToInt32Bits(value);
    static float S(int bits) => BitConverter.Int32BitsToSingle(bits);
    static int[] V(Vector3 value) => [F(value.X), F(value.Y), F(value.Z)];
    static Vector3 V(int[] bits) => new(S(bits[0]), S(bits[1]), S(bits[2]));

    internal sealed record Bits(string Row, int[] Position, int[] Orientation, int Radius, int Length, int Band,
        int[] Axis, int Slope, int AlternateSlope, int? TargetId, string SceneDigest)
    {
        internal static Bits Encode(SupportEquivalenceQueryAnchor a) => new(a.Row, V(a.Pose.Position),
            [F(a.Pose.Orientation.X), F(a.Pose.Orientation.Y), F(a.Pose.Orientation.Z), F(a.Pose.Orientation.W)],
            F(a.Radius), F(a.Length), F(a.Band), [F(a.Axis.X), F(a.Axis.Y)], F(a.Slope), F(a.AlternateSlope),
            a.TargetId, a.SceneDigest);
        internal SupportEquivalenceQueryAnchor Decode() => new(Row,
            new(V(Position), new(S(Orientation[0]), S(Orientation[1]), S(Orientation[2]), S(Orientation[3]))),
            S(Radius), S(Length), S(Band), new(S(Axis[0]), S(Axis[1])), S(Slope), S(AlternateSlope), TargetId, SceneDigest);
    }

    static void WritePose(BinaryWriter writer, Pose pose)
    {
        Write(writer, pose.Position);
        writer.Write(F(pose.Orientation.X)); writer.Write(F(pose.Orientation.Y));
        writer.Write(F(pose.Orientation.Z)); writer.Write(F(pose.Orientation.W));
    }

    static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(F(value.X)); writer.Write(F(value.Y)); writer.Write(F(value.Z));
    }

    static void WriteShape(BinaryWriter writer, PhysicsShape shape)
    {
        writer.Write(shape.GetType().Name);
        switch (shape)
        {
            case BoxShape box: Write(writer, box.HalfExtents); break;
            case SphereShape sphere: writer.Write(F(sphere.Radius)); break;
            case CapsuleShape capsule: writer.Write(F(capsule.Radius)); writer.Write(F(capsule.Length)); break;
            case CylinderShape cylinder: writer.Write(F(cylinder.Radius)); writer.Write(F(cylinder.Length)); break;
            case ConvexHullShape hull:
                writer.Write(hull.Points.Length);
                foreach (Vector3 point in hull.Points) Write(writer, point);
                break;
            case TriangleMeshShape mesh:
                writer.Write(mesh.Vertices.Length);
                foreach (Vector3 point in mesh.Vertices) Write(writer, point);
                writer.Write(mesh.Indices.Length);
                foreach (int index in mesh.Indices) writer.Write(index);
                break;
            case CompoundShape compound:
                writer.Write(compound.Children.Length);
                foreach (CompoundChild child in compound.Children)
                {
                    WritePose(writer, child.Local);
                    WriteShape(writer, child.Shape);
                }
                break;
            default: throw new InvalidOperationException("Unknown equivalence shape.");
        }
    }
}
