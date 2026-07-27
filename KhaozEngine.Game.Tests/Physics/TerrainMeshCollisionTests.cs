using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Headless, fixed-dt tests for terrain-as-physics-geometry: a synthetic terrain chunk surface becomes a
/// static <see cref="TriangleMeshShape"/> in the physics world, a dynamic body RESTS on it (raycast-verified at
/// multiple heights, per the Bepu gotcha - a Bepu mesh is NOT recentered so the collision surface lines up with
/// the vertices), removing the chunk drops collision so a body falls through, and register/unregister churns
/// cleanly across many cycles (the mesh BufferPool disposal discipline). The delegate path (analytic
/// <see cref="TerrainCollision"/>) is untouched and keeps its own tests.</summary>
public class TerrainMeshCollisionTests
{
    const float Dt = 1f / 60f;

    // Build a synthetic flat terrain chunk: a res x res grid of quads over [0,size] x [0,size] at a constant
    // height, CCW-from-above (matching TerrainChunkBuilder's surface winding), plus DOWNWARD skirt vertices
    // appended after the surface (so we can prove the extractor drops them). Returns the mesh + surface count.
    static TerrainChunkMesh FlatChunk(float height, float size = 8f, int res = 4)
    {
        int cols = res + 1;
        var verts = new ModelVertex[cols * cols + cols]; // surface grid + one skirt row (proof the skirt is dropped)
        int vi = 0;
        for (int iz = 0; iz <= res; iz++)
        for (int ix = 0; ix <= res; ix++)
        {
            float x = (float)ix / res * size;
            float z = (float)iz / res * size;
            verts[vi++] = new ModelVertex(new Vector3(x, height, z), Vector3.UnitY, Vector4.One);
        }
        int surfaceVertexCount = vi;
        // Append a skirt row (dropped copies of the -Z edge) with downward-ish normals; the extractor must NOT
        // reference these (all their indices are >= surfaceVertexCount).
        for (int ix = 0; ix <= res; ix++)
            verts[vi++] = new ModelVertex(new Vector3((float)ix / res * size, height - 0.3f, 0f), -Vector3.UnitY, Vector4.One);

        var inds = new System.Collections.Generic.List<uint>();
        for (int iz = 0; iz < res; iz++)
        for (int ix = 0; ix < res; ix++)
        {
            uint i0 = (uint)(iz * cols + ix);
            uint i1 = (uint)(iz * cols + ix + 1);
            uint i2 = (uint)((iz + 1) * cols + ix);
            uint i3 = (uint)((iz + 1) * cols + ix + 1);
            inds.Add(i0); inds.Add(i2); inds.Add(i3);
            inds.Add(i0); inds.Add(i3); inds.Add(i1);
        }
        // A couple of skirt triangles that reference the appended skirt row (must be excluded by the extractor).
        uint s0 = (uint)surfaceVertexCount;
        inds.Add(0); inds.Add(s0); inds.Add(s0 + 1);

        var mesh = new GltfMesh(verts, inds.ToArray());
        var bounds = new TerrainChunkBounds(new Vector3(0, height - 0.3f, 0), new Vector3(size, height, size));
        return new TerrainChunkMesh(mesh, Array.Empty<TerrainSplatWeights>(), bounds,
            lod: 0, region: new TerrainChunkRegion { OriginX = 0, OriginZ = 0, Size = size }, surfaceVertexCount);
    }

    static void StepMany(IPhysicsWorld world, int steps)
    {
        for (int i = 0; i < steps; i++) world.Step(Dt);
    }

    // ---------------------------------------------------------------------
    // Surface-only extraction.
    // ---------------------------------------------------------------------

