using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Assets;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Bounds-only KECL v1 traversal. Discovery needs no physics objects or vertex buffers.</summary>
internal sealed class MapSceneShapeReach(MapAssetClosure assets, MapSceneScan scan)
{
    readonly Dictionary<string, double> _radii = new(StringComparer.Ordinal);
    internal double Radius(MapResolvedAsset asset) => Math.Max(Resource(asset.CollisionResourceId), Resource(asset.SelectionResourceId));

    double Resource(string? id)
    {
        if (id is null) return 0;
        if (_radii.TryGetValue(id, out double radius)) return radius;
        scan.RetainBytes(checked(96L + 2L * id.Length));
        var bytes = assets.BorrowVerifiedResourceBytes(id).Span;
        scan.Scan(bytes.Length);
        // Released opaque declarations are still accepted by neutral capture. A physical consumer refuses them.
        if (bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x4B45434C)
        {
            var reader = new Reader(bytes[4..]);
            if (reader.Byte() != 1) throw Corrupt();
            radius = reader.Shape(0);
            if (!reader.AtEnd) throw Corrupt();
        }
        _radii.Add(id, radius);
        return radius;
    }

    static MapDocumentException Corrupt() => new("Invalid KECL discovery metadata.");

    ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        readonly ReadOnlySpan<byte> _bytes = bytes;
        int _position;
        internal bool AtEnd => _position == _bytes.Length;
        internal byte Byte()
        {
            if (_position >= _bytes.Length) throw Corrupt();
            return _bytes[_position++];
        }
        int Int32()
        {
            if (_bytes.Length - _position < 4) throw Corrupt();
            int value = BinaryPrimitives.ReadInt32LittleEndian(_bytes[_position..]);
            _position += 4;
            return value;
        }
        double Single()
        {
            float value = BitConverter.Int32BitsToSingle(Int32());
            return float.IsFinite(value) ? value : throw Corrupt();
        }
        double Positive()
        {
            double value = Single();
            return value > 0 ? value : throw Corrupt();
        }
        int Count(int width)
        {
            int count = Int32();
            return count >= 0 && count <= (_bytes.Length - _position) / width ? count : throw Corrupt();
        }
        static double Norm(double x, double y, double z) => Math.BitIncrement(Math.Sqrt(x * x + y * y + z * z));
        double Points(int count)
        {
            if (count == 0) throw Corrupt();
            double radius = 0;
            for (int i = 0; i < count; i++) radius = Math.Max(radius, Norm(Single(), Single(), Single()));
            return radius;
        }
        internal double Shape(int depth)
        {
            switch (Byte())
            {
                case 1: return Points(Count(12));
                case 2:
                    int vertices = Count(12);
                    double mesh = Points(vertices);
                    int indices = Count(4);
                    if (indices % 3 != 0) throw Corrupt();
                    for (int i = 0; i < indices; i++) if ((uint)Int32() >= (uint)vertices) throw Corrupt();
                    return mesh;
                case 3: return Norm(Positive(), Positive(), 0);
                case 4: return Norm(Positive(), Positive(), Positive());
                case 5:
                    if (depth >= 16) throw Corrupt();
                    int children = Count(29);
                    if (children == 0) throw Corrupt();
                    double radius = 0;
                    for (int i = 0; i < children; i++)
                    {
                        double translation = Norm(Single(), Single(), Single());
                        double x = Single(), y = Single(), z = Single(), w = Single();
                        double v2 = x * x + y * y + z * z;
                        // Exact operator norm of the quaternion matrix, also conservative for near-unit poses.
                        double scale = Math.BitIncrement(Math.Sqrt(Math.Max(1, 1 + 4 * v2 * (v2 + w * w - 1))));
                        radius = Math.Max(radius, Math.BitIncrement(translation + scale * Shape(depth + 1)));
                    }
                    return radius;
                default: throw Corrupt();
            }
        }
    }
}
