using System;
using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Submits every entity carrying both a <see cref="Transform3D"/> and a <see cref="MeshInstance"/> to a
    /// <see cref="Scene3D"/> as a draw, replacing the per-game "query entities -> compute matrix -> Draw" loop.
    /// Call once per frame between <see cref="Scene3D.Begin"/> and the surface render.
    /// </summary>
    public static class Scene3DBinder
    {
        /// <summary>Draw all renderable entities of <paramref name="world"/> into <paramref name="scene"/>,
        /// carrying each <see cref="MeshInstance.Material"/> through to the scene.</summary>
        public static void Submit(World world, Scene3D scene) =>
            Submit(world, (mesh, world2, tint, material) => scene.Draw(mesh, world2, tint, material));

        /// <summary>
        /// The pure core that carries material: for each entity with <see cref="Transform3D"/> +
        /// <see cref="MeshInstance"/>, invoke <paramref name="draw"/> with its mesh, world matrix, tint (zero
        /// tint -> white), and <see cref="MeshInstance.Material"/>. Headless-testable with a recording delegate.
        /// </summary>
        public static void Submit(World world, Action<MeshHandle, Matrix4x4, Vector4, Material> draw)
        {
            foreach (var e in world.Query().With<Transform3D>().With<MeshInstance>().Entities())
            {
                Transform3D t = world.Get<Transform3D>(e);
                MeshInstance m = world.Get<MeshInstance>(e);
                Vector4 tint = m.Tint == Vector4.Zero ? Vector4.One : m.Tint;
                draw(m.Mesh, t.ToMatrix(), tint, m.Material);
            }
        }

        /// <summary>
        /// The pure core: for each entity with <see cref="Transform3D"/> + <see cref="MeshInstance"/>, invoke
        /// <paramref name="draw"/> with its mesh, world matrix, and tint (zero tint -> white). Headless-testable
        /// with a recording delegate (no GPU). This overload is tint-only: the delegate signature carries no
        /// material, so use the <see cref="Scene3D"/> overload above when materials matter.
        /// </summary>
        public static void Submit(World world, Action<MeshHandle, Matrix4x4, Vector4> draw)
        {
            foreach (var e in world.Query().With<Transform3D>().With<MeshInstance>().Entities())
            {
                Transform3D t = world.Get<Transform3D>(e);
                MeshInstance m = world.Get<MeshInstance>(e);
                Vector4 tint = m.Tint == Vector4.Zero ? Vector4.One : m.Tint;
                draw(m.Mesh, t.ToMatrix(), tint);
            }
        }
    }
}
