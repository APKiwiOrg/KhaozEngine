using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Render3D.Debug;

/// <summary>The first collision-overlay layer: builds one translucent proxy mesh per static shape
/// from a fixed set (once, not per frame) and draws them through <see cref="Scene3D.DrawOverlayMesh"/>
/// each frame while <see cref="Enabled"/>. Headless core is <see cref="BuildMeshes"/>. This class is
/// the thin GPU wrapper (mesh upload + draw-list bookkeeping).</summary>
public sealed class CollisionShapeOverlay : IDisposable
{
    readonly List<(MeshHandle Handle, Matrix4x4 World)> _draws = new();
    CollisionShapeKind[] _presentKinds = Array.Empty<CollisionShapeKind>();
    Scene3D? _scene;

    /// <summary>When false, <see cref="Draw"/> is a no-op. Toggled by the game/tool, not this class.</summary>
    public bool Enabled { get; set; }

    /// <summary>Per-kind color/name lookup used by <see cref="Build"/>. Assign before calling
    /// <see cref="Build"/> to customize (reassigning after <see cref="Build"/> has no effect until
    /// the next rebuild).</summary>
    public CollisionOverlayPalette Palette { get; set; } = new();

    /// <summary>Distinct <see cref="CollisionShapeKind"/> values present across the last-built static
    /// set (compound children counted individually), for driving a legend/filter UI.</summary>
    public IReadOnlyList<CollisionShapeKind> PresentKinds => _presentKinds;

    /// <summary>Headless core: converts each static's shape into a colored local-space mesh and its
    /// world matrix (orientation-then-translation from <see cref="CollisionStatic.Pose"/>), and
    /// collects the distinct <see cref="CollisionShapeKind"/> set across all statics, recursing into
    /// <see cref="CompoundShape"/> children so a compound contributes each child's kind.</summary>
    public static (GltfMesh Mesh, Matrix4x4 World)[] BuildMeshes(
        IReadOnlyList<CollisionStatic> statics, CollisionOverlayPalette palette,
        out IReadOnlyList<CollisionShapeKind> presentKinds)
    {
        var result = new (GltfMesh, Matrix4x4)[statics.Count];
        var kinds = new SortedSet<CollisionShapeKind>();
        for (int i = 0; i < statics.Count; i++)
        {
            var s = statics[i];
            result[i] = (CollisionShapeMesh.Build(s.Shape, palette), World(s.Pose));
            CollectKinds(s.Shape, kinds);
        }
        presentKinds = new List<CollisionShapeKind>(kinds);
        return result;
    }

    static void CollectKinds(PhysicsShape shape, SortedSet<CollisionShapeKind> into)
    {
        if (shape is CompoundShape compound)
        {
            foreach (var child in compound.Children)
                CollectKinds(child.Shape, into);
            return;
        }
        into.Add(CollisionOverlayPalette.KindOf(shape));
    }

    static Matrix4x4 World(Pose pose) =>
        Matrix4x4.CreateFromQuaternion(pose.Orientation) * Matrix4x4.CreateTranslation(pose.Position);

    /// <summary>Builds proxy meshes for <paramref name="statics"/> and uploads them to
    /// <paramref name="scene"/> once. Frees any meshes from a previous <see cref="Build"/> first
    /// (via <see cref="Scene3D.UnloadMesh"/>), so calling this again rebuilds cleanly for a changed
    /// static set instead of leaking GPU slots.</summary>
    public void Build(Scene3D scene, IReadOnlyList<CollisionStatic> statics)
    {
        ReleaseMeshes();
        _scene = scene;
        var built = BuildMeshes(statics, Palette, out var kinds);
        _presentKinds = new List<CollisionShapeKind>(kinds).ToArray();
        _draws.Clear();
        _draws.Capacity = built.Length;
        foreach (var (mesh, world) in built)
            _draws.Add((scene.LoadMesh(mesh), world));
    }

    /// <summary>Draws the built proxies through the translucent overlay pass. No-op when
    /// <see cref="Enabled"/> is false. Allocation-free: an index loop over the pre-sized draw list.</summary>
    public void Draw(Scene3D scene)
    {
        if (!Enabled) return;
        for (int i = 0; i < _draws.Count; i++)
            scene.DrawOverlayMesh(_draws[i].Handle, _draws[i].World);
    }

    void ReleaseMeshes()
    {
        if (_scene is { } scene)
        {
            for (int i = 0; i < _draws.Count; i++)
                scene.UnloadMesh(_draws[i].Handle);
        }
        _draws.Clear();
    }

    /// <summary>Frees the GPU mesh slots backing this overlay's proxies via
    /// <see cref="Scene3D.UnloadMesh"/>.</summary>
    public void Dispose() => ReleaseMeshes();
}
