using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for the world-locked clipmap water grid (<see cref="WaterGridMode.Clipmap"/>): the ring
    /// layout, the per-ring world snap that is the whole point of it, the exact nesting of each ring's hole against
    /// the next ring in, the stitch vertices that close the resulting T-junctions, and the per-ring mip selection.
    /// All GPU-free - the layout is pure CPU math by design, exactly as
    /// <see cref="WaterMath.BuildGridPositions"/> is for the camera-focused grid.
    /// </summary>
    public sealed class WaterClipmapTests
    {
        const float Cell = 0.5f;
        const int Ring = 32;

        static WaterPlane BigPlane() => new(centerX: 0f, surfaceY: 3f, centerZ: 0f, halfExtentX: 4000f);

        static (WaterClipmapVertex[] Verts, uint[] Indices, int VCount, int ICount) Build(
            in WaterPlane plane, float fx, float fz, int levels, float cell = Cell, int ring = Ring,
            float geomorph = 0f)
            => BuildAt(plane, fx, fz, levels, default, cell, ring, geomorph);

        /// <summary>Build against a render origin. <paramref name="plane"/> and the focus are ABSOLUTE either way -
        /// that is the contract, and it is what keeps the lattice world-anchored.</summary>
        static (WaterClipmapVertex[] Verts, uint[] Indices, int VCount, int ICount) BuildAt(
            in WaterPlane plane, float fx, float fz, int levels, Vector3 renderOrigin,
            float cell = Cell, int ring = Ring, float geomorph = 0f)
        {
            var verts = new WaterClipmapVertex[WaterClipmap.VertexCount(levels, ring)];
            var indices = new uint[WaterClipmap.IndexCount(levels, ring)];
            Vector2 focus = WaterClipmap.ClampFocus(plane, fx, fz);
            int vc = WaterClipmap.Build(plane, focus.X, focus.Y, cell, ring, levels, geomorph, verts, indices,
                out int ic, renderOrigin);
            return (verts, indices, vc, ic);
        }

        // ---- Sizing ------------------------------------------------------------------------------------------

        [Theory]
        [InlineData(32, 32)]
        [InlineData(30, 28)]     // rounded DOWN to a multiple of 4
        [InlineData(4, 8)]       // below the floor
        [InlineData(1000, 256)]  // above the ceiling
        public void ClampRingCells_RoundsDownToAMultipleOfFourInsideTheBounds(int requested, int expected)
        {
            int n = WaterClipmap.ClampRingCells(requested);
            Assert.Equal(expected, n);
            Assert.Equal(0, n % 4);
            Assert.InRange(n, WaterClipmap.MinRingCells, WaterClipmap.MaxRingCells);
        }

        [Fact]
        public void BuildWritesExactlyTheCountsItAdvertises()
        {
            const int levels = 5;
            var r = Build(BigPlane(), 17.3f, -8.1f, levels);
            Assert.Equal(WaterClipmap.VertexCount(levels, Ring), r.VCount);
            Assert.Equal(WaterClipmap.IndexCount(levels, Ring), r.ICount);
            // Index count is a pure function of (levels, ringCells): the hole moves with the snap but never
            // changes size, which is what lets the buffers be sized once.
            for (float f = 0f; f < 40f; f += 0.37f)
                Assert.Equal(r.ICount, Build(BigPlane(), f, f * 0.7f, levels).ICount);
        }

        [Fact]
        public void EveryIndexIsInRangeAndEveryTriangleHasArea()
        {
            const int levels = 4;
            var r = Build(BigPlane(), 3.9f, 11.2f, levels);
            for (int i = 0; i < r.ICount; i += 3)
            {
                uint a = r.Indices[i], b = r.Indices[i + 1], c = r.Indices[i + 2];
                Assert.InRange(a, 0u, (uint)r.VCount - 1);
                Assert.InRange(b, 0u, (uint)r.VCount - 1);
                Assert.InRange(c, 0u, (uint)r.VCount - 1);
                Vector3 pa = r.Verts[a].Position, pb = r.Verts[b].Position, pc = r.Verts[c].Position;
                float area2 = MathF.Abs((pb.X - pa.X) * (pc.Z - pa.Z) - (pc.X - pa.X) * (pb.Z - pa.Z));
                Assert.True(area2 > 1e-6f,
                    $"triangle {i / 3} is degenerate at {pa}/{pb}/{pc}: the ring transitions are supposed to be " +
                    "closed by stitch vertices, not by collapsing quads.");
            }
        }

        [Fact]
        public void EveryVertexSitsOnItsOwnRingsLatticeAtTheSurfaceHeight()
        {
            const int levels = 5;
            WaterPlane plane = BigPlane();
            Vector2 focus = WaterClipmap.ClampFocus(plane, 21.7f, -4.4f);
            var r = Build(plane, 21.7f, -4.4f, levels);
            int stride = Ring + 1, perLevel = stride * stride;
            for (int l = 0; l < levels; l++)
            {
                float c = WaterClipmap.CellSize(Cell, l);
                float ox = WaterClipmap.SnapOrigin(focus.X, c), oz = WaterClipmap.SnapOrigin(focus.Y, c);
                for (int v = 0; v < perLevel; v++)
                {
                    Vector3 p = r.Verts[l * perLevel + v].Position;
                    Assert.Equal(plane.SurfaceY, p.Y);
                    // On the lattice, or on the HALF lattice for a stitched boundary vertex (which by construction
                    // sits midway between two coarse neighbours - still world-anchored, just not on this ring's
                    // own nodes). Both are exact multiples of half a cell from the ring origin.
                    Assert.True(OnLattice(p.X - ox, c * 0.5f), $"level {l} vertex X {p.X} is off the lattice");
                    Assert.True(OnLattice(p.Z - oz, c * 0.5f), $"level {l} vertex Z {p.Z} is off the lattice");
                }
            }
        }

        static bool OnLattice(float offset, float quantum)
            => MathF.Abs(offset / quantum - MathF.Round(offset / quantum)) < 1e-3f;

        // ---- The world lock ----------------------------------------------------------------------------------

        [Fact]
        public void SubCellCameraMotionMovesNoVertexAtAll()
        {
            const int levels = 5;
            WaterPlane plane = BigPlane();
            // Both positions round to the same snap index on every level, which is the common case: level 0's
            // quantum is 2 * Cell = 1 m, so a 0.1 m step almost never crosses one.
            var a = Build(plane, 10.02f, 5.03f, levels);
            var b = Build(plane, 10.12f, 5.13f, levels);
            Assert.Equal(a.VCount, b.VCount);
            for (int i = 0; i < a.VCount; i++)
                Assert.Equal(a.Verts[i].Position, b.Verts[i].Position);
            Assert.Equal(a.Indices, b.Indices);
        }

        [Fact]
        public void ASnapMovesEachRingByAWholeNumberOfItsOwnCells()
        {
            const int levels = 6;
            WaterPlane plane = BigPlane();
            // A deliberately large sweep, so every level crosses its own boundary many times over.
            for (float f = 0f; f < 60f; f += 0.13f)
            {
                for (int l = 0; l < levels; l++)
                {
                    float c = WaterClipmap.CellSize(Cell, l);
                    float o0 = WaterClipmap.SnapOrigin(0f, c);
                    float o = WaterClipmap.SnapOrigin(f, c);
                    float cells = (o - o0) / c;
                    Assert.True(MathF.Abs(cells - MathF.Round(cells)) < 1e-3f,
                        $"level {l} moved {cells} cells for a focus of {f}: a partial-cell move is exactly the " +
                        "resampling this grid exists to remove.");
                    // And the step is EVEN, which is what keeps the coarser ring's hole on this ring's lattice.
                    Assert.True(MathF.Abs(MathF.IEEERemainder(MathF.Round(cells), 2f)) < 1e-3f,
                        $"level {l} moved an odd {cells} cells for a focus of {f}.");
                }
            }
        }

        [Fact]
        public void APureLatticeShiftLeavesTheSharedGeometryWhereItWas()
        {
            const int levels = 3;
            WaterPlane plane = BigPlane();
            // A move of exactly one level-0 quantum: every level either does not move or moves by whole cells, so
            // the vertices that survive the shift have to be at world positions the previous build also had.
            var a = Build(plane, 0f, 0f, levels);
            var b = Build(plane, 2f * Cell, 0f, levels);

            var before = new HashSet<(long, long, long)>();
            for (int i = 0; i < a.VCount; i++) before.Add(Quantize(a.Verts[i]));

            int shared = 0, missing = 0;
            for (int i = 0; i < b.VCount; i++)
            {
                Vector3 p = b.Verts[i].Position;
                // Only the innermost level is fully inside the previous build's coverage on both sides of the
                // shift; the outer levels gain a strip. Restrict to what must have been covered.
                if (MathF.Abs(p.X) > 4f || MathF.Abs(p.Z) > 4f) continue;
                if (before.Contains(Quantize(b.Verts[i]))) shared++; else missing++;
            }
            Assert.True(shared > 0, "the shift shared no vertices at all, so the comparison proved nothing");
            Assert.Equal(0, missing);
        }

        static (long, long, long) Quantize(in WaterClipmapVertex v) => (
            (long)MathF.Round(v.Position.X * 1024f),
            (long)MathF.Round(v.Position.Z * 1024f),
            (long)MathF.Round(v.Cell * 1024f));

        // ---- Camera-relative rendering -----------------------------------------------------------------------

        /// <summary>
        /// The world lock has to survive the render origin, in both directions.
        /// <para>
        /// A REBASE must not move a ring: the snap is decided on absolute coordinates, so the same camera at the
        /// same world position must produce the same lattice whatever origin the frame happens to be expressed
        /// against. And it must hold at DISTANCE: a per-vertex absolute position at 100 km has already rounded to
        /// the ~8 mm float lattice, so a build that reduced there instead of on the ring origins would show the
        /// grid quietly re-quantizing the further out the world goes.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0f)]
        [InlineData(1024f)]
        [InlineData(102400f)]    // 100 km, the case camera-relative rendering exists for
        public void TheLatticeIsUnchangedByTheRenderOriginAtAnyDistance(float distance)
        {
            const int levels = 5;
            // The frame grid the render origin is quantized to, so these are the origins a scene can actually pick.
            const float FrameGrid = 128f;
            float originValue = MathF.Round(distance / FrameGrid) * FrameGrid;
            var origin = new Vector3(originValue, 0f, originValue);

            // The same WORLD state, expressed two ways: absolute, and against the render origin.
            var absPlane = new WaterPlane(distance, 0f, distance, 4000f);
            float camX = distance + 3.3f, camZ = distance - 2.1f;

            var flat = Build(absPlane, camX, camZ, levels);
            var shifted = BuildAt(absPlane, camX, camZ, levels, origin);

            Assert.Equal(flat.VCount, shifted.VCount);
            Assert.Equal(flat.Indices, shifted.Indices);
            for (int i = 0; i < flat.VCount; i++)
            {
                // Same lattice: every vertex sits at the same place in the world once the origin is added back.
                // The tolerance is a hair over the float spacing at the SMALL (reduced) magnitudes both builds
                // work in, which is the whole point - it is NOT the 8 mm spacing at 100 km.
                Assert.Equal(flat.Verts[i].Position.X - distance,
                    shifted.Verts[i].Position.X - (distance - originValue), 4);
                Assert.Equal(flat.Verts[i].Position.Z - distance,
                    shifted.Verts[i].Position.Z - (distance - originValue), 4);
                Assert.Equal(flat.Verts[i].Coarse, shifted.Verts[i].Coarse);
                Assert.Equal(flat.Verts[i].Cell, shifted.Verts[i].Cell);
            }
        }

        /// <summary>
        /// At 100 km the reduced grid must still be EXACT: cells the size they were asked for, not the float
        /// lattice's. This is what fails if the render origin is subtracted per VERTEX instead of per ring.
        /// <para>
        /// The cell size here is 0.3 and that is not incidental. A power-of-two cell (0.5, the shipped default) is
        /// a whole multiple of the float32 spacing at 100 km, so every absolute vertex position lands exactly on
        /// the lattice and a per-vertex subtraction is harmless - a test at the default would pass either way and
        /// prove nothing. At 0.3 the same subtraction diverges by 3.1 mm, which is a real re-quantization of the
        /// grid, and <see cref="WaterSettings.ClipmapCellSize"/> is a free float that a consumer may set to
        /// anything.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(0.3f)]
        [InlineData(0.5f)]
        public void ReducedVertexSpacingStaysExactAHundredKilometresOut(float baseCell)
        {
            const int levels = 4;
            const float distance = 102400f;
            var origin = new Vector3(distance, 0f, distance);
            var plane = new WaterPlane(distance, 0f, distance, 4000f);
            var r = BuildAt(plane, distance + 1.7f, distance - 0.9f, levels, origin, baseCell);

            int stride = Ring + 1, perLevel = stride * stride;
            for (int l = 0; l < levels; l++)
            {
                float c = WaterClipmap.CellSize(baseCell, l);
                // Walk a row through the middle of the level, skipping the stitched boundary (which is a
                // half-cell by design), and require every step to be the cell size to within a micron.
                int j = Ring / 2;
                for (int i = 1; i < Ring - 1; i++)
                {
                    float a = r.Verts[l * perLevel + j * stride + i].Position.X;
                    float b = r.Verts[l * perLevel + j * stride + i + 1].Position.X;
                    Assert.Equal(c, b - a, 5);
                }
            }
        }

        // ---- Ring nesting ------------------------------------------------------------------------------------

        [Fact]
        public void EachRingsHoleIsExactlyTheNextRingInsExtent()
        {
            const int levels = 6;
            WaterPlane plane = BigPlane();
            for (float f = 0f; f < 25f; f += 0.29f)
            {
                Vector2 focus = WaterClipmap.ClampFocus(plane, f, f * -0.6f);
                var r = Build(plane, f, f * -0.6f, levels);
                (float MinX, float MaxX, float MinZ, float MaxZ)[] drawn = DrawnBounds(r, levels);
                for (int l = 1; l < levels; l++)
                {
                    float finer = WaterClipmap.CellSize(Cell, l - 1);
                    float fx = WaterClipmap.SnapOrigin(focus.X, finer), fz = WaterClipmap.SnapOrigin(focus.Y, finer);
                    float half = Ring * 0.5f * finer;
                    // Level l's drawn area is its full square minus a hole; the hole's bounds are what level l-1
                    // covers. Assert on level l-1's OUTER bounds, which is the same statement from the other side.
                    Assert.Equal(fx - half, drawn[l - 1].MinX, 3);
                    Assert.Equal(fx + half, drawn[l - 1].MaxX, 3);
                    Assert.Equal(fz - half, drawn[l - 1].MinZ, 3);
                    Assert.Equal(fz + half, drawn[l - 1].MaxZ, 3);
                    Assert.Equal(HoleBounds(r, l, focus), (fx - half, fx + half, fz - half, fz + half));
                }
            }
        }

        /// <summary>World bounds of the quads a level actually draws.</summary>
        static (float MinX, float MaxX, float MinZ, float MaxZ)[] DrawnBounds(
            (WaterClipmapVertex[] Verts, uint[] Indices, int VCount, int ICount) r, int levels)
        {
            int perLevel = (Ring + 1) * (Ring + 1);
            var bounds = new (float, float, float, float)[levels];
            for (int l = 0; l < levels; l++) bounds[l] = (float.MaxValue, float.MinValue, float.MaxValue, float.MinValue);
            for (int i = 0; i < r.ICount; i++)
            {
                int v = (int)r.Indices[i];
                int l = v / perLevel;
                Vector3 p = r.Verts[v].Position;
                var b = bounds[l];
                bounds[l] = (MathF.Min(b.Item1, p.X), MathF.Max(b.Item2, p.X),
                             MathF.Min(b.Item3, p.Z), MathF.Max(b.Item4, p.Z));
            }
            return bounds;
        }

        /// <summary>World bounds of the hole in level <paramref name="level"/>, recovered from which of its
        /// vertices no triangle references.</summary>
        static (float, float, float, float) HoleBounds(
            (WaterClipmapVertex[] Verts, uint[] Indices, int VCount, int ICount) r, int level, Vector2 focus)
        {
            int stride = Ring + 1, perLevel = stride * stride, baseV = level * perLevel;
            var used = new bool[perLevel];
            for (int i = 0; i < r.ICount; i++)
            {
                int v = (int)r.Indices[i] - baseV;
                if (v >= 0 && v < perLevel) used[v] = true;
            }
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float c = WaterClipmap.CellSize(Cell, level);
            float ox = WaterClipmap.SnapOrigin(focus.X, c), oz = WaterClipmap.SnapOrigin(focus.Y, c);
            for (int j = 0; j <= Ring; j++)
            {
                for (int i = 0; i <= Ring; i++)
                {
                    if (used[j * stride + i]) continue;
                    // An unused vertex is strictly inside the hole, so the hole's own boundary is one cell out.
                    minX = MathF.Min(minX, ox + (i - 1 - Ring / 2) * c);
                    maxX = MathF.Max(maxX, ox + (i + 1 - Ring / 2) * c);
                    minZ = MathF.Min(minZ, oz + (j - 1 - Ring / 2) * c);
                    maxZ = MathF.Max(maxZ, oz + (j + 1 - Ring / 2) * c);
                }
            }
            return (minX, maxX, minZ, maxZ);
        }

        // ---- Stitching ---------------------------------------------------------------------------------------

        [Fact]
        public void StitchVerticesNameTheirTwoCoarseNeighboursAndSitMidwayBetweenThem()
        {
            const int levels = 5;
            WaterPlane plane = BigPlane();
            var r = Build(plane, 6.4f, -13.9f, levels);
            int stride = Ring + 1, perLevel = stride * stride;

            // Everything the coarse rings can be met at: a boundary vertex of level l+1 is only ever met by level
            // l's boundary, so index the coarse positions once.
            var coarsePositions = new HashSet<(long, long)>();
            for (int i = 0; i < r.VCount; i++)
                coarsePositions.Add(((long)MathF.Round(r.Verts[i].Position.X * 1024f),
                                     (long)MathF.Round(r.Verts[i].Position.Z * 1024f)));

            int stitched = 0;
            for (int l = 0; l < levels; l++)
            {
                float c = WaterClipmap.CellSize(Cell, l);
                float coarse = l + 1 < levels ? WaterClipmap.CellSize(Cell, l + 1) : c;
                for (int j = 0; j <= Ring; j++)
                {
                    for (int i = 0; i <= Ring; i++)
                    {
                        WaterClipmapVertex v = r.Verts[l * perLevel + j * stride + i];
                        bool boundary = i == 0 || i == Ring || j == 0 || j == Ring;
                        // Both sides of a shared boundary must band-limit to the SAME spacing, or they cannot
                        // evaluate to the same height and the seam opens.
                        Assert.Equal(boundary && l + 1 < levels ? coarse : c, v.Cell, 4);
                        if (v.Coarse == Vector2.Zero) continue;

                        stitched++;
                        Assert.True(boundary, "only ring-boundary vertices may stitch");
                        Assert.True(l + 1 < levels, "the outermost ring has nothing to stitch to");
                        // The stitch names its two coarse neighbours exactly, and the vertex is their midpoint.
                        Assert.Equal(c, MathF.Abs(v.Coarse.X) + MathF.Abs(v.Coarse.Y), 4);
                        foreach (float s in new[] { -1f, 1f })
                        {
                            var p = new Vector2(v.Position.X + s * v.Coarse.X, v.Position.Z + s * v.Coarse.Y);
                            Assert.Contains(((long)MathF.Round(p.X * 1024f), (long)MathF.Round(p.Y * 1024f)),
                                coarsePositions);
                        }
                    }
                }
            }
            // Four sides, every other vertex, on every level but the outermost.
            Assert.Equal((levels - 1) * 4 * (Ring / 2), stitched);
        }

        /// <summary>
        /// The seam-freedom condition itself, stated geometrically and checked where it is most likely to break:
        /// on a plane small enough that the outer rings overhang it and the clamp is live on both sides of a
        /// boundary. The shader evaluates a stitched vertex at <c>Position +/- Stitch</c> and averages, so the
        /// vertex lands on the coarse ring's edge segment exactly when both taps ARE coarse vertices and Position
        /// is their midpoint. Clamping is applied to the taps independently, so this is the check that it still
        /// commutes with the coarse ring's own clamped vertices.
        /// </summary>
        [Theory]
        [InlineData(4000f, 4000f)]   // no clamping at all: the baseline
        [InlineData(9f, 9f)]         // rings overhang badly; almost everything clamps
        [InlineData(40f, 6f)]        // a long thin plane: clamped on one axis, free on the other
        [InlineData(11.5f, 7.25f)]   // edges deliberately off the lattice
        public void StitchedVerticesLandOnTheCoarseRingsEdgeEvenWhenThePlaneClampsThem(float halfX, float halfZ)
        {
            const int levels = 5;
            var plane = new WaterPlane(centerX: 1.5f, surfaceY: 0f, centerZ: -0.75f, halfExtentX: halfX,
                halfExtentZ: halfZ);
            var r = Build(plane, 3.3f, -2.1f, levels);
            int stride = Ring + 1, perLevel = stride * stride;

            int checkedCount = 0;
            for (int l = 0; l + 1 < levels; l++)
            {
                // Every position the NEXT ring out actually has a vertex at.
                var coarse = new HashSet<(long, long)>();
                for (int v = 0; v < perLevel; v++)
                {
                    Vector3 p = r.Verts[(l + 1) * perLevel + v].Position;
                    coarse.Add(Q(p.X, p.Z));
                }

                for (int v = 0; v < perLevel; v++)
                {
                    WaterClipmapVertex vert = r.Verts[l * perLevel + v];
                    int i = v % stride, j = v / stride;
                    if (i != 0 && i != Ring && j != 0 && j != Ring) continue;   // boundary vertices only

                    var lo = new Vector2(vert.Position.X - vert.Coarse.X, vert.Position.Z - vert.Coarse.Y);
                    var hi = new Vector2(vert.Position.X + vert.Coarse.X, vert.Position.Z + vert.Coarse.Y);
                    // Both taps have to be real coarse vertices. For an unstitched boundary vertex the two taps
                    // collapse onto the vertex itself, which must then be a coarse vertex in its own right - that
                    // is the even-index case, and it is just as load-bearing for the seam.
                    Assert.True(coarse.Contains(Q(lo.X, lo.Y)),
                        $"level {l} boundary vertex at {vert.Position} taps {lo}, which level {l + 1} has no " +
                        "vertex at, so the two sides evaluate different points and the seam opens.");
                    Assert.True(coarse.Contains(Q(hi.X, hi.Y)),
                        $"level {l} boundary vertex at {vert.Position} taps {hi}, which level {l + 1} has no " +
                        "vertex at.");
                    // And it must sit at their midpoint, or it is off the segment those two taps span.
                    Assert.Equal((lo.X + hi.X) * 0.5f, vert.Position.X, 4);
                    Assert.Equal((lo.Y + hi.Y) * 0.5f, vert.Position.Z, 4);
                    checkedCount++;
                }
            }
            Assert.True(checkedCount > 0, "no boundary vertices were checked, so this proved nothing");
        }

        static (long, long) Q(float x, float z)
            => ((long)MathF.Round(x * 4096f), (long)MathF.Round(z * 4096f));

        // ---- Plane clamping ----------------------------------------------------------------------------------

        [Fact]
        public void APlaneSmallerThanTheClipmapClampsEveryVertexInsideItsRectangle()
        {
            var plane = new WaterPlane(centerX: 5f, surfaceY: -1f, centerZ: -2f, halfExtentX: 6f, halfExtentZ: 3f);
            var r = Build(plane, 40f, 40f, levels: 4);
            for (int i = 0; i < r.VCount; i++)
            {
                Vector3 p = r.Verts[i].Position;
                Assert.InRange(p.X, plane.CenterX - plane.HalfExtentX, plane.CenterX + plane.HalfExtentX);
                Assert.InRange(p.Z, plane.CenterZ - plane.HalfExtentZ, plane.CenterZ + plane.HalfExtentZ);
            }
            // The focus clamp pulls the rings back onto the water even though the camera is well off it.
            Vector2 focus = WaterClipmap.ClampFocus(plane, 40f, 40f);
            Assert.Equal(11f, focus.X, 4);
            Assert.Equal(1f, focus.Y, 4);
        }

        [Fact]
        public void LevelsForCoversThePlaneFromAnyCameraPositionInsideIt()
        {
            foreach (float half in new[] { 8f, 70f, 600f, 4000f })
            {
                var plane = new WaterPlane(0f, 0f, 0f, half);
                int levels = WaterClipmap.LevelsFor(plane, Cell, Ring);
                Assert.InRange(levels, 1, WaterClipmap.MaxLevels);
                float outerHalf = Ring * 0.5f * WaterClipmap.CellSize(Cell, levels - 1);
                bool capped = levels == WaterClipmap.MaxLevels;
                Assert.True(capped || outerHalf >= 2f * half,
                    $"a {half} half-extent plane got {levels} levels reaching {outerHalf}, short of the 2x needed " +
                    "for a camera in the far corner.");
            }
        }

        // ---- Band limit --------------------------------------------------------------------------------------

        [Fact]
        public void MipLevelMatchesTheCellOverTexelRatioAtNyquistAndClampsBothEnds()
        {
            // maxMip 0 is the pre-mip path and must answer 0 for everything, whatever it is asked.
            Assert.Equal(0f, WaterClipmap.MipLevel(64f, 0.1f, 2f, 0f));
            // And so does samples 0, which is FootprintSamples' band-limit-off switch: the mip filter and the
            // rippleResolve attenuation must never disagree about whether the surface is being low-passed.
            Assert.Equal(0f, WaterClipmap.MipLevel(64f, 0.1f, 0f, 8f));
            // At samples 2 the wanted level is just log2(spacing / texel).
            Assert.Equal(0f, WaterClipmap.MipLevel(0.1f, 0.2f, 2f, 8f));   // finer than a texel: no mip
            Assert.Equal(0f, WaterClipmap.MipLevel(0.2f, 0.2f, 2f, 8f));
            Assert.Equal(1f, WaterClipmap.MipLevel(0.4f, 0.2f, 2f, 8f), 4);
            Assert.Equal(3f, WaterClipmap.MipLevel(1.6f, 0.2f, 2f, 8f), 4);
            Assert.Equal(8f, WaterClipmap.MipLevel(1e6f, 0.2f, 2f, 8f), 4);   // clamped to the chain
            // Oversampling asks for a coarser level: 4x the samples is 2 more mips on top of the Nyquist answer.
            Assert.Equal(3f, WaterClipmap.MipLevel(0.4f, 0.2f, 8f, 8f), 4);
        }

        [Fact]
        public void TheBandLimitIsActuallyLiveAtTheShippedCascadeSizes()
        {
            // Guards against the band limit being silently INERT. Every ring could resolve to mip 0 (if the cells
            // were always finer than a texel) and the world lock alone would still carry the acceptance test, so
            // "the artifact went down" is not evidence that this half of the fix is doing anything.
            //
            // The default sea state's three cascades at 64 texels: tiles 250 / 59.5 / 14.2 give texels of 3.9,
            // 0.93 and 0.22 m.
            float[] texels = { 250f / 64f, 250f / 4.2f / 64f, 250f / 4.2f / 4.2f / 64f };
            float maxMip = WaterClipmap.MipCount(64) - 1;
            var defaults = new WaterSettings();

            // The innermost ring already band-limits the finest cascade: half-metre cells cannot carry 0.22 m
            // content, and sampling it at LOD 0 is exactly what the diagnosis measured as boiling.
            float inner = WaterClipmap.MipLevel(WaterClipmap.CellSize(defaults.ClipmapCellSize, 0), texels[2],
                defaults.ClipmapBandLimitSamples, maxMip);
            Assert.True(inner > 0.5f, $"the innermost ring selects mip {inner} on the finest cascade, i.e. barely " +
                "any band limit at all.");

            // And it climbs monotonically outward, one mip per level, since both the cell size and the mip scale
            // double together - until the chain runs out.
            float previous = -1f;
            for (int l = 0; l < 6; l++)
            {
                float mip = WaterClipmap.MipLevel(WaterClipmap.CellSize(defaults.ClipmapCellSize, l), texels[2],
                    defaults.ClipmapBandLimitSamples, maxMip);
                Assert.True(mip >= previous, $"level {l} band-limits LESS than level {l - 1} ({mip} vs {previous})");
                if (previous >= 0f && mip < maxMip) Assert.Equal(previous + 1f, mip, 3);
                previous = mip;
            }
            Assert.Equal(maxMip, previous, 3);   // the outer rings bottom out on the chain, as they should

            // The coarsest cascade is coarse enough that the near rings leave it alone, which is the whole point
            // of selecting PER CASCADE rather than dropping whole ones.
            Assert.Equal(0f, WaterClipmap.MipLevel(WaterClipmap.CellSize(defaults.ClipmapCellSize, 0), texels[0],
                defaults.ClipmapBandLimitSamples, maxMip));
        }

        [Fact]
        public void MipCountIsTheFullChainDownToOneTexel()
        {
            Assert.Equal(1, WaterClipmap.MipCount(1));
            Assert.Equal(7, WaterClipmap.MipCount(64));
            Assert.Equal(8, WaterClipmap.MipCount(128));
            Assert.Equal(9, WaterClipmap.MipCount(256));
        }

        // ---- Budget ------------------------------------------------------------------------------------------

        [Fact]
        public void TheShippedDefaultsCostFewerTrianglesThanTheGridTheyReplace()
        {
            // The defaults, on the plane size the camera-focused grid's own documentation is written against.
            var plane = new WaterPlane(0f, 0f, 0f, 600f);
            var defaults = new WaterSettings();
            int ring = WaterClipmap.ClampRingCells(defaults.ClipmapRingCells);
            int levels = WaterClipmap.LevelsFor(plane, defaults.ClipmapCellSize, ring);
            Assert.Equal(9, levels);

            int verts = WaterClipmap.VertexCount(levels, ring);
            int tris = WaterClipmap.IndexCount(levels, ring) / 3;
            Assert.Equal(9801, verts);   // uploaded; the hole interiors are never indexed
            Assert.Equal(14336, tris);

            const int focusedVerts = WaterMath.GridResolution * WaterMath.GridResolution;
            const int focusedTris = WaterMath.GridIndexCount / 3;
            Assert.True(tris < focusedTris,
                $"the clipmap draws {tris} triangles against the camera-focused grid's {focusedTris}; it is " +
                "supposed to be the cheaper of the two as well as the steadier.");
            // Vertices are within a few per cent, and unlike the other grid they are only RE-uploaded on a snap.
            Assert.True(verts < focusedVerts * 11 / 10,
                $"the clipmap uploads {verts} vertices against {focusedVerts}.");

            // Coverage at that budget: half a metre of cell around the camera, reaching past the plane's far
            // corner from any position on it (2 * 600). Levels come in powers of two, so the last one overshoots;
            // what it overshoots into is clamped onto the plane's edge and costs no fill.
            Assert.Equal(0.5f, WaterClipmap.CellSize(defaults.ClipmapCellSize, 0));
            Assert.Equal(2048f, ring * 0.5f * WaterClipmap.CellSize(defaults.ClipmapCellSize, levels - 1));
            Assert.True(2048f >= 2f * plane.HalfExtentX);
        }

        // ---- Vertex layout -----------------------------------------------------------------------------------

        [Fact]
        public void ClipmapVertexMatchesTheStrideThePipelineDeclares()
        {
            Assert.Equal((int)WaterRenderer.ClipVertexBytes, Marshal.SizeOf<WaterClipmapVertex>());
            Assert.Equal(3 * 4 + 2 * 4 + 4 + 4, (int)WaterRenderer.ClipVertexBytes);
        }

        [Fact]
        public void TheTwoVertexSourcesDifferOnlyInTheirGridInputs()
        {
            string plain = ShaderSources.WaterVert, clip = ShaderSources.WaterClipmapVert;
            // The camera-focused source pins the band limit off and the tap count at one, both as compile-time
            // constants, so its FFT sampling collapses to the literal LOD-0 fetch it always was.
            Assert.Contains("const float bandCell = 0.0;", plain);
            Assert.Contains("const int KE_TAPS = 1;", plain);
            Assert.Contains("const vec3 tapWeights = vec3(1.0, 0.0, 0.0);", plain);
            Assert.DoesNotContain("in vec2 Coarse;", plain);
            Assert.DoesNotContain("in float Cell;", plain);
            Assert.DoesNotContain("in float Morph;", plain);

            Assert.Contains("in vec2 Coarse;", clip);
            Assert.Contains("in float Cell;", clip);
            Assert.Contains("in float Morph;", clip);
            Assert.Contains("float bandCell = Cell;", clip);
            Assert.Contains("const int KE_TAPS = 3;", clip);

            // One copy of the maths: the swell block and the sampling frame are the same text in both.
            Assert.Contains("float lambdaSum = wavelength", plain);
            Assert.Contains("float lambdaSum = wavelength", clip);
            Assert.Contains("oceanMip(bandCell", clip);
            Assert.Contains("oceanMip(bandCell", plain);
        }
    }
}
