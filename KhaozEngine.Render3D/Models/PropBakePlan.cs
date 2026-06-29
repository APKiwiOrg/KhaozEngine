using KhaozEngine.Collision;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D
{
    /// <summary>What <c>ke-propbake</c> emits for one prop: a collision shape (always) and an optional walkable
    /// top-surface heightmap (only for walkable-solid props). Pure decision (no file IO) so the tool stays thin
    /// and the tree-gets-coll-not-surf rule is unit-testable without a glTF fixture.</summary>
    public readonly record struct PropBakePlan(PhysicsShape Coll, PropSurface? Surface)
    {
        /// <summary>Plan the bakes for a <see cref="PropLoader"/>-normalized mesh: every prop gets a
        /// <see cref="PropCollisionBake"/> collision shape; only an <see cref="PropSurfaceBake.IsWalkableSolid"/>
        /// prop also gets a <see cref="PropSurfaceBake.Bake"/> surface (a tree has no walkable top).</summary>
        public static PropBakePlan For(GltfMesh mesh) => new(
            PropCollisionBake.Bake(mesh),
            PropSurfaceBake.IsWalkableSolid(mesh) ? PropSurfaceBake.Bake(mesh) : null);
    }
}
