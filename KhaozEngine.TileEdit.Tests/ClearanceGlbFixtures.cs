using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Hand-built binary glTF files for the clearance tool tests, positions only, which is all the loader
/// needs. One mesh under one node, so a node transform is the only thing between the written and the loaded
/// positions. A copy of the render tests' fixture, since test projects share no code.</summary>
internal static class ClearanceGlbFixtures
{
    const int TrianglesMode = 4;

    // Two triangles per face, wound outward. Corner bit 0 is x, bit 1 is y, bit 2 is z.
    static readonly int[] BoxIndices =
    {
        0, 2, 3, 0, 3, 1, // -z
        4, 5, 7, 4, 7, 6, // +z
        0, 4, 6, 0, 6, 2, // -x
        1, 3, 7, 1, 7, 5, // +x
        0, 1, 5, 0, 5, 4, // -y
        2, 6, 7, 2, 7, 3, // +y
    };

    /// <summary>Write an axis-aligned box from <paramref name="min"/> to <paramref name="max"/>: 8 corners and 12
    /// triangles with an index accessor when <paramref name="indexed"/>, else the 36 triangle corners written out
    /// with none. The node carries <paramref name="translation"/> and <paramref name="scale"/> when given.</summary>
    public static void WriteBox(string path, Vector3 min, Vector3 max, bool indexed = true,
        Vector3? translation = null, Vector3? scale = null)
    {
        var corners = new Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);

        if (indexed)
        {
            WriteGlb(path, corners, BoxIndices, TrianglesMode, translation, scale);
            return;
        }

        var soup = new Vector3[BoxIndices.Length];
        for (int i = 0; i < soup.Length; i++) soup[i] = corners[BoxIndices[i]];
        WriteGlb(path, soup, indices: null, TrianglesMode, translation, scale);
    }

    static void WriteGlb(string path, IReadOnlyList<Vector3> positions, IReadOnlyList<int>? indices, int mode,
        Vector3? translation, Vector3? scale)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int positionBytes = positions.Count * 3 * sizeof(float);
        int indexBytes = indices is null ? 0 : indices.Count * sizeof(uint);
        byte[] bin = new byte[positionBytes + indexBytes];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 p = positions[i];
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            BitConverter.TryWriteBytes(bin.AsSpan((i * 3) * sizeof(float)), p.X);
            BitConverter.TryWriteBytes(bin.AsSpan((i * 3 + 1) * sizeof(float)), p.Y);
            BitConverter.TryWriteBytes(bin.AsSpan((i * 3 + 2) * sizeof(float)), p.Z);
        }

        if (indices is not null)
        {
            for (int i = 0; i < indices.Count; i++)
                BitConverter.TryWriteBytes(bin.AsSpan(positionBytes + i * sizeof(uint)), (uint)indices[i]);
        }

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string V(Vector3 v) => $"[{F(v.X)},{F(v.Y)},{F(v.Z)}]";

        var node = new StringBuilder("{\"mesh\":0");
        if (translation is Vector3 t) node.Append(",\"translation\":").Append(V(t));
        if (scale is Vector3 s) node.Append(",\"scale\":").Append(V(s));
        node.Append('}');

        string primitive = indices is null
            ? $"{{\"attributes\":{{\"POSITION\":0}},\"mode\":{mode}}}"
            : $"{{\"attributes\":{{\"POSITION\":0}},\"indices\":1,\"mode\":{mode}}}";
        string bufferViews = $"{{\"buffer\":0,\"byteLength\":{positionBytes},\"target\":34962}}";
        string accessors = $"{{\"bufferView\":0,\"componentType\":5126,\"count\":{positions.Count},\"type\":\"VEC3\"," +
            $"\"min\":{V(min)},\"max\":{V(max)}}}";
        if (indices is not null)
        {
            bufferViews += $",{{\"buffer\":0,\"byteOffset\":{positionBytes},\"byteLength\":{indexBytes},\"target\":34963}}";
            accessors += $",{{\"bufferView\":1,\"componentType\":5125,\"count\":{indices.Count},\"type\":\"SCALAR\"}}";
        }

        string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}]," +
            $"\"nodes\":[{node}],\"meshes\":[{{\"primitives\":[{primitive}]}}]," +
            $"\"buffers\":[{{\"byteLength\":{bin.Length}}}],\"bufferViews\":[{bufferViews}],\"accessors\":[{accessors}]}}";
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonLength = (jsonBytes.Length + 3) & ~3;

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonLength + 8 + bin.Length));
        w.Write((uint)jsonLength);
        w.Write(0x4E4F534Au);
        w.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < jsonLength; i++) w.Write((byte)' ');
        w.Write((uint)bin.Length);
        w.Write(0x004E4942u);
        w.Write(bin);
    }
}
