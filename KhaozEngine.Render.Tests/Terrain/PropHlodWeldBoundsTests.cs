using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Bounds tests for the HLOD merge+weld path (KhaozEngine issue #402: a win-x64 field crash where
    /// <see cref="PropHlod.Weld"/> threw <see cref="System.IndexOutOfRangeException"/> during a background chunk
    /// build). Two separate concerns live here.
    /// <para><b>The index buffer.</b> <see cref="PropHlod.Weld"/> rebuilds triangles by looking each corner up in a
    /// per-vertex remap table, and <see cref="PropHlod.Merge"/> rebases every source index onto the merged vertex
    /// array. Neither used to check that a corner actually refers to a vertex that exists, so ONE index past the end
    /// of a source mesh turned into an unhandled crash on a worker thread. These tests craft exactly that mesh, so
    /// they reproduce the throw deterministically on every platform with no Windows box involved.</para>
    /// <para><b>The weld cell math.</b> The original hypothesis on the issue was an unclamped <c>(int)</c> cast
    /// indexing a bucket ARRAY, made to overflow by x64-vs-arm64 float rounding. There is no such array: the cell
    /// key feeds a <see cref="Dictionary{TKey,TValue}"/> and cell ids are dense, so no cell key can ever index out of
    /// range no matter how the cast rounds. The boundary/non-finite cases below pin that down, so the refutation is
    /// executable rather than a claim in a comment.</para></summary>
    public class PropHlodWeldBoundsTests
    {
        static ModelVertex V(float x, float y, float z) =>
            new(new Vector3(x, y, z), Vector3.UnitY, new Vector4(1f, 1f, 1f, 1f));

        // A well-formed single triangle: three vertices, three in-range indices.
        static GltfMesh Tri() =>
            new(new[] { V(0f, 0f, 0f), V(1f, 0f, 0f), V(0f, 0f, 1f) }, new uint[] { 0, 1, 2 });

        // ---- The real defect: a corner index past the end of the vertex array ----

        [Fact]
        public void Weld_DoesNotThrow_WhenACornerIndexIsPastTheVertexArray()
        {
            // Three vertices (valid ids 0..2), but the triangle names vertex 3. This is the exact shape that reached
            // remap[si[t]] in the field. Nothing about it is platform specific.
            var mesh = new GltfMesh(
                new[] { V(0f, 0f, 0f), V(4f, 0f, 0f), V(0f, 0f, 4f) },
                new uint[] { 0, 1, 3 });

            GltfMesh welded = PropHlod.Weld(mesh, 1f);

            // Degraded, not crashed: the one unrepresentable triangle is dropped, exactly like a degenerate one.
            Assert.Equal(0, welded.TriangleCount);
        }

        [Fact]
        public void Weld_DropsOnlyTheBadTriangle_AndKeepsTheValidOnes()
        {
            // Two triangles that weld to distinct cells, plus a third naming a vertex that does not exist.
            var mesh = new GltfMesh(
                new[]
                {
                    V(0f, 0f, 0f), V(4f, 0f, 0f), V(0f, 0f, 4f),
                    V(20f, 0f, 0f), V(24f, 0f, 0f), V(20f, 0f, 4f),
                },
                new uint[] { 0, 1, 2, 3, 4, 5, 0, 1, 99 });

            GltfMesh welded = PropHlod.Weld(mesh, 1f);

            Assert.Equal(2, welded.TriangleCount);
            // Every surviving corner addresses a vertex that exists.
            foreach (uint i in welded.Indices32) Assert.True(i < (uint)welded.Vertices.Length);
        }

        [Fact]
        public void Weld_TreatsTheLargestPossibleIndexAsOutOfRange_NotAsANegativeOffset()
        {
            // uint.MaxValue would sign-extend to -1 if it were ever narrowed to int before the bounds check.
            var mesh = new GltfMesh(
                new[] { V(0f, 0f, 0f), V(4f, 0f, 0f), V(0f, 0f, 4f) },
                new uint[] { 0, 1, uint.MaxValue });

            GltfMesh welded = PropHlod.Weld(mesh, 1f);

            Assert.Equal(0, welded.TriangleCount);
        }

        [Fact]
        public void Merge_NeverEmitsAnIndexPastTheMergedVertexArray()
        {
            // A malformed kit mesh: 3 vertices, but a corner naming vertex 7. Merge rebases indices onto the merged
            // array, so without a guard it writes 7 and then 10 for the second placement, both past the 6 vertices
            // it actually produced, and the crash lands in whatever reads the merged mesh next.
            var bad = new GltfMesh(
                new[] { V(0f, 0f, 0f), V(1f, 0f, 0f), V(0f, 0f, 1f) },
                new uint[] { 0, 1, 7 });
            var kit = new Dictionary<string, GltfMesh> { ["bad"] = bad };
            var placements = new List<PropPlacement>
            {
                new("bad", 0f, 0f, 0f, 1f, 0f, 0),
                new("bad", 30f, 0f, 0f, 1f, 0f, 0),
            };

            GltfMesh merged = PropHlod.Merge(placements, kit);

            Assert.Equal(6, merged.Vertices.Length);
            foreach (uint i in merged.Indices32) Assert.True(i < (uint)merged.Vertices.Length);
        }

        [Fact]
        public void BuildMergedMesh_SurvivesAMalformedSourceMesh_OnBothTheWeldedAndUnweldedPaths()
        {
            var bad = new GltfMesh(
                new[] { V(0f, 0f, 0f), V(1f, 0f, 0f), V(0f, 0f, 1f) },
                new uint[] { 0, 1, 7 });
            var kit = new Dictionary<string, GltfMesh> { ["bad"] = bad };
            var placements = new List<PropPlacement> { new("bad", 0f, 0f, 0f, 1f, 0f, 0) };

            GltfMesh welded = PropHlod.BuildMergedMesh(placements, kit, 1.5f);
            GltfMesh raw = PropHlod.BuildMergedMesh(placements, kit, 0f);   // non-positive cell skips the weld

            foreach (uint i in welded.Indices32) Assert.True(i < (uint)welded.Vertices.Length);
            foreach (uint i in raw.Indices32) Assert.True(i < (uint)raw.Vertices.Length);
        }

        // ---- The guard must be invisible to well-formed input (the goldens-hold contract) ----

        [Fact]
        public void Weld_OutputIsUnchanged_ForAWellFormedMesh()
        {
            // Two triangles sharing an edge, welded at a cell that keeps them distinct.
            var mesh = new GltfMesh(
                new[] { V(0f, 0f, 0f), V(4f, 0f, 0f), V(0f, 0f, 4f), V(8f, 0f, 8f) },
                new uint[] { 0, 1, 2, 1, 2, 3 });

            GltfMesh welded = PropHlod.Weld(mesh, 1f);

            Assert.Equal(2, welded.TriangleCount);
            Assert.Equal(4, welded.Vertices.Length);
            Assert.Equal(new uint[] { 0, 1, 2, 1, 2, 3 }, welded.Indices32);
        }

        [Fact]
        public void Merge_OutputIsUnchanged_ForAWellFormedKit()
        {
            var kit = new Dictionary<string, GltfMesh> { ["pine"] = Tri() };
            var placements = new List<PropPlacement>
            {
                new("pine", 0f, 0f, 0f, 1f, 0f, 0),
                new("pine", 10f, 0f, 0f, 1f, 0f, 0),
            };

            GltfMesh merged = PropHlod.Merge(placements, kit);

            Assert.Equal(6, merged.Vertices.Length);
            Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, merged.Indices32);
        }

        // ---- The refuted hypothesis: cell keys cannot index anything ----

        [Fact]
        public void Weld_HandlesVerticesExactlyOnACellBoundary()
        {
            // (pos / cellSize) landing exactly on an integer is the case the issue predicted would step one cell past
            // a bucket array. There is no bucket array, so this is simply a normal weld: it must not throw, and the
            // vertices either side of the boundary must land in different cells.
            const float cell = 0.5f;
            var mesh = new GltfMesh(
                new[]
                {
                    V(0f, 0f, 0f),           // exactly on 0
                    V(cell, 0f, 0f),         // exactly on the 1st boundary
                    V(cell * 2f, 0f, 0f),    // exactly on the 2nd boundary
                },
                new uint[] { 0, 1, 2 });

            GltfMesh welded = PropHlod.Weld(mesh, cell);

            Assert.Equal(3, welded.Vertices.Length);   // three separate cells, nothing collapsed, nothing thrown
            Assert.Equal(1, welded.TriangleCount);
        }

        [Fact]
        public void Weld_HandlesNonFiniteAndExtremePositions_WithoutIndexingOutOfRange()
        {
            // (int)MathF.Floor(NaN / cell) and the +/-infinity cases are exactly where x64 (SSE cvttss2si) and arm64
            // (NEON fcvtzs) historically disagreed. Here the cast only ever produces a Dictionary KEY, so whichever
            // value it yields the weld stays in range. If this ever throws IndexOutOfRangeException, a cell key has
            // become an array index somewhere and the refutation above no longer holds.
            var mesh = new GltfMesh(
                new[]
                {
                    V(float.NaN, 0f, 0f),
                    V(float.PositiveInfinity, 0f, 0f),
                    V(float.NegativeInfinity, 0f, 0f),
                    V(float.MaxValue, float.MinValue, 0f),
                    V(0f, 0f, 0f),
                    V(1f, 0f, 1f),
                },
                new uint[] { 0, 1, 2, 3, 4, 5 });

            GltfMesh welded = PropHlod.Weld(mesh, 1f);

            foreach (uint i in welded.Indices32) Assert.True(i < (uint)welded.Vertices.Length);
        }
    }
}
