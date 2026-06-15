using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The per-frame instance queue for <see cref="Scene3D"/>: <see cref="Begin"/> clears it, <see cref="Add"/>
    /// queues one (mesh, world) draw, and the renderer consumes <see cref="Items"/> in submission order. Pure /
    /// headless so the queueing is testable without a GPU.
    /// </summary>
    public sealed class SceneInstances
    {
        readonly List<Instance> _items = new();
        public IReadOnlyList<Instance> Items => _items;

        public void Begin() => _items.Clear();
        public void Add(MeshHandle mesh, Matrix4x4 world) => _items.Add(new Instance(mesh, world));

        public readonly struct Instance
        {
            public MeshHandle Mesh { get; }
            public Matrix4x4 World { get; }
            public Instance(MeshHandle mesh, Matrix4x4 world) { Mesh = mesh; World = world; }
        }
    }
}
