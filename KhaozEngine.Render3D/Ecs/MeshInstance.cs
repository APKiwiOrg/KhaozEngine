using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The mesh + tint an entity renders with via <see cref="Scene3DBinder"/>. A zero <see cref="Tint"/> is
    /// treated as white (no tint), so <c>new MeshInstance { Mesh = handle }</c> renders the mesh untinted.
    /// </summary>
    public struct MeshInstance : IComponent
    {
        public MeshHandle Mesh;
        public Vector4 Tint;
    }
}
