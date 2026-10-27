using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;

namespace KhaozEngine.Tests.MapDoc;

internal static class NativeProxyFixtures
{
    internal static byte[] Coarsest(Vector3[] positions, bool sameMaterial = false)
    {
        byte[] glb = NativeRenderAssetFixtures.Glb();
        int length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var root = JsonNode.Parse(glb.AsSpan(20, length))!.AsObject();
        byte[] bin = glb.AsSpan(28 + length).ToArray();
        for (int part = 0; part < 2; part++)
        {
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            for (int v = 0; v < 3; v++)
            {
                var position = positions[part * 3 + v];
                min = Vector3.Min(min, position); max = Vector3.Max(max, position);
                int offset = (part * 3 + v) * 12;
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(offset), position.X);
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(offset + 4), position.Y);
                BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(offset + 8), position.Z);
            }
            root["accessors"]![part]!["min"] = new JsonArray(min.X, min.Y, min.Z);
            root["accessors"]![part]!["max"] = new JsonArray(max.X, max.Y, max.Z);
        }
        if (sameMaterial) root["materials"]![1] = root["materials"]![0]!.DeepClone();
        return NativeRenderAssetFixtures.Pack(root, bin);
    }
}
