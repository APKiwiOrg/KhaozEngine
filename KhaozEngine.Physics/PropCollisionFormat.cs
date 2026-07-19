using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>The render-free KECL (<c>.coll</c>) collision-shape format: read/write a single
/// <see cref="PhysicsShape"/> plus headless, manifest-free bulk loaders. Lives in the dependency-free
/// <c>KhaozEngine.Physics</c> package (only <see cref="System.IO"/> + <see cref="PhysicsShape"/>), so an
/// authoritative server with no GPU/windowing can load baked shapes and build the same physics world a client
/// predicts against - byte-identical, so spatial queries match and prediction reconciles. The offline
/// <c>Bake(GltfMesh)</c> path that PRODUCES shapes from glTF stays in <c>KhaozEngine.Render3D</c> /
/// <c>ke-propbake</c> (it needs the mesh); the render-side <c>PropCollisionBake</c> / <c>PropCollisionLoader</c>
/// delegate their serialization here, so the format is single-sourced and the manifest-driven load path keeps
/// working unchanged.</summary>
public static class PropCollisionFormat
{
    /// <summary>Binary magic: "KECL" (KhaozEngine Collision).</summary>
    public const uint Magic = 0x4B45434C;
    /// <summary>Format version written by this implementation.</summary>
    public const byte Version = 1;

    // Shape kind byte written to the binary. Stable wire values - never renumber.
    internal const byte KindConvexHull = 1;
    internal const byte KindTriangleMesh = 2;
    internal const byte KindCylinder = 3;
    internal const byte KindBox = 4;
    internal const byte KindCompound = 5;

    /// <summary>Serialize <paramref name="shape"/> to <paramref name="stream"/> in the KECL binary format:
    /// Magic (uint32 LE) + version (byte) + kind (byte) + payload. The stream is left open.</summary>
    public static void Write(PhysicsShape shape, Stream stream)
    {
        if (shape == null) throw new ArgumentNullException(nameof(shape));
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        WriteShape(w, shape);
    }

