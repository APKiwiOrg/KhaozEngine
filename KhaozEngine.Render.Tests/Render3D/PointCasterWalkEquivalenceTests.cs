using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The point pass's caster spans, read from the index, are the spans the walk over every instance built, span for
/// span and in the same order. Compared on a real <see cref="Scene3D"/> over the fake device, so the eligibility
/// rules (a stale handle, an opted-out instance, dissolving and inverted casters) and the span grouping are the
/// production code, and the oracle is the old walk kept in <see cref="PointCasterFullWalk"/>.
/// </summary>
public sealed class PointCasterWalkEquivalenceTests
{
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void EveryLightsCasterSpansMatchTheFullWalk(int seed)
    {
        using var rig = new PointShadowRig();
        var meshes = new TestMeshes(rig.Scene);
        var random = new Random(seed);
        int lights = 0, touched = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            // Frame 0 queues nothing. Later frames shrink and grow, so the index's reused arrays hold slots from an
            // earlier frame past the live count, which a query must never read.
            List<Queued> queue = frame == 0 ? new List<Queued>() : RandomQueue(random, meshes, random.Next(20, 300));
            // No light is queued, so the frame never builds the index itself and the first ask below builds it
            // lazily, which is the path the pass's diagnostic entry points take.
            rig.RenderFrame(s => QueueAll(s, queue));
            SceneInstances mirror = Mirror(queue);
            for (int l = 0; l < 24; l++)
            {
                (Vector3 at, float radius, float near, Vector3 min, Vector3 max) = RandomLight(random);
                List<Scene3D.ShadowCasterSpan> expected = PointCasterFullWalk.Spans(
                    mirror.Items, meshes.BoundsOf, false, at, radius, near, min, max);
                Assert.Equal(expected, rig.Scene.DebugPointCasterSpans(at, radius, near, min, max));
                lights++;
                if (expected.Count > 0) touched++;
            }
        }
        Assert.True(touched >= lights / 4, $"only {touched} of {lights} lights touched a caster");
    }

    [Fact]
    public void ADynamicRowDrawsExactlyTheFullWalksSpans()
    {
        using var rig = new PointShadowRig();
        var meshes = new TestMeshes(rig.Scene);
        List<Queued> queue = RandomQueue(new Random(1110), meshes, 200);
        queue.Insert(0, new Queued(meshes.Ground, Matrix4x4.Identity, 0f, true, false));   // under the light
        var light = new Vector3(4f, 2f, -3f);
        void Queue(Scene3D s)
        {
            QueueAll(s, queue);
            s.AddLight(light, Color.White, 12f, 1f, LightShadow.Dynamic);
        }

        rig.RenderFrame(Queue);                                  // asks for the atlas
        ShadowPassDiagnostics drawn = rig.RenderFrame(Queue);    // draws the dynamic row

        List<Scene3D.ShadowCasterSpan> expected = PointCasterFullWalk.Spans(
            Mirror(queue).Items, meshes.BoundsOf, false, light, 12f, 0f, default, default);
        Assert.Equal(1, drawn.PointDynamicRenders);
        Assert.NotEmpty(expected);
        Assert.Equal(6 * expected.Count, drawn.PointFaceDrawCalls);   // one draw per span per face
    }

    [Fact]
    public void ASteadyFrameTestsOnlyTheCastersNearEachStaticLight()
    {
        using var rig = new PointShadowRig();
        rig.Settings.PointShadows.MaxStaticRebuildsPerFrame = 8;
        MeshHandle box = rig.Scene.LoadMesh(MeshPrimitives.Box(1f));
        const int side = 40;
        const float spacing = 16f;
        void Queue(Scene3D s)
        {
            for (int x = 0; x < side; x++)
                for (int z = 0; z < side; z++)
                    s.Draw(box, Matrix4x4.CreateTranslation(x * spacing, 0.5f, z * spacing));
            for (int light = 0; light < 8; light++)
                s.AddLight(new Vector3((5 * light + 3) * spacing, 3f, (4 * light + 1) * spacing), Color.White,
                    6f, 1f, LightShadow.Static(light + 1));
        }

        rig.RenderFrame(Queue);                                  // asks for the atlas
        rig.RenderFrame(Queue);                                  // draws all eight rows
        ShadowPassDiagnostics steady = rig.RenderFrame(Queue);

        Assert.Equal(0, steady.PointStaticRebuilds);
        Assert.Equal(8, rig.Scene.PointShadowedLights);
        // Eight signatures, one query each. Each query's window is under 44.5 m per horizontal axis (radius 6, one
        // cell, the slack and the cell floor), which holds at most three lattice columns 16 m apart, so at most
        // nine tests a light. The walk over every run tested all 1,600 instances for every light.
        Assert.InRange(rig.Scene.PointCasterTouchTests, 8, 8 * 9);
    }

    readonly record struct Queued(MeshHandle Mesh, Matrix4x4 World, float Dissolve, bool Casts, bool Invert);

    /// <summary>The meshes every case queues: a small prop, a caster far wider than a cell, a ground plane and a
    /// handle whose mesh was unloaded, with the local bounds the scene computed for each at load.</summary>
    sealed class TestMeshes
    {
        readonly Dictionary<(int, int), MeshBounds> _bounds = new();

        internal TestMeshes(Scene3D scene)
        {
            Small = Load(scene, MeshPrimitives.Box(1f));
            Huge = Load(scene, MeshPrimitives.Box(30f));
            Ground = Load(scene, MeshPrimitives.Plane(40f, 40f));
            Stale = scene.LoadMesh(MeshPrimitives.Box(2f));
            scene.UnloadMesh(Stale);   // still queued, which the walk skips by generation
        }

        internal MeshHandle Small { get; }
        internal MeshHandle Huge { get; }
        internal MeshHandle Ground { get; }
        internal MeshHandle Stale { get; }

        internal MeshBounds? BoundsOf(MeshHandle mesh) =>
            _bounds.TryGetValue((mesh.Index, mesh.Generation), out MeshBounds bounds) ? bounds : null;

        MeshHandle Load(Scene3D scene, GltfMesh mesh)
        {
            MeshHandle handle = scene.LoadMesh(mesh);
            _bounds[(handle.Index, handle.Generation)] = MeshBounds.FromVertices(mesh.Vertices);
            return handle;
        }
    }

    static List<Queued> RandomQueue(Random random, TestMeshes meshes, int count)
    {
        MeshHandle[] pick = { meshes.Small, meshes.Small, meshes.Small, meshes.Small, meshes.Huge, meshes.Ground, meshes.Stale };
        var queue = new List<Queued>(count);
        for (int i = 0; i < count; i++)
        {
            var at = new Vector3(Range(random, -60f, 60f), Range(random, -4f, 12f), Range(random, -60f, 60f));
            if (random.Next(5) == 0) at = Snap(at);   // on a cell corner
            Matrix4x4 world = Matrix4x4.CreateScale(Range(random, 0.5f, 3f))
                * Matrix4x4.CreateRotationY(Range(random, 0f, MathF.Tau))
                * Matrix4x4.CreateTranslation(at);
            float dissolve = random.Next(4) == 0 ? Range(random, 0.1f, 0.9f) : 0f;
            queue.Add(new Queued(pick[random.Next(pick.Length)], world, dissolve,
                Casts: random.Next(8) != 0, Invert: dissolve > 0f && random.Next(3) == 0));
        }
        return queue;
    }

    static void QueueAll(Scene3D scene, List<Queued> queue)
    {
        foreach (Queued q in queue)
            scene.Draw(q.Mesh, q.World, Color.White, Material.None, q.Dissolve, 0f, Color.White, q.Casts, q.Invert);
    }

    static SceneInstances Mirror(List<Queued> queue)
    {
        var mirror = new SceneInstances();
        foreach (Queued q in queue)
            mirror.Add(q.Mesh, q.World, Color.White, Material.None, q.Dissolve, 0f, Color.White, q.Casts, q.Invert);
        return mirror;
    }

    static (Vector3 At, float Radius, float Near, Vector3 Min, Vector3 Max) RandomLight(Random random)
    {
        float radius = Range(random, 2f, 24f);
        var at = new Vector3(Range(random, -70f, 70f), Range(random, 0f, 8f), Range(random, -70f, 70f));
        if (random.Next(3) == 0) at = Snap(at) + new Vector3(radius + PointCasterIndex.CellSize);
        float near = random.Next(4) == 0 ? Range(random, 0f, radius * 0.5f) : 0f;
        bool boxed = random.Next(4) == 0;
        var half = new Vector3(Range(random, 0.1f, 3f), Range(random, 0.1f, 3f), Range(random, 0.1f, 3f));
        return (at, radius, near, boxed ? at - half : default, boxed ? at + half : default);
    }

    static Vector3 Snap(Vector3 p) => new(
        MathF.Round(p.X / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Y / PointCasterIndex.CellSize) * PointCasterIndex.CellSize,
        MathF.Round(p.Z / PointCasterIndex.CellSize) * PointCasterIndex.CellSize);

    static float Range(Random random, float min, float max) => min + (float)random.NextDouble() * (max - min);
}
