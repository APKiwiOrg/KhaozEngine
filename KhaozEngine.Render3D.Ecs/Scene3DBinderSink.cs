using System.Runtime.CompilerServices;
using KhaozEngine.Ecs;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>One scene's cached ECS draw callback, attached through a weak key so unused scenes can be collected.</summary>
internal sealed class Scene3DBinderSink
{
    static readonly ConditionalWeakTable<Scene3D, Scene3DBinderSink> Sinks = new();
    static readonly ConditionalWeakTable<Scene3D, Scene3DBinderSink>.CreateValueCallback CreateSink =
        static scene => new Scene3DBinderSink(scene);

    readonly Scene3D scene;
    readonly RefAction<Transform3D, MeshInstance> draw;

    Scene3DBinderSink(Scene3D scene)
    {
        this.scene = scene;
        draw = QueueDraw;
    }

    internal static void Submit(World world, Scene3D scene)
        => world.ForEach(Sinks.GetValue(scene, CreateSink).draw);

    internal static RigidInstanceDraw Describe(Entity entity, in Transform3D transform, in MeshInstance mesh)
    {
        Color tint = mesh.Tint == Color.Transparent ? Color.White : mesh.Tint;
        return new RigidInstanceDraw(mesh.Mesh, transform.ToMatrix())
        {
            Tint = tint,
            Material = mesh.Material,
            Motion = Scene3DBinder.MotionKeyOf(entity),
        };
    }

    void QueueDraw(Entity entity, ref Transform3D transform, ref MeshInstance mesh)
    {
        RigidInstanceDraw descriptor = Describe(entity, in transform, in mesh);
        scene.Draw(in descriptor);
    }
}
