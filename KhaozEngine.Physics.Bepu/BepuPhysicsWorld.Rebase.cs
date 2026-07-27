using System.Numerics;
using BepuPhysics;
using KhaozEngine.Physics;

using BepuStaticHandle = BepuPhysics.StaticHandle;
using BepuBodyHandle = BepuPhysics.BodyHandle;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The floating-origin half of the Bepu backend: re-expressing a live world against a new origin, in
/// place, between steps. Separated from the main file because it is one self-contained operation with a long
/// rationale, and because the main file is close to the size ratchet.</summary>
public sealed partial class BepuPhysicsWorld
{
    // The world-space point this world's coordinates are expressed against. Vector3.Zero until something rebases,
    // which is what makes an unframed game byte-identical to the pre-rebase backend.
    private Vector3 _origin;

    /// <inheritdoc/>
    public Vector3 Origin => _origin;

    /// <inheritdoc/>
    public bool CanRebase => true;

    /// <summary>
    /// Re-express every static, every body (awake AND sleeping) and every world-space constraint anchor against
    /// <paramref name="newOrigin"/>, then adopt it. Contents and <see cref="Origin"/> move together, so the world is
    /// never left half-rebased.
    /// <para>
    /// This is a bulk of direct pose writes plus broadphase refits, NOT a remove-and-re-add: <c>BodyReference.Pose</c>
    /// and <c>StaticReference.Pose</c> are ref-returning in Bepu 2.4, and <c>UpdateBounds</c> refits the broadphase
    /// for the new pose without waking anything. Sleep state, contacts, velocities and constraints all survive, so
    /// nothing inside the simulation can observe the shift. Enumerating <c>Bodies.Sets</c> rather than the active set
    /// alone is what covers SLEEPING bodies (set 0 is the active island, later allocated sets are the inactive ones)
    /// and the shapeless kinematic anchor bodies a world-space constraint end creates.
    /// </para>
    /// <para>
    /// <c>Statics.ApplyDescription</c> is the obvious API and is the wrong one: its own doc says it forces every
    /// sleeping body whose bounds overlap the old or new collidable active, so a rebase through it would wake the
    /// entire sleeping population of the world on every shift. The direct pose write plus
    /// <c>Statics.UpdateBounds</c> does not.
    /// </para>
    /// <para>
    /// Constraints need nothing: <c>ConstraintFactory</c> converts world poses into body-LOCAL offsets at build
    /// time, so a uniform translate of both ends preserves every joint exactly.
    /// </para>
    /// </summary>
    public void Rebase(Vector3 newOrigin)
    {
        Vector3 delta = _origin - newOrigin;
        _origin = newOrigin;
        if (delta == Vector3.Zero) return;   // adopting the origin you already have moves nothing

        // Bodies: every allocated set, so awake and sleeping alike. A sleeping body written this way stays asleep
        // and does not move on the next step; that is the whole reason the poses are written directly.
        for (int setIndex = 0; setIndex < _sim.Bodies.Sets.Length; setIndex++)
        {
            ref BodySet set = ref _sim.Bodies.Sets[setIndex];
            if (!set.Allocated) continue;
            for (int i = 0; i < set.Count; i++)
            {
                BepuBodyHandle handle = set.IndexToHandle[i];
                BodyReference body = _sim.Bodies[handle];
                body.Pose.Position += delta;
                body.UpdateBounds();
            }
        }

        // Statics: index order, resolved to handles, because UpdateBounds takes a handle.
        for (int i = _sim.Statics.Count - 1; i >= 0; i--)
        {
            BepuStaticHandle handle = _sim.Statics.IndexToHandle[i];
            _sim.Statics[handle].Pose.Position += delta;
            _sim.Statics.UpdateBounds(handle);
        }
    }
}
