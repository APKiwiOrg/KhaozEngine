using System;
using System.Numerics;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests
{
    /// <summary>Headless tests for MeshContainment: the generalized winding number over closed, open, inside-out
    /// and overlapping shells, and the nearest-triangle distance.</summary>
    public class MeshContainmentTests
    {
        // The [-1,1] cube as 12 outward-wound triangles, optionally without its +Y face and shifted along X.
        static (Vector3[] Positions, int[] Indices) Cube(float offsetX = 0f, bool openTop = false)
        {
            var p = new Vector3[8];
            for (int i = 0; i < 8; i++)
                p[i] = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f)
                    + new Vector3(offsetX, 0f, 0f);
            int[] bottom = [0, 1, 5, 0, 5, 4];
            int[] top = [2, 6, 7, 2, 7, 3];
            int[] sides =
            [
                0, 4, 6, 0, 6, 2,   // -X
                1, 3, 7, 1, 7, 5,   // +X
                0, 2, 3, 0, 3, 1,   // -Z
                4, 5, 7, 4, 7, 6,   // +Z
            ];
            int[] indices = openTop ? [.. bottom, .. sides] : [.. bottom, .. top, .. sides];
            return (p, indices);
        }

        static int[] Flipped(int[] indices)
        {
            var flipped = (int[])indices.Clone();
            for (int i = 0; i < flipped.Length; i += 3) (flipped[i + 1], flipped[i + 2]) = (flipped[i + 2], flipped[i + 1]);
            return flipped;
        }

        [Fact]
        public void ACubeEnclosesItsCentre()
        {
            (Vector3[] p, int[] i) = Cube();
            Assert.Equal(1f, MeshContainment.WindingNumber(p, i, Vector3.Zero), 4);
        }

        [Fact]
        public void APointOutsideACubeIsNotEnclosed()
        {
            (Vector3[] p, int[] i) = Cube();
            Assert.Equal(0f, MeshContainment.WindingNumber(p, i, new Vector3(3f, 0f, 0f)), 4);
        }

        [Fact]
        public void AnOpenShellStillEnclosesItsCentre()
        {
            (Vector3[] p, int[] i) = Cube(openTop: true);
            Assert.True(MathF.Abs(MeshContainment.WindingNumber(p, i, new Vector3(0f, -0.5f, 0f))) >= 0.5f);
        }

        [Fact]
        public void AnInsideOutShellStillContains()
        {
            (Vector3[] p, int[] i) = Cube();
            Assert.Equal(-1f, MeshContainment.WindingNumber(p, Flipped(i), Vector3.Zero), 4);
        }

        [Fact]
        public void OverlappingShellsEncloseTheirOverlap()
        {
            (Vector3[] a, int[] ai) = Cube();
            (Vector3[] b, int[] bi) = Cube(offsetX: 1f);
            Vector3[] positions = [.. a, .. b];
            int[] indices = [.. ai, .. Array.ConvertAll(bi, x => x + a.Length)];
            Assert.True(MathF.Abs(MeshContainment.WindingNumber(positions, indices, new Vector3(0.5f, 0f, 0f))) >= 0.5f);
        }

        [Fact]
        public void NearestDistanceMeasuresToTheClosestFace()
        {
            (Vector3[] p, int[] i) = Cube();
            Assert.Equal(0.01f, MeshContainment.NearestDistanceSquared(p, i, new Vector3(1.1f, 0f, 0f)), 4);
            Assert.Equal(2f, MeshContainment.NearestDistanceSquared(p, i, new Vector3(2f, 2f, 0f)), 4);
        }
    }
}
