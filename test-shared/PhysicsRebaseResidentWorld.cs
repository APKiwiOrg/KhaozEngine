using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Physics;

/// <summary>The shared resident fixture for deterministic guards and the hosted-only timing budget.</summary>
internal static class PhysicsRebaseResidentWorld
{
    static readonly BoxShape UnitBox = new(new Vector3(0.5f, 0.5f, 0.5f));
    internal static readonly Vector3 Shift = new(128f, 0f, 128f);

    internal static BepuPhysicsWorld CreateWarmed()
    {
        // The reference consumer's resident shape: a gameplay ring of terrain statics, a few thousand props, a few hundred
        // dynamics. Terrain statics are triangle meshes, which is the expensive collidable to refit.
        var world = new BepuPhysicsWorld();
        for (int i = 0; i < 25; i++)
        {
            float x = (i % 5) * 60f, z = (i / 5) * 60f;
            world.AddStatic(FlatTriangleMesh(60f, 8), Pose.At(new Vector3(x, 0f, z)));
        }
        var rng = new XorRng(12345);
        for (int i = 0; i < 2000; i++)
            world.AddStatic(UnitBox, Pose.At(new Vector3(rng.NextFloat() * 300f, 0.5f, rng.NextFloat() * 300f)));
        for (int i = 0; i < 200; i++)
            world.AddDynamic(UnitBox, Pose.At(new Vector3(rng.NextFloat() * 300f, 4f + i * 0.01f, rng.NextFloat() * 300f)),
                DynamicBodyDescription.WithMass(1f));

        // Settle and warm every code path the measurement touches.
        for (int i = 0; i < 120; i++) world.Step(1f / 60f);
        world.Rebase(Shift);
        world.Rebase(Vector3.Zero);

        return world;
    }

    // A flat res x res triangle grid over [0, size]^2 at y = 0, wound so Bepu's front face points up (the same
    // reversal TerrainChunkCollision applies). Stands in for a terrain chunk's collision surface.
    static TriangleMeshShape FlatTriangleMesh(float size, int res)
    {
        int cols = res + 1;
        var verts = new Vector3[cols * cols];
        for (int iz = 0; iz <= res; iz++)
            for (int ix = 0; ix <= res; ix++)
                verts[iz * cols + ix] = new Vector3((float)ix / res * size, 0f, (float)iz / res * size);

        var inds = new List<int>(res * res * 6);
        for (int iz = 0; iz < res; iz++)
            for (int ix = 0; ix < res; ix++)
            {
                int i0 = iz * cols + ix, i1 = i0 + 1, i2 = (iz + 1) * cols + ix, i3 = i2 + 1;
                inds.Add(i0); inds.Add(i3); inds.Add(i2);
                inds.Add(i0); inds.Add(i1); inds.Add(i3);
            }
        return new TriangleMeshShape(verts, inds.ToArray());
    }
}
