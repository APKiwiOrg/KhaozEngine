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
            // Teleport CharDissolve: DissolveThreshold 0 = no dissolve (normal pipeline), and > 0 routes this draw through
            // the dissolve pipeline variant (SpecParams.z=threshold, .w=edge width, Emissive=edge colour).
            public readonly float DissolveThreshold;
            public readonly float DissolveEdgeWidth;
            public readonly Vector4 DissolveEdge;
            // Shadow-caster opt-out (issue #387): false keeps this draw out of the key light's depth pass while it
            // still draws and receives. CPU-side only, like SceneInstances.Instance.CastsShadows.
            public readonly bool CastsShadows;
            // Motion key (TEMPORAL-FOUNDATIONS-DESIGN section 3): which body this is across frames. CPU-side only,
            // like CastsShadows. None from every constructor except the descriptor's. Only Scene3D.DrawSkinned with a
            // descriptor records it into motion history, so a key on a standalone queue is never recorded.
            public readonly MotionKey Motion;
            public Instance(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material,
                float dissolveThreshold = 0f, float dissolveEdgeWidth = 0f, Vector4 dissolveEdge = default,
                bool castsShadows = true)
                : this(mesh, world, tint, material, dissolveThreshold, dissolveEdgeWidth, dissolveEdge, castsShadows,
                    MotionKey.None) { }

            /// <summary>One queued draw from its descriptor, motion key included.</summary>
            internal Instance(in SkinnedInstanceDraw draw)
                : this(draw.Mesh, draw.Model, draw.Tint, draw.Material, draw.Dissolve, draw.DissolveEdgeWidth,
                    draw.DissolveEdgeColor, draw.CastsShadows, draw.Motion) { }

            Instance(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material, float dissolveThreshold,
                float dissolveEdgeWidth, Vector4 dissolveEdge, bool castsShadows, MotionKey motion)
            {
                Mesh = mesh; World = world; Tint = tint; Material = material;
                DissolveThreshold = dissolveThreshold; DissolveEdgeWidth = dissolveEdgeWidth; DissolveEdge = dissolveEdge;
                CastsShadows = castsShadows; Motion = motion;
            }

            /// <summary>True when this draw should go through the dissolve pipeline variant.</summary>
            public bool Dissolving => DissolveThreshold > 0f;
        }

        readonly List<Instance> _items = new();
        public IReadOnlyList<Instance> Items => _items;
        public void Begin() => _items.Clear();
        public void Add(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material)
            => _items.Add(new Instance(mesh, world, tint, material));

        /// <summary>Queue a skinned draw with CharDissolve params (see <see cref="Instance"/>).</summary>
        public void Add(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolveThreshold, float dissolveEdgeWidth, Color dissolveEdge)
            => _items.Add(new Instance(mesh, world, tint, material, dissolveThreshold, dissolveEdgeWidth, dissolveEdge));

        /// <summary>Queue a skinned draw with CharDissolve params and the shadow-caster opt-out (issue #387):
        /// <paramref name="castsShadows"/> false keeps it out of the depth pass. <c>true</c> is the overload above.</summary>
        public void Add(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material,
            float dissolveThreshold, float dissolveEdgeWidth, Color dissolveEdge, bool castsShadows)
            => _items.Add(new Instance(mesh, world, tint, material, dissolveThreshold, dissolveEdgeWidth, dissolveEdge,
                castsShadows));

        /// <summary>Queue a skinned draw from its descriptor, every knob plus the motion key. The key rides as data
        /// only: motion history is recorded by
        /// <see cref="Scene3D.DrawSkinned(in SkinnedInstanceDraw, System.ReadOnlySpan{System.Numerics.Matrix4x4})"/>,
        /// so a key queued on a standalone queue is never recorded.</summary>
        public void Add(in SkinnedInstanceDraw draw) => _items.Add(new Instance(in draw));
    }
}
