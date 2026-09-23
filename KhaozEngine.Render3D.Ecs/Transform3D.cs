using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A 3D world transform for an entity rendered via <see cref="Scene3DBinder"/>. Position is always used;
    /// a zero <see cref="Scale"/> is treated as 1 and a zero <see cref="Rotation"/> as identity, so
    /// <c>new Transform3D { Position = p }</c> just works with object-initializer syntax (struct defaults are
    /// zero). Pure System.Numerics.
    /// </summary>
    public struct Transform3D : IComponent
    {
        public Vector3 Position;
        public Vector3 Scale;
        public Quaternion Rotation;

        /// <summary>World matrix: scale, then rotation, then translation (zero scale/rotation treated as identity).</summary>
        public Matrix4x4 ToMatrix() => ToMatrix(Vector3.Zero);

        /// <summary>
        /// As <see cref="ToMatrix()"/>, but built against <paramref name="renderOrigin"/>: the translation is
        /// <c><see cref="Position"/> - renderOrigin</c>, so the matrix is already camera-relative.
        /// <para>
        /// A CONVENIENCE, not a requirement. Every <c>Scene3D</c> entry point takes an ABSOLUTE world matrix and
        /// reduces it itself on the way to the GPU, so a consumer never has to call this. It exists for a consumer
        /// that wants to build the reduced matrix once and keep it (a cached transform, an editor gizmo), and it is
        /// exact whenever <paramref name="renderOrigin"/> is a <c>WorldFrame</c> anchor and the object is near the
        /// eye. Reducing a matrix that <c>Scene3D</c> will also reduce double-subtracts the origin.
        /// </para>
        /// </summary>
        public Matrix4x4 ToMatrix(Vector3 renderOrigin)
        {
            Vector3 s = Scale == Vector3.Zero ? Vector3.One : Scale;
            Quaternion r = Rotation == default ? Quaternion.Identity : Rotation;
            return Matrix4x4.CreateScale(s)
                 * Matrix4x4.CreateFromQuaternion(r)
                 * Matrix4x4.CreateTranslation(Position - renderOrigin);
        }
    }
}
