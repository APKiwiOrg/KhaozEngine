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
        public Matrix4x4 ToMatrix()
        {
            Vector3 s = Scale == Vector3.Zero ? Vector3.One : Scale;
            Quaternion r = Rotation == default ? Quaternion.Identity : Rotation;
            return Matrix4x4.CreateScale(s)
                 * Matrix4x4.CreateFromQuaternion(r)
                 * Matrix4x4.CreateTranslation(Position);
        }
    }
}
