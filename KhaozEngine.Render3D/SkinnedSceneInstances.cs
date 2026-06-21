using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>The per-frame skinned-draw queue: one entry per <see cref="Scene3D.DrawSkinned"/> call, holding
    /// the mesh handle, model transform, tint, material, and the bone offset (the start index of this draw's
    /// composed bone matrices in Scene3D's shared per-frame bone buffer). Mirrors <see cref="SceneInstances"/>.</summary>
    public sealed class SkinnedSceneInstances
    {
        public readonly struct Instance
        {
            public readonly SkinnedMeshHandle Mesh;
            public readonly Matrix4x4 World;
            public readonly Vector4 Tint;             // stored as Vector4 (Color converts implicitly), like SceneInstances
            public readonly Material Material;
            public readonly uint BoneOffset;
            // Take Color (implicitly stored as Vector4) to mirror SceneInstances.Instance exactly.
            public Instance(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material, uint boneOffset)
            {
                Mesh = mesh; World = world; Tint = tint; Material = material; BoneOffset = boneOffset;
            }
        }

        readonly List<Instance> _items = new();
        public IReadOnlyList<Instance> Items => _items;
        public void Begin() => _items.Clear();
        public void Add(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material, uint boneOffset)
            => _items.Add(new Instance(mesh, world, tint, material, boneOffset));
    }
}
