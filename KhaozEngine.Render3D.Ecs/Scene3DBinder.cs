using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Submits every entity carrying both a <see cref="Transform3D"/> and a <see cref="MeshInstance"/> to a
    /// <see cref="Scene3D"/> as a draw, replacing the per-game "query entities -> compute matrix -> Draw" loop.
    /// Call once per frame between <see cref="Scene3D.Begin"/> and the surface render. Each draw carries the entity's
    /// motion key (<see cref="MotionKeyOf"/>), so an entity that moves reports its own motion to temporal rendering.
    /// </summary>
    public static class Scene3DBinder
    {
        // The Combine part every entity key is mixed with: a key space of the binder's own ("ECS1"), so entity keys
        // scatter away from the small ids a game passes to MotionKey.From and from the keys other helpers derive.
        const uint EntityKeyPart = 0x4543_5331u;

        /// <summary>Draw all renderable entities of <paramref name="world"/> into <paramref name="scene"/>,
        /// carrying each <see cref="MeshInstance.Material"/> through and keying each draw with
        /// <see cref="MotionKeyOf"/>.</summary>
        public static void Submit(World world, Scene3D scene) => Submit(world, draw => scene.Draw(in draw));

        /// <summary>
        /// The keyed pure core: for each entity with <see cref="Transform3D"/> + <see cref="MeshInstance"/>, invoke
        /// <paramref name="draw"/> with a <see cref="RigidInstanceDraw"/> of its mesh, world matrix, tint (zero tint
        /// -> white), <see cref="MeshInstance.Material"/> and <see cref="MotionKeyOf"/> key. Every other knob keeps the
        /// descriptor's default. Headless-testable with a recording delegate, and the place to re-key a second world
        /// drawn into the same scene: <c>Submit(preview, d =&gt; scene.Draw(d with { Motion =
        /// MotionKey.Combine(d.Motion, 1) }))</c>.
        /// </summary>
        public static void Submit(World world, Action<RigidInstanceDraw> draw)
        {
            foreach (var e in world.Query().With<Transform3D>().With<MeshInstance>().Entities())
                draw(Describe(world, e));
        }

        /// <summary>
        /// The pure core that carries material: for each entity with <see cref="Transform3D"/> +
        /// <see cref="MeshInstance"/>, invoke <paramref name="draw"/> with its mesh, world matrix, tint (zero
        /// tint -> white), and <see cref="MeshInstance.Material"/>. Headless-testable with a recording delegate.
        /// Carries no motion key.
        /// </summary>
        public static void Submit(World world, Action<MeshHandle, Matrix4x4, Color, Material> draw)
        {
            foreach (var e in world.Query().With<Transform3D>().With<MeshInstance>().Entities())
            {
                RigidInstanceDraw d = Describe(world, e);
                draw(d.Mesh, d.World, d.Tint, d.Material);
            }
        }

        /// <summary>
        /// The pure core: for each entity with <see cref="Transform3D"/> + <see cref="MeshInstance"/>, invoke
        /// <paramref name="draw"/> with its mesh, world matrix, and tint (zero tint -> white). Headless-testable
        /// with a recording delegate (no GPU). This overload is tint-only: the delegate signature carries no
        /// material, so use the <see cref="Scene3D"/> overload above when materials matter.
        /// </summary>
        public static void Submit(World world, Action<MeshHandle, Matrix4x4, Color> draw)
        {
            foreach (var e in world.Query().With<Transform3D>().With<MeshInstance>().Entities())
            {
                RigidInstanceDraw d = Describe(world, e);
                draw(d.Mesh, d.World, d.Tint);
            }
        }

        // One renderable entity as a draw, the single place all three cores read it: its mesh, world matrix, tint
        // (zero tint -> white), material and motion key.
        static RigidInstanceDraw Describe(World world, Entity e)
        {
            Transform3D t = world.Get<Transform3D>(e);
            MeshInstance m = world.Get<MeshInstance>(e);
            Color tint = m.Tint == Color.Transparent ? Color.White : m.Tint;
            return new RigidInstanceDraw(m.Mesh, t.ToMatrix()) { Tint = tint, Material = m.Material, Motion = MotionKeyOf(e) };
        }

        /// <summary>
        /// The motion key <see cref="Submit(World, Scene3D)"/> gives <paramref name="entity"/>, from its id and
        /// version. It holds for the entity's whole life. A recycled id carries a new version, so it is a new key with
        /// no previous state. Keys of one world never collide. Two worlds drawn into one scene share entity ids, so
        /// their keys collide (the scene counts it in <see cref="Scene3D.LastTemporalDiagnostics"/>), and one of them
        /// should be re-keyed through <see cref="Submit(World, Action{RigidInstanceDraw})"/>. A game derives an
        /// attachment's key from this with <see cref="MotionKey.Combine"/>. <see cref="MotionKey.None"/> for a
        /// default handle, which is no entity.
        /// </summary>
        public static MotionKey MotionKeyOf(Entity entity)
            => MotionKey.Combine(MotionKey.From(((ulong)entity.Version << 32) | unchecked((uint)entity.Id)), EntityKeyPart);
    }
}