    // Writes a single shape's kind byte + payload. Recurses for compound children. No magic/version here (the
    // top-level Write emits those once), so the existing kind 1/2/3 byte layout is unchanged.
    static void WriteShape(BinaryWriter w, PhysicsShape shape)
    {
        switch (shape)
        {
            case ConvexHullShape hull:
                w.Write(KindConvexHull);
                w.Write(hull.Points.Length);
                foreach (Vector3 p in hull.Points)
                { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
                break;
            case CylinderShape cyl:
                w.Write(KindCylinder);
                w.Write(cyl.Radius);
                w.Write(cyl.Length);
                break;
            case TriangleMeshShape mesh:
                w.Write(KindTriangleMesh);
                w.Write(mesh.Vertices.Length);
                foreach (Vector3 v in mesh.Vertices)
                { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
                w.Write(mesh.Indices.Length);
                foreach (int idx in mesh.Indices)
                    w.Write(idx);
                break;
            case BoxShape box:
                w.Write(KindBox);
                w.Write(box.HalfExtents.X); w.Write(box.HalfExtents.Y); w.Write(box.HalfExtents.Z);
                break;
            case CompoundShape compound:
                w.Write(KindCompound);
                w.Write(compound.Children.Length);
                foreach (CompoundChild child in compound.Children)
                {
                    WritePose(w, child.Local);
                    WriteShape(w, child.Shape);
                }
                break;
            default:
                throw new NotSupportedException($"PropCollisionFormat.Write: unsupported shape type {shape.GetType().Name}");
        }
    }

    static void WritePose(BinaryWriter w, Pose pose)
    {
        w.Write(pose.Position.X); w.Write(pose.Position.Y); w.Write(pose.Position.Z);
        w.Write(pose.Orientation.X); w.Write(pose.Orientation.Y); w.Write(pose.Orientation.Z); w.Write(pose.Orientation.W);
    }

    /// <summary>Read a single baked shape from <paramref name="stream"/>. Throws
    /// <see cref="InvalidOperationException"/> on a bad magic, unsupported version, unknown kind, or an
    /// array-count field that is negative or could not possibly fit in what remains of the stream (a truncated
    /// or corrupted file) - never <see cref="OverflowException"/> or <see cref="OutOfMemoryException"/> from an
    /// unchecked allocation. The stream is left open.</summary>
    public static PhysicsShape Read(Stream stream)
    {
        if (stream == null) throw new ArgumentNullException(nameof(stream));
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        uint magic = r.ReadUInt32();
        if (magic != Magic)
            throw new InvalidOperationException(
                $"PropCollisionFormat: bad magic 0x{magic:X8} (expected 0x{Magic:X8}).");

        byte version = r.ReadByte();
        if (version != Version)
            throw new InvalidOperationException(
                $"PropCollisionFormat: unsupported version {version} (expected {Version}).");

        return ReadShape(r);
    }

    // Bytes the smallest possible encoding of one array element occupies, used only as a fallback ceiling when the
    // stream can't report a remaining length (see ReadCount). A Vector3 is 3 floats, an int index is 4 bytes, and a
    // compound child is at minimum a pose (7 floats) plus a shape kind byte.
    const int Vector3Bytes = 12;
    const int Int32Bytes = 4;
    const int MinCompoundChildBytes = 7 * 4 + 1;

    // Sane upper bound for any single count field, used only when the stream can't report a remaining length (a
    // non-seekable Stream). Generously large: no legitimate baked shape approaches this list size.
    const int MaxCountFallback = 100_000_000;

    // Validates a length-prefixed array count read from the stream before it is used to allocate. A truncated or
    // corrupted .coll file can hand this a garbage int32: a negative value would overflow new T[count] into an
    // OverflowException (the CLR treats a negative array length as an unsigned overflow), and a huge positive
    // value would throw OutOfMemoryException or stall the allocation - both outside the InvalidOperationException
    // contract this format promises elsewhere (magic/version/kind). Rejects a negative count outright. When the
    // stream can report its remaining length, also rejects a count whose minimum possible byte size could not
    // possibly fit in what's left, catching a bogus positive count before the allocation is attempted rather than
    // picking an arbitrary ceiling. Otherwise it falls back to a generous absolute maximum.
    static int ReadCount(BinaryReader r, string what, int elementMinBytes)
    {
        int count = r.ReadInt32();
        if (count < 0)
            throw new InvalidOperationException(
                $"PropCollisionFormat: {what} count {count} is negative.");

        Stream stream = r.BaseStream;
        if (stream.CanSeek)
        {
            long remaining = stream.Length - stream.Position;
            if ((long)count * elementMinBytes > remaining)
                throw new InvalidOperationException(
                    $"PropCollisionFormat: {what} count {count} needs at least {(long)count * elementMinBytes} bytes, but only {remaining} remain in the stream.");
        }
        else if (count > MaxCountFallback)
        {
            throw new InvalidOperationException(
                $"PropCollisionFormat: {what} count {count} exceeds the maximum of {MaxCountFallback}.");
        }

        return count;
    }

    static PhysicsShape ReadShape(BinaryReader r)
    {
        byte kind = r.ReadByte();
        switch (kind)
        {
            case KindConvexHull:
            {
                int count = ReadCount(r, "convex hull point", Vector3Bytes);
                var points = new Vector3[count];
                for (int i = 0; i < count; i++)
                    points[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                return new ConvexHullShape(points);
            }
            case KindCylinder:
            {
                float radius = r.ReadSingle();
                float length = r.ReadSingle();
                return new CylinderShape(radius, length);
            }
            case KindTriangleMesh:
            {
                int vCount = ReadCount(r, "triangle mesh vertex", Vector3Bytes);
                var verts = new Vector3[vCount];
                for (int i = 0; i < vCount; i++)
                    verts[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int iCount = ReadCount(r, "triangle mesh index", Int32Bytes);
                var indices = new int[iCount];
                for (int i = 0; i < iCount; i++)
                    indices[i] = r.ReadInt32();
                return new TriangleMeshShape(verts, indices);
            }
            case KindBox:
            {
                float hx = r.ReadSingle(), hy = r.ReadSingle(), hz = r.ReadSingle();
                return new BoxShape(new Vector3(hx, hy, hz));
            }
            case KindCompound:
            {
                int childCount = ReadCount(r, "compound child", MinCompoundChildBytes);
                var children = new CompoundChild[childCount];
                for (int i = 0; i < childCount; i++)
                {
                    Pose local = ReadPose(r);
                    PhysicsShape child = ReadShape(r);
                    children[i] = new CompoundChild(child, local);
                }
                return new CompoundShape(children);
            }
            default:
                throw new InvalidOperationException(
                    $"PropCollisionFormat: unknown shape kind {kind}.");
        }
    }

    static Pose ReadPose(BinaryReader r)
    {
        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var orient = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new Pose(pos, orient);
    }

    /// <summary>Read a single baked shape from a <c>.coll</c> file path. Convenience over the
    /// <see cref="Read(Stream)"/> overload.</summary>
    public static PhysicsShape Read(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path must be non-empty.", nameof(path));
        using FileStream fs = File.OpenRead(path);
        return Read(fs);
    }

    /// <summary>Headless, manifest-free load: map every <c>&lt;id&gt;.coll</c> file directly under
    /// <paramref name="directory"/> to its shape, keyed by the file name without extension. Top-level only (no
    /// recursion). The exact <c>.coll</c> extension is matched (not a Windows wildcard prefix). Throws
    /// <see cref="DirectoryNotFoundException"/> when the directory is absent. Intended for an authoritative
    /// server that ships a flat kit of baked shapes; the manifest-driven path stays in
    /// <c>KhaozEngine.Render3D.PropCollisionLoader.LoadAll</c>.</summary>
    public static IReadOnlyDictionary<string, PhysicsShape> LoadDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory must be non-empty.", nameof(directory));
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"PropCollisionFormat.LoadDirectory: '{directory}' does not exist.");

        var result = new Dictionary<string, PhysicsShape>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.coll"))
        {
            // Guard the Windows search-pattern quirk where "*.coll" can also match longer extensions.
            if (!Path.GetExtension(path).Equals(".coll", StringComparison.OrdinalIgnoreCase)) continue;
            result[Path.GetFileNameWithoutExtension(path)] = Read(path);
        }
        return result;
    }

    /// <summary>Headless, manifest-free load from explicit <c>(id, collPath)</c> pairs (the path need not follow
    /// the <c>&lt;id&gt;.coll</c> convention). A later duplicate id overwrites an earlier one.</summary>
    public static IReadOnlyDictionary<string, PhysicsShape> Load(IEnumerable<(string id, string collPath)> entries)
    {
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        var result = new Dictionary<string, PhysicsShape>();
        foreach ((string id, string collPath) in entries)
            result[id] = Read(collPath);
        return result;
    }
}