    [Fact]
    public void Build_KeepsSurfaceTriangles_DropsSkirtTriangles()
    {
        TerrainChunkMesh chunk = FlatChunk(height: 3f);
        TriangleMeshShape? shape = TerrainChunkCollision.Build(chunk);
        Assert.NotNull(shape);

        // res=4 => 4x4 quads => 32 surface triangles; the one skirt triangle must be dropped.
        Assert.Equal(32 * 3, shape!.Indices.Length);
        // Every index the collision mesh keeps is a surface vertex (below surfaceVertexCount), so no skirt leaks.
        int surfaceCount = chunk.SurfaceVertexCount;
        foreach (int idx in shape.Indices)
            Assert.True(idx < surfaceCount, $"kept index {idx} must reference a surface vertex (< {surfaceCount})");
        // Every kept vertex sits at the surface height (skirt copies at height-0.3 are never referenced).
        foreach (int idx in shape.Indices)
            Assert.Equal(3f, shape.Vertices[idx].Y, 3);
    }

    [Fact]
    public void Build_EmptyChunk_ReturnsNull()
    {
        var mesh = new GltfMesh(new[] { new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One) }, Array.Empty<uint>());
        var chunk = new TerrainChunkMesh(mesh, Array.Empty<TerrainSplatWeights>(),
            new TerrainChunkBounds(Vector3.Zero, Vector3.Zero), 0,
            new TerrainChunkRegion { OriginX = 0, OriginZ = 0, Size = 1f }, surfaceVertexCount: 1);
        Assert.Null(TerrainChunkCollision.Build(chunk));
    }

    // ---------------------------------------------------------------------
    // Rest on the terrain surface at multiple heights (raycast-verified).
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(0f)]
    [InlineData(5f)]
    [InlineData(20f)]
    public void DynamicBox_RestsOnTerrainSurface_AtMultipleHeights(float terrainY)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        TriangleMeshShape mesh = TerrainChunkCollision.Build(FlatChunk(terrainY))!;
        world.AddStatic(mesh, Pose.Identity);

        // Drop a 1x1x1 box over the middle of the chunk from well above the surface.
        var box = new BoxShape(new Vector3(0.5f, 0.5f, 0.5f));
        var dropXz = new Vector3(4f, terrainY + 6f, 4f);
        DynamicBodyHandle h = world.AddDynamic(box, Pose.At(dropXz), DynamicBodyDescription.WithMass(1f));

        StepMany(world, 300); // 5 s: fall + settle onto the terrain surface

        Pose pose = world.GetDynamicPose(h);
        Assert.True(MathF.Abs(pose.Position.Y - (terrainY + 0.5f)) < 0.15f,
            $"box must rest with its base on the terrain surface at y={terrainY} (centre ~{terrainY + 0.5f}), was {pose.Position.Y:F3}");

        // Raycast-down verification per the gotcha: the surface directly under the box centre is at terrainY,
        // and the settled box top is ~terrainY+1.
        bool surfaceHit = world.Raycast(new Vector3(4f, terrainY + 5f, 4f), -Vector3.UnitY, 20f, out RayHit rh);
        Assert.True(surfaceHit, "ray must hit the settled box or the terrain surface");
        float topY = (terrainY + 5f) - rh.Distance;
        Assert.True(topY > terrainY + 0.9f && topY < terrainY + 1.1f,
            $"settled box top must be ~{terrainY + 1f}, was {topY:F3}");
    }

    // ---------------------------------------------------------------------
    // Chunk unload removes collision: a body falls THROUGH after unload.
    // ---------------------------------------------------------------------

    [Fact]
    public void UnloadChunk_RemovesCollision_BodyFallsThrough()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        TriangleMeshShape mesh = TerrainChunkCollision.Build(FlatChunk(height: 10f))!;
        StaticHandle terrain = world.AddStatic(mesh, Pose.Identity);

        // A body rests on the surface first.
        var box = new BoxShape(new Vector3(0.5f, 0.5f, 0.5f));
        DynamicBodyHandle h = world.AddDynamic(box, Pose.At(new Vector3(4f, 16f, 4f)), DynamicBodyDescription.WithMass(1f));
        StepMany(world, 240);
        Assert.True(world.GetDynamicPose(h).Position.Y > 10f, "body should be resting on the surface (above y=10) before unload");

        // Unload the terrain chunk: its collision goes away.
        world.RemoveStatic(terrain);
        // A downward ray through the chunk now hits only the (still-present) box, not the removed terrain.
        world.SetDynamicVelocity(h, Vector3.Zero, Vector3.Zero); // wake it so it resumes falling
        StepMany(world, 240); // 4 s of free fall with no floor

        float fallenY = world.GetDynamicPose(h).Position.Y;
        Assert.True(fallenY < 0f, $"with terrain collision removed the body must fall THROUGH (well below y=10), was {fallenY:F3}");
    }

    // ---------------------------------------------------------------------
    // Churn: register/unregister N times, no leak/throw, world empty after.
    // ---------------------------------------------------------------------

    [Fact]
    public void ChurnManyRegisterUnregisterCycles_NoThrow_WorldEmptyAfter()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero); // static-only, isolate the mesh pool churn

        // Register a terrain chunk mesh, verify a ray hits it, remove it, verify the ray misses - many times.
        // This exercises the Bepu Mesh BufferPool ownership: RemoveStatic -> RecursivelyRemoveAndDispose returns
        // the triangle buffer each cycle, so thousands of streaming cycles do not grow the pool.
        // (A direct pool-size assertion would be better, but BepuUtilities 2.4.0's BufferPool exposes no cheap
        // block/allocated-byte count - only AssertEmpty/Clear/Take/Return - and the live Simulation holds its own
        // permanent buffers in the same pool, so AssertEmpty cannot be used to bound just the shape churn. The
        // ray hit-then-miss per cycle is the available signal that each registration's shape is fully released.)
        for (int cycle = 0; cycle < 200; cycle++)
        {
            float y = 1f + (cycle % 7);
            TriangleMeshShape mesh = TerrainChunkCollision.Build(FlatChunk(height: y))!;
            StaticHandle h = world.AddStatic(mesh, Pose.Identity);

            Assert.True(world.Raycast(new Vector3(4f, y + 5f, 4f), -Vector3.UnitY, 20f, out RayHit rh),
                $"cycle {cycle}: ray must hit the freshly registered terrain surface");
            Assert.Equal(y, (y + 5f) - rh.Distance, 2);

            world.RemoveStatic(h);
            Assert.False(world.Raycast(new Vector3(4f, y + 5f, 4f), -Vector3.UnitY, 20f, out _),
                $"cycle {cycle}: ray must miss after the terrain surface is unregistered");
        }
    }

    // ---------------------------------------------------------------------
    // The chunk-lifecycle helper (ChunkTerrainCollision) that Scene3DChunkSink drives on load/unload.
    // ---------------------------------------------------------------------

    [Fact]
    public void ChunkTerrainCollision_Add_RegistersSurface_Remove_RemovesIt()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        TerrainChunkMesh chunk = FlatChunk(height: 9f);

        bool added = ChunkTerrainCollision.Add(world, chunk, out StaticHandle h);
        Assert.True(added, "a chunk with surface triangles must register a body");
        Assert.True(world.Raycast(new Vector3(4f, 14f, 4f), -Vector3.UnitY, 20f, out RayHit rh),
            "ray must hit the registered terrain surface");
        Assert.Equal(9f, 14f - rh.Distance, 2);

        ChunkTerrainCollision.Remove(world, added, h);
        Assert.False(world.Raycast(new Vector3(4f, 14f, 4f), -Vector3.UnitY, 20f, out _),
            "ray must miss after the terrain body is removed");
    }

    [Fact]
    public void ChunkTerrainCollision_EmptyChunk_AddsNothing_RemoveIsNoOp()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        var mesh = new GltfMesh(new[] { new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One) }, Array.Empty<uint>());
        var empty = new TerrainChunkMesh(mesh, Array.Empty<TerrainSplatWeights>(),
            new TerrainChunkBounds(Vector3.Zero, Vector3.Zero), 0,
            new TerrainChunkRegion { OriginX = 0, OriginZ = 0, Size = 1f }, surfaceVertexCount: 1);

        bool added = ChunkTerrainCollision.Add(world, empty, out StaticHandle h);
        Assert.False(added, "an empty chunk registers no body");
        ChunkTerrainCollision.Remove(world, added, h); // must not throw
    }

    [Fact]
    public void ChunkTerrainCollision_Churn_ThroughHelper_NoThrow()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld(Vector3.Zero);
        for (int cycle = 0; cycle < 100; cycle++)
        {
            float y = 2f + (cycle % 5);
            bool added = ChunkTerrainCollision.Add(world, FlatChunk(height: y), out StaticHandle h);
            Assert.True(added);
            Assert.True(world.Raycast(new Vector3(4f, y + 5f, 4f), -Vector3.UnitY, 20f, out _), $"cycle {cycle}: ray must hit");
            ChunkTerrainCollision.Remove(world, added, h);
        }
        Assert.False(world.Raycast(new Vector3(4f, 100f, 4f), -Vector3.UnitY, 200f, out _),
            "no terrain body should remain after all cycles");
    }

    // ---------------------------------------------------------------------
    // Determinism: identical worlds with the terrain mesh + dropped body stay bit-identical.
    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    // A terrain chunk 100 km from the origin. Every terrain physics test above sits at origin 0, which is exactly
    // why the chunk-local bake could have shipped broken: the vertices and the static's pose have to agree about
    // which space they are in, and nothing else in the suite says so.
    //
    // What these two bind, stated so nobody over-reads them: the vertex space against the pose. Registering
    // chunk-local vertices at Pose.Identity (or absolute vertices at the region pose) puts the terrain 100 km from
    // where it belongs and both fail loudly - checked by making exactly that mismatch. What they do NOT show is the
    // pre-bake pipeline failing: an absolute chunk registered at Pose.Identity is self-consistent, and Bepu's
    // downward ray against a near-horizontal triangle stays sub-millimetre even on 100 km operands, so both
    // representations answer this particular query. The bake's collision win is the geometry (an exact 60 m vertex
    // lattice instead of one jittered onto the 7.8 mm float32 lattice) and the magnitude every triangle test runs
    // at, which is a grazing-sweep and contact-generation property this axis-aligned probe cannot see.
    // ---------------------------------------------------------------------

    // A planar ramp field: height depends only on X, with a slope we know exactly, so the meshed surface
    // reproduces the field EXACTLY (a triangulated plane is a plane) and any residual is measurement, not
    // tessellation. The ramp is measured from refX so the heights stay small however far out the chunk is; it is
    // still a pure function of (x, z), which is what ITerrainFeature requires.
    sealed class RampFeature : ITerrainFeature
    {
        readonly float _refX, _slope;
        public RampFeature(float refX, float slope) { _refX = refX; _slope = slope; }
        public float Apply(float x, float z, float h) => _slope * (x - _refX);
    }

    static TerrainField RampField(float refX, float slope) => new(new TerrainConfig
    {
        GentleAmplitude = 0f,
        Biomes = new[] { new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, BaseHeight = 0f, HillAmplitude = 0f } },
        Features = new ITerrainFeature[] { new RampFeature(refX, slope) },
    });

    [Fact]
    public void TerrainRaycast_At100Km_HitsTheFieldHeight_WithOnlyTheQueryQuantumLeft()
    {
        const float far = 100_000f;          // binade [65536, 131072): one float32 ULP is 7.8 mm
        const float ulp = 7.8125e-3f;
        const float slope = 0.05f;           // 1-in-20: gentle enough that the residual below is sub-millimetre

        var region = new TerrainChunkRegion { OriginX = far, OriginZ = far, Size = 60f };
        TerrainField field = RampField(far, slope);
        TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod: 0);

        using IPhysicsWorld world = new BepuPhysicsWorld();
        Assert.True(ChunkTerrainCollision.Add(world, chunk, out _));

        // The point the test MEANS, in double, and the float32 coordinates the ray can actually carry.
        const double intendedX = far + 30.123456789, intendedZ = far + 21.987654321;
        float rayX = (float)intendedX, rayZ = (float)intendedZ;

        Assert.True(world.Raycast(new Vector3(rayX, 500f, rayZ), -Vector3.UnitY, 1000f, out RayHit hit,
            QueryFilter.StaticsOnly), "the ray must hit the terrain chunk 100 km out at all");
        float hitY = 500f - hit.Distance;

        // (a) The assertion that tests the BAKE: against the field at the XZ the ray really carried, not at the
        //     mathematical point. Two roundings are in play and both are the price of sampling an absolute field:
        //     the ray's own coordinate, and the builder's sample at OriginX + local. Neither is the triangle test,
        //     which is what the chunk-local bake moved down to 60 m magnitude.
        float expected = field.SampleHeight(rayX, rayZ);
        Assert.True(MathF.Abs(hitY - expected) < 1e-3f,
            $"hit {hitY:F6} m against the field's {expected:F6} m at the ray's own XZ: {MathF.Abs(hitY - expected) * 1000f:F4} mm out");

        // (b) The residual, recorded as a known bounded property rather than left for a future reader to find as a
        //     flake: the ray asked about a point up to half a lattice step from the one the test meant, and over a
        //     slope that is a height difference no amount of chunk-local baking can remove. The bound carries both
        //     rounding sources from (a).
        float intended = field.SampleHeight((float)(intendedX - far) + far, (float)(intendedZ - far) + far);
        float residual = MathF.Abs(hitY - intended);
        Assert.True(residual <= slope * ulp * 2f,
            $"the residual against the intended point is {residual * 1000f:F4} mm, past the " +
            $"{slope * ulp * 2f * 1000f:F4} mm this slope's lateral quantization can explain");
    }

    [Fact]
    public void TerrainChunk_At100Km_IsAsPreciseAsTheSameChunkAtTheOrigin()
    {
        // The release headline as a comparison rather than a tolerance: the same chunk shape, meshed and collided
        // at the origin and at 100 km, answers the same downward ray to the same height. Under the pre-bake
        // absolute vertices this could not hold, because both the vertex buffer and Bepu's triangle test ran on
        // 100 km operands.
        const float far = 100_000f, slope = 0.05f;

        static float HitHeight(float originXz, float slopeRef, float localX, float localZ)
        {
            var region = new TerrainChunkRegion { OriginX = originXz, OriginZ = originXz, Size = 60f };
            TerrainField field = RampField(slopeRef, slope);
            using IPhysicsWorld world = new BepuPhysicsWorld();
            Assert.True(ChunkTerrainCollision.Add(world, TerrainChunkBuilder.Build(field, region, lod: 0), out _));
            Assert.True(world.Raycast(new Vector3(originXz + localX, 500f, originXz + localZ), -Vector3.UnitY, 1000f,
                out RayHit hit, QueryFilter.StaticsOnly));
            return 500f - hit.Distance;
        }

        float atOrigin = HitHeight(0f, 0f, 30.125f, 21.5f);
        float atRange = HitHeight(far, far, 30.125f, 21.5f);
        Assert.True(MathF.Abs(atOrigin - atRange) < 1e-3f,
            $"the chunk at 100 km answers {atRange:F6} m where the same chunk at the origin answers {atOrigin:F6} m");
    }

    [Fact]
    public void TwoIdenticalWorlds_DroppedOnTerrain_StepBitIdentically()
    {
        static Vector3 Run()
        {
            using IPhysicsWorld world = new BepuPhysicsWorld();
            world.AddStatic(TerrainChunkCollision.Build(FlatChunk(height: 4f))!, Pose.Identity);
            DynamicBodyHandle h = world.AddDynamic(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)),
                Pose.At(new Vector3(4f, 10f, 4f)), DynamicBodyDescription.WithMass(1f));
            for (int i = 0; i < 200; i++) world.Step(Dt);
            return world.GetDynamicPose(h).Position;
        }
        Assert.Equal(Run(), Run());
    }
}
