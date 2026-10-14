using System;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class AttachmentGlb
{
    public static void ValidateRigidEmbedded(ReadOnlyMemory<byte> glb, string sourceName) => Validate(glb, sourceName, container =>
    {
        JsonElement root = container.Root;
        foreach (JsonElement _ in Array(root, "skins"))
            throw new InvalidDataException("Skin data is not rigid geometry.");
        foreach (JsonElement _ in Array(root, "animations"))
            throw new InvalidDataException("Animated geometry is not a rigid attachment.");
        foreach (JsonElement node in Array(root, "nodes"))
            if (node.TryGetProperty("skin", out _) || node.TryGetProperty("weights", out _))
                throw new InvalidDataException("Node skin or morph data is not rigid geometry.");
        foreach (JsonElement mesh in Array(root, "meshes"))
        {
            if (mesh.TryGetProperty("weights", out _))
                throw new InvalidDataException("Mesh morph data is not rigid geometry.");
            foreach (JsonElement primitive in Array(mesh, "primitives"))
            {
                if (primitive.TryGetProperty("targets", out _))
                    throw new InvalidDataException("Morph targets are not rigid geometry.");
                if (primitive.TryGetProperty("attributes", out JsonElement attributes))
                    foreach (JsonProperty attribute in attributes.EnumerateObject())
                        if (attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) || attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal))
                            throw new InvalidDataException("Skin vertex attributes are not rigid geometry.");
            }
        }
    });
}
