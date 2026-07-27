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
            in WaterPlane plane, float fx, float fz, int levels, float cell = Cell, int ring = Ring)
        {
            var verts = new WaterClipmapVertex[WaterClipmap.VertexCount(levels, ring)];
            var indices = new uint[WaterClipmap.IndexCount(levels, ring)];
            Vector2 focus = WaterClipmap.ClampFocus(plane, fx, fz);
            int vc = WaterClipmap.Build(plane, focus.X, focus.Y, cell, ring, levels, verts, indices, out int ic);
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
                        if (v.Stitch == Vector2.Zero) continue;

                        stitched++;
                        Assert.True(boundary, "only ring-boundary vertices may stitch");
                        Assert.True(l + 1 < levels, "the outermost ring has nothing to stitch to");
                        // The stitch names its two coarse neighbours exactly, and the vertex is their midpoint.
                        Assert.Equal(c, MathF.Abs(v.Stitch.X) + MathF.Abs(v.Stitch.Y), 4);
                        foreach (float s in new[] { -1f, 1f })
                        {
                            var p = new Vector2(v.Position.X + s * v.Stitch.X, v.Position.Z + s * v.Stitch.Y);
                            Assert.Contains(((long)MathF.Round(p.X * 1024f), (long)MathF.Round(p.Y * 1024f)),
                                coarsePositions);
                        }
                    }
                }
            }
            // Four sides, every other vertex, on every level but the outermost.
            Assert.Equal((levels - 1) * 4 * (Ring / 2), stitched);
        }

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
            Assert.Equal(3 * 4 + 2 * 4 + 4, (int)WaterRenderer.ClipVertexBytes);
        }

        [Fact]
        public void TheTwoVertexSourcesDifferOnlyInTheirGridInputs()
        {
            string plain = ShaderSources.WaterVert, clip = ShaderSources.WaterClipmapVert;
            // The camera-focused source pins the band limit off and the tap count at one, both as compile-time
            // constants, so its FFT sampling collapses to the literal LOD-0 fetch it always was.
            Assert.Contains("const float bandCell = 0.0;", plain);
            Assert.Contains("const int taps = 1;", plain);
            Assert.DoesNotContain("in vec2 Stitch;", plain);
            Assert.DoesNotContain("in float Cell;", plain);

            Assert.Contains("in vec2 Stitch;", clip);
            Assert.Contains("in float Cell;", clip);
            Assert.Contains("float bandCell = Cell;", clip);

            // One copy of the maths: the swell block and the sampling frame are the same text in both.
            Assert.Contains("float lambdaSum = wavelength", plain);
            Assert.Contains("float lambdaSum = wavelength", clip);
            Assert.Contains("oceanMip(bandCell", clip);
            Assert.Contains("oceanMip(bandCell", plain);
        }
    }
}
