using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A camera that can build its view against a RENDER ORIGIN, so the GPU never sees a 100 km translation meeting
    /// another 100 km translation inside a matrix concatenation. Implemented by <see cref="FollowCamera3D"/>,
    /// <see cref="FlyCamera3D"/> and <see cref="IsoCamera3D"/>.
    /// <para>
    /// <see cref="Scene3D"/> writes <see cref="RenderOrigin"/> once per frame from its own latched
    /// <c>Scene3D.RenderOrigin</c>, and falls back to the WHOLE pre-release absolute path (origin
    /// <see cref="Vector3.Zero"/> for the geometry AND the view) when the active camera does not implement this, so
    /// a consumer's own <see cref="IIsoCamera3D"/> keeps working unchanged and can never end up with its geometry
    /// and its view in different spaces. <c>Scene3D.RenderOriginActive</c> reports which path a frame took.
    /// </para>
    /// <para>
    /// Why this is not a member of <see cref="IIsoCamera3D"/>: a settable origin needs backing storage, so it cannot
    /// be a default interface member, so putting it there would be a breaking interface change for every consumer
    /// camera. And why <see cref="Scene3D"/> cannot simply prepend a translation to the camera's
    /// <c>ViewProjection</c> instead: <c>View</c>'s translation row is <c>-dot(axis, Eye)</c>, roughly 1e5 at
    /// 100 km, so prepending computes the difference of two large nearly equal float32 values, which is exactly the
    /// cancellation this interface exists to remove. The subtraction has to happen on the EYE, before the look-at is
    /// built, and only the camera can do that.
    /// </para>
    /// </summary>
    public interface IRenderOriginAware
    {
        /// <summary>The render origin subtracted from the eye and the look target when building <c>View</c>. Set by
        /// <see cref="Scene3D"/> each frame; <see cref="Vector3.Zero"/> (the default) reproduces the pre-release
        /// matrices bit for bit. <see cref="IIsoCamera3D.Eye"/> stays ABSOLUTE world: culling, the shadow-cascade
        /// fit and the origin choice itself all need it.</summary>
        Vector3 RenderOrigin { get; set; }

        /// <summary>The pre-shift view-projection, i.e. what <see cref="IIsoCamera3D.ViewProjection"/> returned
        /// before this interface existed. <see cref="Scene3D"/> uses it for every CPU-side spatial computation that
        /// runs against absolute bounds (frustum culling, shadow-cascade fitting, caster classification), so those
        /// paths stay byte-identical to the pre-release engine at any origin.</summary>
        Matrix4x4 AbsoluteViewProjection { get; }
    }
}
