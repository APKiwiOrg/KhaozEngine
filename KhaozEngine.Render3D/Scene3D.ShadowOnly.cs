using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// How one grouped instance slot takes part in the MAIN (colour) pass. Three-way rather than a bool because
    /// shadow-only geometry is neither drawn nor culled: it was never a candidate for the camera to reject, so
    /// counting it as culled would report a culling win that did not happen.
    /// </summary>
    enum MainPassSlot : byte
    {
        /// <summary>Inside the frustum and not withheld: drawn, and counted by <see cref="Scene3D.DrawnInstances"/>.</summary>
        Drawn = 0,
        /// <summary>Rejected by camera-frustum culling, counted by <see cref="Scene3D.CulledInstances"/>.</summary>
        Culled = 1,
        /// <summary>Queued for the depth pass alone, counted by <see cref="Scene3D.ShadowOnlyInstances"/> and by
        /// neither of the other two.</summary>
        ShadowOnly = 2,
    }

    /// <summary>
    /// The SHADOW-ONLY caster policy (issue #974): an instance that writes depth into the key light's cascade atlas
    /// and never draws in the colour pass.
    /// <para>
    /// It is the fourth caster policy, after the per-instance opt-out (issue #287, draws and casts nothing), the
    /// dissolving caster (14.5.0, whose shadow erodes with its mesh) and the inverted dissolve (issue #391, the
    /// merged half of an HLOD crossfade). The three that came before all answer "how does this VISIBLE instance
    /// cast", and this one answers the question none of them could: how does geometry a VIEW HIDES FROM THE EYE,
    /// while the world still contains it, keep throwing the shadow it physically throws.
    /// </para>
    /// <para>
    /// The first consumer is a tile world's hidden roof. <c>TileWorldView</c> withholds the roofs over the building
    /// the observer stands in (and every roof under a roofs-off setting) so the camera can see inside, and before
    /// this policy those placements never reached the scene at all, so the depth pass never saw them and the sun
    /// landed on the interior floor. Queueing them here puts the roof back into the cascade atlas and nowhere else,
    /// which is what makes an indoor room read as indoors.
    /// </para>
    /// <para>
    /// Deliberately NOT a fourth <see cref="ShadowCastKind"/>. That enum says which DEPTH pipeline draws an
    /// instance, and shadow-only changes nothing about the depth pipeline: a shadow-only instance classifies
    /// exactly as it would have if it were visible (opaque, or dissolving either way). What it changes is the
    /// COLOUR pass, so it rides the main-pass visibility mask instead, and the caster walk, the per-cascade cull
    /// and the caster signature are all untouched.
    /// </para>
    /// <para>
    /// CPU-side only. The flag never reaches the GPU instance stream, so the uploaded bytes for a shadow-only
    /// instance are identical to the ordinary draw it would otherwise have been, and a frame that queues none of
    /// them renders byte-for-byte as before.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // Per-instance shadow-only flags, index-aligned to _instanceData (filled by GroupInstances, the one place
        // that still knows each uploaded slot's source instance, exactly as _instanceCastKinds is). Reused, never
        // per-frame allocated.
        readonly List<bool> _instanceShadowOnly = new();

        int _shadowOnlyInstances;

        /// <summary>Number of mesh instances queued through <see cref="DrawShadowOnly"/> that reached the uploaded
        /// stream last frame: they recorded depth for the key light and drew nothing in the colour pass. Counted by
        /// neither <see cref="DrawnInstances"/> nor <see cref="CulledInstances"/>, because a shadow-only instance
        /// was never a main-pass candidate the camera could reject. Zero until the first rendered frame.</summary>
        public int ShadowOnlyInstances => _shadowOnlyInstances;

        /// <summary>
        /// Queue one instance into the SHADOW depth pass ALONE: it writes depth into the key light's cascade atlas
        /// and is masked out of the colour pass, so it casts a shadow and is never seen. For geometry a view hides
        /// from the eye while the world still contains it (the tile world's hidden roof is the first consumer).
        /// <para>
        /// Everything else about the instance is the ordinary draw: the same world transform, the same mesh, the
        /// same per-cascade caster cull and the same depth pipeline it would have used while visible. It is never
        /// an opt-out, so it is counted by <see cref="ShadowOnlyInstances"/> rather than by
        /// <see cref="DrawnInstances"/> or <see cref="CulledInstances"/>, and it is NOT frustum-rejected before
        /// packing the way an explicit non-caster is, because an offscreen caster still throws an onscreen shadow.
        /// </para>
        /// </summary>
        /// <param name="mesh">The already-uploaded mesh to record depth for.</param>
        /// <param name="world">The world transform to record it at.</param>
        public void DrawShadowOnly(MeshHandle mesh, Matrix4x4 world) => Draw(new RigidInstanceDraw(mesh, world) { ShadowOnly = true });

        /// <summary>How one grouped slot takes part in the main pass, from the shadow-only flag and the frustum
        /// result. Shadow-only WINS over the frustum test, and is reached on the culling-off parity path too, so
        /// a shadow-only instance is invisible whichever way <see cref="FrustumCulling"/> is set. Pure, so the
        /// mask fill and the tests read one definition of the rule.</summary>
        internal static MainPassSlot ClassifyMainPassSlot(bool shadowOnly, bool insideFrustum)
            => shadowOnly ? MainPassSlot.ShadowOnly
             : insideFrustum ? MainPassSlot.Drawn
             : MainPassSlot.Culled;
    }
}
