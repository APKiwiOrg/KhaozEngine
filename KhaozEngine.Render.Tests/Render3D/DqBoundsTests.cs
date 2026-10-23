using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class DqBoundsTests
    {
        static readonly SkinningValidationContext Context = new("synthetic-bounds", null, 0);
        static readonly RigidDualQuaternion Identity = new(new Vector4(0, 0, 0, 1), Vector4.Zero);
        static readonly Vector4 Indices = new(0, 1, 2, 3);

        [Fact]
        public void DQ_escapes_the_LBS_convex_union_but_is_enclosed()
        {
            var positive = new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            var negative = new Matrix4x4(0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            RigidDualQuaternion[] palette = [Prepare(positive), Prepare(negative)];
            SkinnedVertex[] vertices =
            [
                Vertex(new Vector3(1, 0, 0), new Vector4(.5f, .5f, 0, 0), new Vector4(0, 1, 0, 1)),
                Vertex(new Vector3(2, 0, 0), new Vector4(.5f, .5f, 0, 0), new Vector4(0, 1, 0, 1)),
                Vertex(new Vector3(1, 0, 2), new Vector4(.5f, .5f, 0, 0), new Vector4(0, 1, 0, 1)),
            ];
            var sourceBounds = new DqBoundsSource(vertices, palette.Length);
            Assert.True(DualQuaternionBounds.TryBoundModel(in sourceBounds, palette, out MeshBounds bounds));
            foreach (SkinnedVertex vertex in vertices)
            {
                Assert.Equal(0, Vector3.Transform(vertex.Position, positive).X);
                Assert.Equal(0, Vector3.Transform(vertex.Position, negative).X);
                double[] independentlyPosed = IndependentPosition(vertex, palette);
                Assert.True(independentlyPosed[0] > .99);
                AssertEnclosed(bounds, independentlyPosed);
                AssertEnclosed(bounds, DualQuaternionSkinning.Position(vertex, palette));
            }
        }

        [Fact]
        public void Rounded_norm_has_a_positive_outward_lower_bound()
        {
            RigidDualQuaternion[] orthogonal =
            [
                Prepare(Matrix4x4.CreateRotationX(MathF.PI)),
                Prepare(Matrix4x4.CreateRotationY(MathF.PI)),
                Prepare(Matrix4x4.CreateRotationZ(MathF.PI)),
                Identity,
            ];
            RigidDualQuaternion[] exactTies =
            [
                new(Vector4.UnitX, Vector4.Zero), new(Vector4.UnitY, Vector4.Zero),
                new(Vector4.UnitZ, Vector4.Zero), Identity,
            ];
            RigidDualQuaternion[] nearIdentity =
            [
                Identity, Prepare(Matrix4x4.CreateRotationX(1f / 4096)),
                Prepare(Matrix4x4.CreateRotationY(-1f / 8192)), Prepare(Matrix4x4.CreateTranslation(2, -3, 4)),
            ];
            RigidDualQuaternion[] opposed =
            [
                Prepare(Matrix4x4.CreateRotationX(2.8f)), Prepare(Matrix4x4.CreateRotationX(-2.8f)), Identity, Identity,
            ];
            RigidDualQuaternion[] roundedTie =
            [
                new(new Vector4(.5f), Vector4.Zero),
                new(new Vector4(MathF.BitDecrement(.5f), -.5f, -.5f, .5f), Vector4.Zero), Identity, Identity,
            ];
            // This decision dot rounds to zero although its exact value is negative. The envelope
            // must cover the retained sign too, rather than using an exact-hemisphere assumption.
            Vector4 prefix = new(.25f), q = roundedTie[1].Real;
            float decision = (((q.X * prefix.X) + (q.Y * prefix.Y)) + (q.Z * prefix.Z)) + (q.W * prefix.W);
            Assert.Equal(0, decision);
            Assert.True((((double)q.X * prefix.X + (double)q.Y * prefix.Y) + (double)q.Z * prefix.Z) + (double)q.W * prefix.W < 0);
            const float tolerance = 1f / (1 << 20);
            Vector4[] weights =
            [
                new(.25f), new((1f - tolerance) / 4), new((1f + tolerance) / 4),
                new(1f - tolerance, 0, 0, 0), new(0, 0, 0, 1f + tolerance),
                new(.1f, .2f, .3f, .4f), new(float.Epsilon, .25f, .25f, .5f), new(.5f, .5f, 0, 0),
            ];
            foreach (RigidDualQuaternion[] palette in new[] { orthogonal, exactTies, nearIdentity, opposed, roundedTie })
            {
                foreach (Vector4 weight in weights)
                {
                    var vertex = Vertex(new Vector3(2, -3, 5), weight, Indices);
                    var source = new DqBoundsSource([vertex], palette.Length);
                    DualQuaternionSkinning.Blend(Indices, weight, palette, out Vector4 real, out _);
                    float n = (((real.X * real.X) + (real.Y * real.Y)) + (real.Z * real.Z)) + (real.W * real.W);
                    double lower = Math.BitDecrement((double)real.X * real.X);
                    lower = Math.BitDecrement(lower + Math.BitDecrement((double)real.Y * real.Y));
                    lower = Math.BitDecrement(lower + Math.BitDecrement((double)real.Z * real.Z));
                    lower = Math.BitDecrement(lower + Math.BitDecrement((double)real.W * real.W));
                    Assert.True(lower >= .125, $"Outward exact norm lower {lower:R}");
                    Assert.True(n >= .125f, $"Rounded graph norm {n:R}");
                    Assert.True(DualQuaternionBounds.TryBoundModel(in source, palette, out MeshBounds bounds));
                    AssertEnclosed(bounds, IndependentPosition(vertex, palette));
                    AssertEnclosed(bounds, DualQuaternionSkinning.Position(vertex, palette));
                }
            }
        }

        [Fact]
        public void Exact_identity_near_identity_and_unweighted_positions_are_enclosed()
        {
            RigidDualQuaternion near = Prepare(Matrix4x4.CreateRotationZ(1f / (1 << 20)));
            RigidDualQuaternion[] palette = [Identity, near];
            foreach (Vector4 weight in new[] { Vector4.Zero, new Vector4(MathF.BitDecrement(1e-8f), 0, 0, 0), Vector4.UnitX, Vector4.UnitY })
            {
                var vertex = Vertex(new Vector3(-0f, 1, float.Epsilon), weight, new Vector4(0, 1, 0, 1));
                var source = new DqBoundsSource([vertex], 2);
                Assert.True(DualQuaternionBounds.TryBoundModel(in source, palette, out MeshBounds bounds));
                AssertEnclosed(bounds, IndependentPosition(vertex, palette));
                AssertEnclosed(bounds, DualQuaternionSkinning.Position(vertex, palette));
            }
        }

        [Fact]
        public void Huge_finite_translation_and_prepared_Gram_boundary_are_enclosed()
        {
            Matrix4x4 matrix = Matrix4x4.CreateTranslation(MathF.ScaleB(1, 55), -MathF.ScaleB(1, 54), MathF.ScaleB(1, 53));
            matrix.M11 = 1f + 1f / (1 << 17);
            Assert.Equal(1f / (1 << 16), (matrix.M11 * matrix.M11) - 1f);
            RigidDualQuaternion[] palette = [Prepare(matrix)];
            var vertex = Vertex(new Vector3(7, -9, 11), Vector4.UnitX, Vector4.Zero);
            var source = new DqBoundsSource([vertex], 1);
            Assert.True(DualQuaternionBounds.TryBoundModel(in source, palette, out MeshBounds bounds));
            AssertEnclosed(bounds, IndependentPosition(vertex, palette));
            AssertEnclosed(bounds, DualQuaternionSkinning.Position(vertex, palette));
        }

        [Fact]
        public void Nonfinite_or_unproved_envelope_refuses()
        {
            var vertex = Vertex(Vector3.UnitX, Vector4.UnitX, Vector4.Zero);
            var source = new DqBoundsSource([vertex], 1);
            Assert.False(DualQuaternionBounds.TryBoundModel(default, [Identity], out _));
            Assert.False(DualQuaternionBounds.TryBoundModel(in source, [], out _));
            RigidDualQuaternion[] invalid =
            [
                new(Vector4.Zero, Vector4.Zero), new(new Vector4(0, 0, 0, 2), Vector4.Zero),
                new(new Vector4(0, 0, 0, 1e-12f), Vector4.Zero),
                new(new Vector4(float.NaN, 0, 0, 1), Vector4.Zero),
                new(new Vector4(0, 0, 0, 1), new Vector4(float.PositiveInfinity, 0, 0, 0)),
                new(new Vector4(0, 0, 0, 1), new Vector4(float.MaxValue, 0, 0, 0)),
            ];
            foreach (RigidDualQuaternion bone in invalid)
                Assert.False(DualQuaternionBounds.TryBoundModel(in source, [bone], out _));
            var huge = new DqBoundsSource([Vertex(new Vector3(float.MaxValue), Vector4.UnitX, Vector4.Zero)], 1);
            Assert.False(DualQuaternionBounds.TryBoundModel(in huge, [Identity], out _));
            RigidDualQuaternion[] finiteHuge = [Prepare(Matrix4x4.CreateTranslation(float.MaxValue / 4, 0, 0))];
            Assert.False(DualQuaternionBounds.TryBoundModel(in source, finiteHuge, out _));
        }

        [Fact]
        public void Source_metadata_validates_the_complete_source_domain_without_retaining_vertices()
        {
            SkinnedVertex[] vertices = [Vertex(new Vector3(3, 4, 0), Vector4.UnitX, Vector4.Zero)];
            var source = new DqBoundsSource(vertices, 1);
            vertices[0].Position = new Vector3(1000);
            Assert.True(DualQuaternionBounds.TryBoundModel(in source, [Identity], out MeshBounds bounds));
            AssertEnclosed(bounds, new Vector3(3, 4, 0));
            Assert.True(bounds.Max.X < 100);
            vertices[0].BoneIndices.W = 1;
            Assert.Throws<ArgumentException>(() => new DqBoundsSource(vertices, 1));
            vertices[0] = Vertex(Vector3.UnitX, new Vector4(.5f, 0, 0, 0), Vector4.Zero);
            Assert.Throws<ArgumentException>(() => new DqBoundsSource(vertices, 1));
            vertices[0] = Vertex(new Vector3(float.NaN, 0, 0), Vector4.UnitX, Vector4.Zero);
            Assert.Throws<ArgumentException>(() => new DqBoundsSource(vertices, 1));
        }

        [Fact]
        public void World_corner_intervals_enclose_shear_and_general_affine_placement()
        {
            var model = new MeshBounds(new Vector3(-1), new Vector3(1));
            Matrix4x4[] placements =
            [
                new(1, 1, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
                new(-2, .75f, .125f, 0, 3, -4, .5f, 0, .25f, 1, 0, 0, 100, -200, 300, 1),
                Matrix4x4.CreateScale(0, -2, 3) * Matrix4x4.CreateTranslation(7, 11, -13),
            ];
            model.WorldSphere(placements[0], out Vector3 oldCenter, out float oldRadius);
            Assert.True(Vector3.Distance(new Vector3(2, 2, 1), oldCenter) > oldRadius);
            foreach (Matrix4x4 world in placements)
            {
                Assert.True(DualQuaternionBounds.TryBoundWorld(in model, in world, out MeshBounds bounds));
                for (int corner = 0; corner < 8; corner++)
                {
                    var p = new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
                    AssertEnclosed(bounds, ExactWorld(p, world));
                    AssertEnclosed(bounds, Vector3.Transform(p, world));
                }
                AssertEnclosed(bounds, ExactWorld(new Vector3(.25f, -.75f, .125f), world));
            }
        }

        [Fact]
        public void World_conversion_rounds_outward_and_refuses_nonfinite_or_projective_arithmetic()
        {
            float next = MathF.BitIncrement(1);
            var model = new MeshBounds(new Vector3(next, 0, 0), new Vector3(next, 0, 0));
            Matrix4x4 world = Matrix4x4.CreateScale(next, 1, 1);
            Assert.True(DualQuaternionBounds.TryBoundWorld(in model, in world, out MeshBounds result));
            AssertEnclosed(result, new[] { (double)next * next, 0, 0 });
            Matrix4x4 bad = world;
            bad.M14 = float.Epsilon;
            Assert.False(DualQuaternionBounds.TryBoundWorld(in model, in bad, out _));
            bad = world;
            bad.M41 = float.PositiveInfinity;
            Assert.False(DualQuaternionBounds.TryBoundWorld(in model, in bad, out _));
            bad = world;
            bad.M22 = float.NaN;
            Assert.False(DualQuaternionBounds.TryBoundWorld(in model, in bad, out _));
            var inverted = new MeshBounds(Vector3.One, -Vector3.One);
            Assert.False(DualQuaternionBounds.TryBoundWorld(in inverted, in world, out _));
            var huge = new MeshBounds(new Vector3(float.MaxValue), new Vector3(float.MaxValue));
            bad = Matrix4x4.Identity;
            bad.M11 = 2;
            bad.M21 = -2;
            Assert.False(DualQuaternionBounds.TryBoundWorld(in huge, in bad, out _));
        }

        static RigidDualQuaternion Prepare(Matrix4x4 matrix) => DualQuaternionPalette.FromComposed(matrix, Context, 0);
        static SkinnedVertex Vertex(Vector3 p, Vector4 weights, Vector4 indices) => new()
        { Position = p, Normal = Vector3.UnitY, Color = Vector4.One, BoneWeights = weights, BoneIndices = indices };

        static void AssertEnclosed(MeshBounds bounds, Vector3 p) => AssertEnclosed(bounds, new[] { (double)p.X, p.Y, p.Z });
        static void AssertEnclosed(MeshBounds bounds, double[] p)
        {
            double squared = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.InRange(p[axis], bounds.Min[axis], bounds.Max[axis]);
                double delta = p[axis] - bounds.Center[axis];
                squared += delta * delta;
            }
            Assert.True(double.IsFinite(squared));
            Assert.True(squared <= (double)bounds.Radius * bounds.Radius, $"Sphere misses {string.Join(", ", p)}");
        }

        static double[] ExactWorld(Vector3 p, Matrix4x4 m) =>
        [
            (((double)p.X * m.M11) + ((double)p.Y * m.M21)) + ((double)p.Z * m.M31) + m.M41,
            (((double)p.X * m.M12) + ((double)p.Y * m.M22)) + ((double)p.Z * m.M32) + m.M42,
            (((double)p.X * m.M13) + ((double)p.Y * m.M23)) + ((double)p.Z * m.M33) + m.M43,
        ];

        // Independent binary64 Hamilton oracle, without the production blend or rotation-matrix helpers.
        // The binary32 output is checked separately. The proof covers either hemisphere sign at a tie.
        static double[] IndependentPosition(SkinnedVertex vertex, RigidDualQuaternion[] palette)
        {
            Vector4 weights = vertex.BoneWeights;
            if (((weights.X + weights.Y) + weights.Z) + weights.W < 1e-8f)
                return [vertex.Position.X, vertex.Position.Y, vertex.Position.Z];
            double[] r = new double[4], d = new double[4];
            for (int slot = 0; slot < 4; slot++)
            {
                RigidDualQuaternion bone = palette[(int)vertex.BoneIndices[slot]];
                double dot = 0;
                for (int c = 0; c < 4; c++) dot += bone.Real[c] * r[c];
                double signedWeight = dot < 0 ? -weights[slot] : weights[slot];
                for (int c = 0; c < 4; c++)
                {
                    r[c] += signedWeight * bone.Real[c];
                    d[c] += signedWeight * bone.Dual[c];
                }
            }
            double n = 0;
            for (int c = 0; c < 4; c++) n += r[c] * r[c];
            double[] conjugate = [-r[0], -r[1], -r[2], r[3]];
            double[] rotated = Hamilton(Hamilton(r, [vertex.Position.X, vertex.Position.Y, vertex.Position.Z, 0]), conjugate);
            double[] translated = Hamilton(d, conjugate);
            return [(rotated[0] + 2 * translated[0]) / n, (rotated[1] + 2 * translated[1]) / n, (rotated[2] + 2 * translated[2]) / n];
        }

        static double[] Hamilton(double[] a, double[] b) =>
        [
            a[3] * b[0] + a[0] * b[3] + a[1] * b[2] - a[2] * b[1],
            a[3] * b[1] + a[1] * b[3] + a[2] * b[0] - a[0] * b[2],
            a[3] * b[2] + a[2] * b[3] + a[0] * b[1] - a[1] * b[0],
            a[3] * b[3] - a[0] * b[0] - a[1] * b[1] - a[2] * b[2],
        ];
    }
}
