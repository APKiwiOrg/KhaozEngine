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

        /// <summary>As the dissolve overload, plus the per-instance shadow-caster opt-out (issue #287):
        /// <paramref name="castsShadows"/> false keeps this instance out of the shadow depth pass entirely (it still
        /// draws and still RECEIVES shadows), so a dense decorative layer can stop writing casters. True is the
        /// unchanged default every other overload queues.</summary>
        public void Add(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolveThreshold, float dissolveEdgeWidth, Color dissolveEdge, bool castsShadows)
            => _items.Add(new Instance(mesh, world, tint, material, dissolveThreshold, dissolveEdgeWidth, dissolveEdge, castsShadows));

        /// <summary>As the dissolve + opt-out overload, plus the inverted SHADOW dither (issue #391):
        /// <paramref name="invertShadowDissolve"/> true draws this instance's depth through the inverted dissolve
        /// pipeline, which keeps exactly what the plain one discards, so it complements a sibling instance dithering
        /// at the mirrored threshold instead of nesting inside it. For the merged half of an HLOD crossfade. Affects
        /// the SHADOW only: the colour pass is untouched, and false is the unchanged default everywhere else.</summary>
        public void Add(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolveThreshold, float dissolveEdgeWidth, Color dissolveEdge, bool castsShadows, bool invertShadowDissolve)
            => _items.Add(new Instance(mesh, world, tint, material, dissolveThreshold, dissolveEdgeWidth, dissolveEdge,
                castsShadows, invertShadowDissolve));

        public readonly struct Instance
        {
            public MeshHandle Mesh { get; }
            public Matrix4x4 World { get; }
            public Color Tint { get; }
            public Material Material { get; }
            // Rigid dissolve (issue #253): DissolveThreshold 0 = no dissolve (byte-identical old path). A value > 0 folds the
            // noise discard + emissive edge into ModelFrag (InstanceData.Dissolve = (threshold, edge width), and the
            // edge colour is substituted onto InstanceData.Emissive). Mirrors SkinnedSceneInstances.Instance.
            public float DissolveThreshold { get; }
            public float DissolveEdgeWidth { get; }
            public Vector4 DissolveEdge { get; }
            /// <summary>Whether this instance writes into the key light's shadow depth pass (issue #287). True on
            /// every ctor that does not say otherwise, so the whole pre-flag path is unchanged. False keeps the
            /// instance out of the depth pass while it still draws and still receives shadows: the per-layer
            /// casts-shadows policy a consumer sets on dense decorative geometry. CPU-side only - it never reaches
            /// the GPU instance stream, so the uploaded bytes are identical either way.</summary>
            public bool CastsShadows { get; }
            /// <summary>Whether this instance's SHADOW dither is inverted (issue #391): the depth pass keeps exactly
            /// what the plain dissolve discards, so it complements a sibling dithering at the mirrored threshold
            /// instead of nesting inside it. Set on the merged half of an HLOD crossfade. Like
            /// <see cref="CastsShadows"/> this is CPU-side only - it selects a depth pipeline and never reaches the
            /// GPU instance stream, so the uploaded bytes and the whole COLOUR pass are identical either way. Only
            /// meaningful while <see cref="Dissolving"/>.</summary>
            public bool InvertShadowDissolve { get; }
            public Instance(MeshHandle mesh, Matrix4x4 world, Color tint) : this(mesh, world, tint, Material.None) { }
            public Instance(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
                float dissolveThreshold = 0f, float dissolveEdgeWidth = 0f, Vector4 dissolveEdge = default,
                bool castsShadows = true, bool invertShadowDissolve = false)
            {
                Mesh = mesh; World = world; Tint = tint; Material = material;
                DissolveThreshold = dissolveThreshold; DissolveEdgeWidth = dissolveEdgeWidth; DissolveEdge = dissolveEdge;
                CastsShadows = castsShadows; InvertShadowDissolve = invertShadowDissolve;
            }

            /// <summary>True when this draw carries a dissolve (routes through the gated ModelFrag term).</summary>
            public bool Dissolving => DissolveThreshold > 0f;
        }
    }
}
