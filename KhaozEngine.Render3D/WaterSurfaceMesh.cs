using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>A previously uploaded bounded water mesh. Domain ownership follows Mesh. Task 8 resolves that handle
/// from the published resource. Submitting this value never uploads geometry or domain data.</summary>
public readonly record struct WaterSurfaceMesh(MeshHandle Mesh, RenderAnchor Anchor, Matrix4x4 LocalTransform, WaterLook Look);

/// <summary>Version 1 original-domain triangles in mesh-local XZ. One vec4 header (version, triangle count, 0, 0),
/// followed by three vec4 points per triangle (x, z, 0, 0), little endian. The bounded path alone binds this data.</summary>
public static class WaterSurfaceDomain
{
    public const int MaxVertices = 1048576;
    public static byte[] Encode(IReadOnlyList<Vector2> triangleVertices)
    {
        ArgumentNullException.ThrowIfNull(triangleVertices);
        if (triangleVertices.Count == 0 || triangleVertices.Count % 3 != 0 || triangleVertices.Count > MaxVertices)
            throw new ArgumentException("Water domains require bounded complete triangles.", nameof(triangleVertices));
        var bytes = new byte[checked(16 + 16 * triangleVertices.Count)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, 1);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), triangleVertices.Count / 3);
        for (int i = 0; i < triangleVertices.Count; i++)
        {
            var p = triangleVertices[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) throw new ArgumentException("Water domains require finite vertices.", nameof(triangleVertices));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + i * 16), p.X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20 + i * 16), p.Y);
        }
        Validate(bytes);
        return bytes;
    }
    public static int Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64 || bytes.Length % 16 != 0 || Read(bytes, 0) != 1 || Read(bytes, 8) != 0 || Read(bytes, 12) != 0)
            throw new ArgumentException("Invalid bounded water domain.", nameof(bytes));
        float count = Read(bytes, 4);
        int triangles = (bytes.Length - 16) / 48;
        if (!float.IsFinite(count) || count != triangles || triangles < 1 || triangles > MaxVertices / 3 || (bytes.Length - 16) % 48 != 0)
            throw new ArgumentException("Invalid bounded water domain length.", nameof(bytes));
        for (int i = 16; i < bytes.Length; i += 16)
            if (!float.IsFinite(Read(bytes, i)) || !float.IsFinite(Read(bytes, i + 4)) || Read(bytes, i + 8) != 0 || Read(bytes, i + 12) != 0)
                throw new ArgumentException("Invalid bounded water point.", nameof(bytes));
        for (int i = 0; i < triangles; i++)
        {
            int offset = 16 + i * 48;
            var a = Point(bytes, offset); var b = Point(bytes, offset + 16); var c = Point(bytes, offset + 32);
            if (Cross(a, b, c) == 0) throw new ArgumentException("Collapsed bounded water domain triangle.", nameof(bytes));
        }
        return triangles;
    }
    /// <summary>CPU counterpart of the bounded fragment predicate, boundaries included.</summary>
    public static bool Contains(ReadOnlySpan<byte> bytes, Vector2 localPoint)
    {
        int count = Validate(bytes);
        for (int i = 0; i < count; i++)
        {
            int offset = 16 + i * 48;
            var a = Point(bytes, offset); var b = Point(bytes, offset + 16); var c = Point(bytes, offset + 32);
            double ab = Cross(a, b, localPoint), bc = Cross(b, c, localPoint), ca = Cross(c, a, localPoint);
            if ((ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0)) return true;
        }
        return false;
    }
    static float Read(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]);
    static Vector2 Point(ReadOnlySpan<byte> bytes, int offset) => new(Read(bytes, offset), Read(bytes, offset + 4));
    static double Cross(Vector2 a, Vector2 b, Vector2 c) => ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
}
