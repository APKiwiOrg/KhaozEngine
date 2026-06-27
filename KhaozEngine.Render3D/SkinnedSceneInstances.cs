using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>The per-frame skinned-draw queue: one entry per <see cref="Scene3D.DrawSkinned(KhaozEngine.Render3D.SkinnedMeshHandle, System.ReadOnlySpan{System.Numerics.Matrix4x4}, System.Numerics.Matrix4x4, KhaozEngine.Primitives.Color)"/> call, holding the
    /// mesh handle, model transform, tint, and material. Each entry is drawn separately (its bone palette lives in
    /// slot i of the shared bone buffer, selected by a per-draw dynamic offset). Mirrors <see cref="SceneInstances"/>.</summary>
    public sealed class SkinnedSceneInstances
    {
        public readonly struct Instance
        {
            public readonly SkinnedMeshHandle Mesh;
            public readonly Matrix4x4 World;
            public readonly Vector4 Tint;             // stored as Vector4 (Color converts implicitly), like SceneInstances
            public readonly Material Material;
            public Instance(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material)
            {
                Mesh = mesh; World = world; Tint = tint; Material = material;
            }
        }

        readonly List<Instance> _items = new();
        public IReadOnlyList<Instance> Items => _items;
        public void Begin() => _items.Clear();
        public void Add(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material)
            => _items.Add(new Instance(mesh, world, tint, material));
    }
}
