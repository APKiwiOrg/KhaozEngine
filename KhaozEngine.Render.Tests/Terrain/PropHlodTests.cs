using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the HLOD merge+weld library (<see cref="PropHlod"/>, no GPU): world-space merge
    /// determinism and per-kit opt-in, vertex-cluster weld reduction bounds + colour preservation, the one-call
    /// BuildMergedMesh, and the distance crossfade curve. Everything is a pure function of its inputs.</summary>
    public class PropHlodTests
    {
        // A tiny single-triangle mesh at an offset, flat-coloured. Three distinct positions so a merge is measurable.
        static GltfMesh Tri(Vector3 offset, Vector4 color)
        {
            var v = new[]
            {
                new ModelVertex(offset + new Vector3(0f, 0f, 0f), Vector3.UnitY, color),
                new ModelVertex(offset + new Vector3(1f, 0f, 0f), Vector3.UnitY, color),
                new ModelVertex(offset + new Vector3(0f, 0f, 1f), Vector3.UnitY, color),
            };
            return new GltfMesh(v, new ushort[] { 0, 1, 2 });
        }

        static Dictionary<string, GltfMesh> Kit(params (string id, GltfMesh mesh)[] entries)
        {
            var d = new Dictionary<string, GltfMesh>();
            foreach (var (id, mesh) in entries) d[id] = mesh;
            return d;
        }

        static void AssertMeshByteIdentical(GltfMesh a, GltfMesh b)
        {
            Assert.Equal(a.Vertices.Length, b.Vertices.Length);
            Assert.Equal(a.Indices32.Length, b.Indices32.Length);
            for (int i = 0; i < a.Vertices.Length; i++)
            {
                Assert.Equal(a.Vertices[i].Position, b.Vertices[i].Position);
                Assert.Equal(a.Vertices[i].Normal, b.Vertices[i].Normal);
                Assert.Equal(a.Vertices[i].Color, b.Vertices[i].Color);
            }
            for (int i = 0; i < a.Indices32.Length; i++) Assert.Equal(a.Indices32[i], b.Indices32[i]);
        }

        // ---- Merge: world-space concat, deterministic, per-kit opt-in ----

        [Fact]
        public void Merge_ConcatenatesEveryPlacementIntoOneMesh()
        {
            var kit = Kit(("pine", Tri(Vector3.Zero, new Vector4(1f, 0f, 0f, 1f))));
            var placements = new List<PropPlacement>
            {
                new PropPlacement("pine", 0f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("pine", 10f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("pine", 0f, 0f, 20f, 1f, 0f, 0),
            };

            GltfMesh merged = PropHlod.Merge(placements, kit);

            Assert.Equal(9, merged.Vertices.Length);   // 3 placements * 3 verts
            Assert.Equal(3, merged.TriangleCount);      // 3 placements * 1 tri
        }

        [Fact]
        public void Merge_TransformsVerticesToWorldSpace()
        {
            var kit = Kit(("pine", Tri(Vector3.Zero, new Vector4(1f, 1f, 1f, 1f))));
            // One placement translated to (10, 5, 20), no scale/yaw: the tri's first vertex lands at exactly (10,5,20).
            var placements = new List<PropPlacement> { new PropPlacement("pine", 10f, 5f, 20f, 1f, 0f, 0) };

            GltfMesh merged = PropHlod.Merge(placements, kit);

            Assert.Equal(new Vector3(10f, 5f, 20f), merged.Vertices[0].Position);
            Assert.Equal(new Vector3(11f, 5f, 20f), merged.Vertices[1].Position);   // (1,0,0) offset carried through
        }

        [Fact]
        public void Merge_SkipsPlacementsWithNoSourceMesh()
        {
            var kit = Kit(("pine", Tri(Vector3.Zero, new Vector4(1f, 1f, 1f, 1f))));
            var placements = new List<PropPlacement>
            {
                new PropPlacement("pine", 0f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("ghost", 5f, 0f, 0f, 1f, 0f, 0),   // no mesh for this kit: contributes nothing
            };

            GltfMesh merged = PropHlod.Merge(placements, kit);

            Assert.Equal(3, merged.Vertices.Length);   // only the pine, the ghost is skipped
        }

        [Fact]
        public void Merge_IsDeterministic_ByteIdentical()
        {
            var kit = Kit(("pine", Tri(Vector3.Zero, new Vector4(0.3f, 0.6f, 0.2f, 1f))),
                          ("rock", Tri(new Vector3(0.1f, 0f, 0f), new Vector4(0.5f, 0.5f, 0.5f, 1f))));
            var placements = new List<PropPlacement>
            {
                new PropPlacement("pine", 3f, 1f, 4f, 1.3f, 0.7f, 0),
                new PropPlacement("rock", -2f, 0f, 8f, 0.8f, 2.1f, 1),
                new PropPlacement("pine", 12f, 2f, -5f, 1f, 4.4f, 0),
            };

            AssertMeshByteIdentical(PropHlod.Merge(placements, kit), PropHlod.Merge(placements, kit));
        }

        // ---- Weld: reduction bounds, colour preservation, guard ----

        [Fact]
        public void Weld_ReducesTriangleCount_MoreAtLargerCells()
        {
            GltfMesh sphere = MeshPrimitives.Sphere(radius: 5f, rings: 16, segments: 24);
            GltfMesh small = PropHlod.Weld(sphere, 1.5f);
            GltfMesh large = PropHlod.Weld(sphere, 3.0f);

            Assert.True(small.TriangleCount < sphere.TriangleCount, "a weld must not increase triangles");
            Assert.True(large.TriangleCount < small.TriangleCount, "a coarser weld must reduce further");
            Assert.True(small.Vertices.Length < sphere.Vertices.Length, "a weld must collapse vertices");
        }

        [Fact]
        public void Weld_AveragesColourOfCollapsedVertices()
        {
            // Two vertices in the same 10 m cell, colours (1,0,0) and (0,1,0): the welded vertex is the average.
            var v = new[]
            {
                new ModelVertex(new Vector3(0f, 0f, 0f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f)),
                new ModelVertex(new Vector3(1f, 0f, 0f), Vector3.UnitY, new Vector4(0f, 1f, 0f, 1f)),
                new ModelVertex(new Vector3(2f, 0f, 0f), Vector3.UnitY, new Vector4(0f, 0f, 1f, 1f)),
            };
            var mesh = new GltfMesh(v, new ushort[] { 0, 1, 2 });

            GltfMesh welded = PropHlod.Weld(mesh, 10f);   // all three fall in cell (0,0,0)

            Assert.Single(welded.Vertices);
            Vector4 c = welded.Vertices[0].Color;
            Assert.Equal(1f / 3f, c.X, 4);
            Assert.Equal(1f / 3f, c.Y, 4);
            Assert.Equal(1f / 3f, c.Z, 4);
        }

        [Fact]
        public void Weld_IsDeterministic_ByteIdentical()
        {
            GltfMesh sphere = MeshPrimitives.Sphere(radius: 4f, rings: 12, segments: 18);
            AssertMeshByteIdentical(PropHlod.Weld(sphere, 1.2f), PropHlod.Weld(sphere, 1.2f));
        }

        [Fact]
        public void Weld_RejectsNonPositiveCell()
        {
            GltfMesh sphere = MeshPrimitives.Sphere(radius: 2f, rings: 6, segments: 8);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => PropHlod.Weld(sphere, 0f));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => PropHlod.Weld(sphere, -1f));
        }

        // ---- BuildMergedMesh: merge, optional weld ----

        [Fact]
        public void BuildMergedMesh_ZeroCell_KeepsFullDetailMerge()
        {
            var kit = Kit(("pine", MeshPrimitives.Sphere(radius: 3f, rings: 10, segments: 14)));
            var placements = new List<PropPlacement>
            {
                new PropPlacement("pine", 0f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("pine", 8f, 0f, 0f, 1f, 0f, 0),
            };

            GltfMesh full = PropHlod.BuildMergedMesh(placements, kit, weldCellSize: 0f);
            GltfMesh merge = PropHlod.Merge(placements, kit);

            Assert.Equal(merge.TriangleCount, full.TriangleCount);   // no weld: identical to the bare merge
        }

        [Fact]
        public void BuildMergedMesh_PositiveCell_WeldsBelowMergeTriangles()
        {
            var kit = Kit(("pine", MeshPrimitives.Sphere(radius: 3f, rings: 12, segments: 18)));
            var placements = new List<PropPlacement>
            {
                new PropPlacement("pine", 0f, 0f, 0f, 1f, 0f, 0),
                new PropPlacement("pine", 8f, 0f, 0f, 1f, 0f, 0),
            };

            GltfMesh merge = PropHlod.Merge(placements, kit);
            GltfMesh welded = PropHlod.BuildMergedMesh(placements, kit, weldCellSize: 1.5f);

            Assert.True(welded.TriangleCount < merge.TriangleCount, "a positive weld cell must reduce triangles");
            AssertMeshByteIdentical(welded, PropHlod.BuildMergedMesh(placements, kit, weldCellSize: 1.5f));
        }

        // ---- CrossfadeAt: 0 near, 1 far, linear across the band, hard swap on a zero width ----

        [Fact]
        public void Crossfade_ZeroBeforeBand_OneAfterBand_HalfAtCentre()
        {
            // hlodDistance 100, width 40 -> band [80, 120]. Below 80 = 0 (props), above 120 = 1 (HLOD), 100 = 0.5.
            Assert.Equal(0f, PropHlod.CrossfadeAt(70f, 100f, 40f), 4);
            Assert.Equal(0f, PropHlod.CrossfadeAt(80f, 100f, 40f), 4);   // inner edge
            Assert.Equal(0.5f, PropHlod.CrossfadeAt(100f, 100f, 40f), 4);
            Assert.Equal(0.75f, PropHlod.CrossfadeAt(110f, 100f, 40f), 4);
            Assert.Equal(1f, PropHlod.CrossfadeAt(120f, 100f, 40f), 4);   // outer edge
            Assert.Equal(1f, PropHlod.CrossfadeAt(200f, 100f, 40f), 4);
        }

        [Fact]
        public void Crossfade_ZeroWidth_IsAHardSwap()
        {
            Assert.Equal(0f, PropHlod.CrossfadeAt(99f, 100f, 0f), 4);    // just inside: full props
            Assert.Equal(1f, PropHlod.CrossfadeAt(100f, 100f, 0f), 4);   // at the distance: full HLOD
            Assert.Equal(1f, PropHlod.CrossfadeAt(101f, 100f, 0f), 4);
        }

        [Fact]
        public void Crossfade_IsDeterministic_SameDistanceSameFade()
        {
            Assert.Equal(PropHlod.CrossfadeAt(105f, 100f, 40f), PropHlod.CrossfadeAt(105f, 100f, 40f));
        }
    }
}
