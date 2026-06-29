using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>Reads baked 3D collision shapes (<c>.coll</c>) produced by <see cref="PropCollisionBake"/>.
    /// Render-free: no GPU dependency, safe for headless server and the authoritative physics sim.</summary>
    public static class PropCollisionLoader
    {
        /// <summary>Read a single baked shape from <paramref name="stream"/>. Throws
        /// <see cref="InvalidOperationException"/> on a bad magic, unsupported version, or unknown kind.</summary>
        public static PhysicsShape Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            uint magic = r.ReadUInt32();
            if (magic != PropCollisionBake.Magic)
                throw new InvalidOperationException(
                    $"PropCollisionLoader: bad magic 0x{magic:X8} (expected 0x{PropCollisionBake.Magic:X8}).");

            byte version = r.ReadByte();
            if (version != PropCollisionBake.Version)
                throw new InvalidOperationException(
                    $"PropCollisionLoader: unsupported version {version} (expected {PropCollisionBake.Version}).");

            byte kind = r.ReadByte();
            switch (kind)
            {
                case 1: // ConvexHullShape
                {
                    int count = r.ReadInt32();
                    var points = new Vector3[count];
                    for (int i = 0; i < count; i++)
                        points[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    return new ConvexHullShape(points);
                }
                case 3: // CylinderShape
                {
                    float radius = r.ReadSingle();
                    float length = r.ReadSingle();
                    return new CylinderShape(radius, length);
                }
                case 2: // TriangleMeshShape
                {
                    int vCount = r.ReadInt32();
                    var verts = new Vector3[vCount];
                    for (int i = 0; i < vCount; i++)
                        verts[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    int iCount = r.ReadInt32();
                    var indices = new int[iCount];
                    for (int i = 0; i < iCount; i++)
                        indices[i] = r.ReadInt32();
                    return new TriangleMeshShape(verts, indices);
                }
                default:
                    throw new InvalidOperationException(
                        $"PropCollisionLoader: unknown shape kind {kind}.");
            }
        }

        /// <summary>Read every entry's referenced <c>.coll</c> into an id -> <see cref="PhysicsShape"/> map.
        /// Entries with no <see cref="AssetEntry.CollisionShape"/> path are skipped.</summary>
        public static IReadOnlyDictionary<string, PhysicsShape> LoadAll(AssetManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            var result = new Dictionary<string, PhysicsShape>();
            foreach (AssetEntry e in manifest.Props)
            {
                if (string.IsNullOrEmpty(e.CollisionShape)) continue;
                using FileStream fs = File.OpenRead(e.CollisionShape);
                result[e.Id] = Read(fs);
            }
            return result;
        }
    }
}
