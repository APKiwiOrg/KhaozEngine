using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The per-frame instance queue for <see cref="Scene3D"/>: <see cref="Begin"/> clears it, <see cref="Add(KhaozEngine.Render3D.MeshHandle, System.Numerics.Matrix4x4, KhaozEngine.Primitives.Color)"/>
    /// queues one (mesh, world) draw, and the renderer consumes <see cref="Items"/> in submission order. Pure /
    /// headless so the queueing is testable without a GPU.
    /// </summary>
    public sealed class SceneInstances
    {
        readonly List<Instance> _items = new();
        public IReadOnlyList<Instance> Items => _items;

        public void Begin() => _items.Clear();
        public void Add(MeshHandle mesh, Matrix4x4 world, Color tint) => _items.Add(new Instance(mesh, world, tint));
        public void Add(MeshHandle mesh, Matrix4x4 world, Color tint, Material material) => _items.Add(new Instance(mesh, world, tint, material));

        /// <summary>Queue one instance with rigid-dissolve params (issue #253). Mirrors the
        /// <see cref="SkinnedSceneInstances"/> dissolve overload: <paramref name="dissolveThreshold"/> 0 = fully
        /// drawn (the byte-identical old path), &gt; 0 folds the noise discard + emissive edge into ModelFrag, with
        /// <paramref name="dissolveEdge"/> as the edge colour (it rides InstanceData.Emissive engine-side).</summary>
        public void Add(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolveThreshold, float dissolveEdgeWidth, Color dissolveEdge)
            => _items.Add(new Instance(mesh, world, tint, material, dissolveThreshold, dissolveEdgeWidth, dissolveEdge));

        public readonly struct Instance
        {
            public MeshHandle Mesh { get; }
            public Matrix4x4 World { get; }
            public Color Tint { get; }
            public Material Material { get; }
            // Rigid dissolve (issue #253): DissolveThreshold 0 = no dissolve (byte-identical old path); > 0 folds the
            // noise discard + emissive edge into ModelFrag (InstanceData.Dissolve = (threshold, edge width), and the
            // edge colour is substituted onto InstanceData.Emissive). Mirrors SkinnedSceneInstances.Instance.
            public float DissolveThreshold { get; }
            public float DissolveEdgeWidth { get; }
            public Vector4 DissolveEdge { get; }
            public Instance(MeshHandle mesh, Matrix4x4 world, Color tint) : this(mesh, world, tint, Material.None) { }
            public Instance(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
                float dissolveThreshold = 0f, float dissolveEdgeWidth = 0f, Vector4 dissolveEdge = default)
            {
                Mesh = mesh; World = world; Tint = tint; Material = material;
                DissolveThreshold = dissolveThreshold; DissolveEdgeWidth = dissolveEdgeWidth; DissolveEdge = dissolveEdge;
            }

            /// <summary>True when this draw carries a dissolve (routes through the gated ModelFrag term).</summary>
            public bool Dissolving => DissolveThreshold > 0f;
        }
    }
}
