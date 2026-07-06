using KhaozEngine.Ecs;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The mesh + tint + material an entity renders with via <see cref="Scene3DBinder"/>. A zero
    /// <see cref="Tint"/> is treated as white (no tint), so <c>new MeshInstance { Mesh = handle }</c> renders the
    /// mesh untinted. An unset <see cref="Material"/> is matte (zero emissive, zero specular), functionally
    /// equivalent to <see cref="Render3D.Material.None"/> since zero specular makes shininess irrelevant, so
    /// existing entities are unchanged.
    /// </summary>
    public struct MeshInstance : IComponent
    {
        public MeshHandle Mesh;
        public Color Tint;
        public Material Material;
    }
}
