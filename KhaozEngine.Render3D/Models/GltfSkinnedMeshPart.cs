namespace KhaozEngine.Render3D
{
    /// <summary>One source material's directly indexed skinned geometry and decoded texture maps.
    /// Parts imported together share their skeleton and skin-order bind metadata.</summary>
    public sealed record GltfSkinnedMeshPart(SkinnedGltfMesh Mesh, GltfMaterialMaps Maps);
}
