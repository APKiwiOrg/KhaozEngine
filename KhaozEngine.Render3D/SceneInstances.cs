using System;
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

        /// <summary>As the dissolve overload, plus the complementary coverage phase used by a LOD handoff.
        /// A value of 0 keeps the ordinary noise ownership. A value of 1 keeps its exact complement in both the
        /// color and shadow passes.</summary>
        public void Add(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolveThreshold, float dissolveEdgeWidth, Color dissolveEdge, bool castsShadows,
            bool invertShadowDissolve, float dissolveComplement)
            => _items.Add(new Instance(mesh, world, tint, material, dissolveThreshold, dissolveEdgeWidth, dissolveEdge,
                castsShadows, invertShadowDissolve, dissolveComplement));

        /// <summary>Queue one SHADOW-ONLY instance (issue #974): it writes depth into the key light's cascade
        /// atlas and never draws in the colour pass, so the world keeps the shadow of geometry a view hides from
        /// the eye. The queued instance still casts, so it is never an opt-out, and the opposite pair (shadow-only
        /// with <c>castsShadows</c> false) is a contradiction the <see cref="Instance"/> constructor refuses.</summary>
        public void AddShadowOnly(MeshHandle mesh, Matrix4x4 world)
            => _items.Add(new Instance(mesh, world, Color.White, Material.None, shadowOnly: true));

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
            /// <summary>Complementary rigid-dissolve phase. Zero uses the ordinary keep decision. One inverts it in
            /// both color and shadow so two instances at the same threshold own every noise sample exactly once.</summary>
            public float DissolveComplement { get; }
            /// <summary>Whether this instance is drawn into the SHADOW depth pass ALONE (issue #974): it records
            /// depth for the cascade atlas and is masked out of the colour pass, so it throws a shadow and is never
            /// seen. The fourth caster policy, for geometry a view hides from the eye while the world still contains
            /// it (a tile world's roof over the building the observer stands in). CPU-side only, exactly like
            /// <see cref="CastsShadows"/>: it never reaches the GPU instance stream, so the uploaded bytes are
            /// identical either way. Shadow-only with <see cref="CastsShadows"/> false is a contradiction (an
            /// instance that draws nowhere at all) and the constructor refuses the pair.</summary>
            public bool ShadowOnly { get; }
            public Instance(MeshHandle mesh, Matrix4x4 world, Color tint) : this(mesh, world, tint, Material.None) { }
            public Instance(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
                float dissolveThreshold = 0f, float dissolveEdgeWidth = 0f, Vector4 dissolveEdge = default,
                bool castsShadows = true, bool invertShadowDissolve = false, float dissolveComplement = 0f,
                bool shadowOnly = false)
            {
                // Shadow-only says "colour pass no, depth pass yes" and the opt-out says "depth pass no", so the
                // pair asks for an instance that is drawn in neither: a queue entry that costs a slot and produces
                // nothing. Refused at the queue rather than dropped silently later, where it would read as a
                // missing shadow nobody could account for.
                if (shadowOnly && !castsShadows)
                    throw new ArgumentException(
                        "A shadow-only instance must cast shadows: shadowOnly with castsShadows false draws in neither pass.",
                        nameof(shadowOnly));
                Mesh = mesh; World = world; Tint = tint; Material = material;
                DissolveThreshold = dissolveThreshold; DissolveEdgeWidth = dissolveEdgeWidth; DissolveEdge = dissolveEdge;
                CastsShadows = castsShadows; InvertShadowDissolve = invertShadowDissolve;
                DissolveComplement = dissolveComplement; ShadowOnly = shadowOnly;
            }

            /// <summary>True when this draw carries a dissolve (routes through the gated ModelFrag term).</summary>
            public bool Dissolving => DissolveThreshold > 0f || DissolveComplement > 0f;
        }
    }

    public sealed partial class Scene3D
    {
        /// <summary>Queue one rigid dissolve instance with an explicit complementary coverage phase. The phase is
        /// shared by the color and shadow paths. The separate shadow-only inversion remains available for HLOD.</summary>
        public void Draw(MeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolve, float edgeWidth, Color edgeColor, bool castsShadows, bool invertShadowDissolve,
            float dissolveComplement)
            => _instances.Add(mesh, world, tint, material, dissolve, edgeWidth, edgeColor, castsShadows,
                invertShadowDissolve, dissolveComplement);
    }
}
