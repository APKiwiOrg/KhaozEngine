using System;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class AttachmentGlb
{
    public static void ValidateRigidEmbedded(ReadOnlyMemory<byte> glb, string sourceName) => Validate(glb, sourceName, container =>
    {
        JsonElement root = container.Root;
        foreach (JsonElement _ in Array(root, "skins"))
            throw new AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal.SkinPresent, "Skin data is not rigid geometry.");
        foreach (JsonElement _ in Array(root, "animations"))
            throw new AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal.AnimationPresent, "Animated geometry is not a rigid attachment.");
        int nodeIndex = 0;
        foreach (JsonElement node in Array(root, "nodes"))
        {
            if (node.TryGetProperty("skin", out _) || node.TryGetProperty("weights", out _))
                throw new AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal.NodeDeformationPresent, "Node skin or morph data is not rigid geometry.")
                {
                    NodeIndex = nodeIndex,
                    HasSkin = node.TryGetProperty("skin", out _),
                    HasWeights = node.TryGetProperty("weights", out _),
                };
            nodeIndex++;
        }
        int meshIndex = 0;
        foreach (JsonElement mesh in Array(root, "meshes"))
        {
            if (mesh.TryGetProperty("weights", out _))
                throw new AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal.MeshMorphWeightsPresent, "Mesh morph data is not rigid geometry.")
                {
                    MeshIndex = meshIndex,
                };
            int primitiveIndex = 0;
            foreach (JsonElement primitive in Array(mesh, "primitives"))
            {
                if (primitive.TryGetProperty("targets", out _))
                    throw new AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal.PrimitiveMorphTargetsPresent, "Morph targets are not rigid geometry.")
                    {
                        MeshIndex = meshIndex,
                        PrimitiveIndex = primitiveIndex,
                    };
                if (primitive.TryGetProperty("attributes", out JsonElement attributes))
                    foreach (JsonProperty attribute in attributes.EnumerateObject())
                        if (attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) || attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal))
                            throw new AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal.SkinAttributePresent, "Skin vertex attributes are not rigid geometry.")
                            {
                                MeshIndex = meshIndex,
                                PrimitiveIndex = primitiveIndex,
                                AttributeName = attribute.Name,
                            };
                primitiveIndex++;
            }
            meshIndex++;
        }
    });
}
