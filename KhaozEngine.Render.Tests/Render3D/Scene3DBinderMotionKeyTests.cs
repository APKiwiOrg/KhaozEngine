using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="Scene3DBinder"/> keys each entity's draw from its id and version (TEMPORAL-FOUNDATIONS-DESIGN section 3).
/// The keyed core carries every knob the material core carries, a recycled id is a new key, keys are pinned across
/// platforms, and one world's keys never collide.
/// </summary>
public sealed class Scene3DBinderMotionKeyTests
{
    static Entity Spawn(World world, Vector3 position, MeshHandle mesh, Color tint = default, Material material = default)
    {
        Entity e = world.Spawn();
        world.Set(e, new Transform3D { Position = position });
        world.Set(e, new MeshInstance { Mesh = mesh, Tint = tint, Material = material });
        return e;
    }

    [Fact]
    public void Each_entity_draws_under_the_key_of_its_id_and_version()
    {
        var world = new World();
        Entity a = Spawn(world, new Vector3(1f, 0f, 0f), new MeshHandle(5));
        Entity b = Spawn(world, new Vector3(2f, 0f, 0f), new MeshHandle(5));
        var drawn = new List<RigidInstanceDraw>();

        Scene3DBinder.Submit(world, draw => drawn.Add(draw));

        Assert.Equal(2, drawn.Count);
        Assert.Equal(Scene3DBinder.MotionKeyOf(a), drawn.Single(d => d.World.Translation.X == 1f).Motion);
        Assert.Equal(Scene3DBinder.MotionKeyOf(b), drawn.Single(d => d.World.Translation.X == 2f).Motion);
        Assert.NotEqual(Scene3DBinder.MotionKeyOf(a), Scene3DBinder.MotionKeyOf(b));
        Assert.All(drawn, d => Assert.False(d.Motion.IsNone));
    }

    [Fact]
    public void The_keyed_core_carries_what_the_material_core_carries()
    {
        var world = new World();
        Spawn(world, new Vector3(1f, 2f, 3f), new MeshHandle(5), new Color(1f, 0f, 0f, 1f),
            Material.Glowing(new Color(0.9f, 0.4f, 0.1f, 1f)));
        Spawn(world, new Vector3(4f, 0f, 0f), new MeshHandle(7));   // zero tint and unset material
        var keyed = new List<RigidInstanceDraw>();
        var plain = new List<(MeshHandle Mesh, Matrix4x4 World, Color Tint, Material Material)>();

        Scene3DBinder.Submit(world, draw => keyed.Add(draw));
        Scene3DBinder.Submit(world, (mesh, matrix, tint, material) => plain.Add((mesh, matrix, tint, material)));

        Assert.Equal(plain.Count, keyed.Count);
        for (int i = 0; i < plain.Count; i++)
        {
            Assert.Equal(plain[i].Mesh, keyed[i].Mesh);
            Assert.Equal(plain[i].World, keyed[i].World);
            Assert.Equal(plain[i].Tint, keyed[i].Tint);
            Assert.Equal(plain[i].Material, keyed[i].Material);
            Assert.True(keyed[i].CastsShadows);
            Assert.Equal(0f, keyed[i].Dissolve);
        }
    }

    [Fact]
    public void A_recycled_id_is_a_new_key_and_a_default_handle_is_none()
    {
        var world = new World();
        Entity first = world.Spawn();
        world.Despawn(first);
        Entity second = world.Spawn();

        Assert.Equal(first.Id, second.Id);   // the id really was recycled
        Assert.NotEqual(Scene3DBinder.MotionKeyOf(first), Scene3DBinder.MotionKeyOf(second));
        Assert.True(Scene3DBinder.MotionKeyOf(default).IsNone);
    }

    [Theory]
    [InlineData(0, 1u, 0xB4BCD51E446A76ACUL)]
    [InlineData(7, 3u, 0x6535DBAF466A9CE6UL)]
    [InlineData(-1, 1u, 0x839BC53833007692UL)]   // a negative id zero-extends, it never reaches the version bits
    public void Entity_keys_are_pinned_across_runs_and_platforms(int id, uint version, ulong expected)
        => Assert.Equal(expected, Scene3DBinder.MotionKeyOf(new Entity(id, version)).Value);

    [Fact]
    public void Keys_of_one_world_never_collide()
    {
        var seen = new HashSet<ulong>();
        for (int id = 0; id < 10_000; id++)
            for (uint version = 1; version <= 3; version++)
                Assert.True(seen.Add(Scene3DBinder.MotionKeyOf(new Entity(id, version)).Value),
                    $"entity {id} version {version} collided");
    }

    [Fact]
    public void Submitting_into_a_scene_queues_each_entitys_draw_with_its_key()
    {
        var world = new World();
        var material = new Material(new Color(200, 40, 40, 255), 0.5f, 16f);
        Entity a = Spawn(world, new Vector3(1f, 0f, 0f), new MeshHandle(5));
        Entity b = Spawn(world, new Vector3(0f, 2f, 3f), new MeshHandle(6), new Color(10, 20, 30, 255), material);
        using var harness = new MotionTestScene();

        harness.Scene.Begin();
        Scene3DBinder.Submit(world, harness.Scene);

        SceneInstances.Instance[] expected =
        [
            // The binder queues the entity's own MeshInstance.Material, which is default here, not Material.None.
            new(new RigidInstanceDraw(new MeshHandle(5), WorldAt(1f, 0f, 0f))
                { Tint = Color.White, Material = default, Motion = Scene3DBinder.MotionKeyOf(a) }),
            new(new RigidInstanceDraw(new MeshHandle(6), WorldAt(0f, 2f, 3f))
                {
                    Tint = new Color(10, 20, 30, 255), Material = material, Motion = Scene3DBinder.MotionKeyOf(b),
                }),
        ];
        IReadOnlyList<SceneInstances.Instance> queued = harness.Scene.QueuedInstancesForTests;
        Assert.Equal(expected.Length, queued.Count);
        PropertyInfo[] properties =
            typeof(SceneInstances.Instance).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < expected.Length; i++)
            foreach (PropertyInfo property in properties)
                Assert.True(Equals(property.GetValue(expected[i]), property.GetValue(queued[i])),
                    $"entity {i}: {property.Name} differs");
    }

    static Matrix4x4 WorldAt(float x, float y, float z)
        => new Transform3D { Position = new Vector3(x, y, z) }.ToMatrix();
}
