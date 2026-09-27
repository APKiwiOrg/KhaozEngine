using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Reports how far a camera boom can extend before it meets an obstruction. <see cref="FollowCamera3D"/> asks
    /// it through <see cref="FollowCamera3D.BoomProbe"/> once per computed eye, from the pivot toward the geometric
    /// eye, and pulls the eye in to the reach it returns. Coordinates are absolute world, as everywhere on the
    /// camera. A probe over a rebased world converts on its own side.
    /// </summary>
    public interface ICameraBoomProbe
    {
        /// <summary>
        /// Free length of the boom from <paramref name="origin"/> along <paramref name="direction"/> for a sphere
        /// of <paramref name="radius"/>.
        /// </summary>
        /// <param name="origin">Boom start, the camera pivot, in absolute world space.</param>
        /// <param name="direction">Unit direction from the pivot toward the geometric eye.</param>
        /// <param name="length">Full boom length. The probe never needs to look past it.</param>
        /// <param name="radius">Radius of the sphere swept along the boom.</param>
        /// <returns>The free length in [0, <paramref name="length"/>]. <paramref name="length"/> means the boom
        /// is clear.</returns>
        float Reach(Vector3 origin, Vector3 direction, float length, float radius);
    }
}
