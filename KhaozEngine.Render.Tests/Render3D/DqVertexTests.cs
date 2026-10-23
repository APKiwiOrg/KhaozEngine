using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class DqVertexTests
    {
        static readonly SkinningValidationContext Context = new("synthetic-vertex", null, 0);
        static readonly Matrix4x4 QuarterTurn = new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Normal_and_tangent_follow_rotation(bool blended)
        {
            var transform = QuarterTurn * Matrix4x4.CreateTranslation(3, -7, 1);
            var source = DqFixtureMeshes.Vertex(new Vector3(2, 3, 4), blended ? new Vector4(.5f, .5f, 0, 0) : Vector4.UnitX);
            source.Normal = new Vector3(3, 0, 0);
            source.Tangent = new Vector4(0, 2, 0, -1);
            RigidDualQuaternion[] palette = Prepare(blended ? Matrix4x4.CreateTranslation(3, -7, 1) : transform, transform);
            ModelVertex withTangent = DualQuaternionSkinning.Vertex(source, palette);
            float half = MathF.Sqrt(.5f);
            Vector3 expectedNormal = blended ? new Vector3(half, half, 0) : Vector3.UnitY;
            AssertPoint(blended ? new Vector3(3 - half, 5 * half - 7, 5) : new Vector3(0, -5, 5),
                withTangent.Position, 2e-5f * MathF.Sqrt(59));
            Assert.InRange(Vector3.Distance(expectedNormal, withTangent.Normal), 0f, 2e-5f);
            AssertDirection(expectedNormal, withTangent.Normal);
            AssertDirection(blended ? new Vector3(-half, half, 0) : -Vector3.UnitX, TangentDirection(withTangent));
            Assert.Equal(source.Tangent.W, withTangent.Tangent.W);
            AssertAttributes(source, withTangent);
            ModelVertex dispatched = SkinningEvaluation.Vertex(source, SkinningProfile.DualQuaternionRigid, [], palette);
            Assert.Equal(withTangent, dispatched);
            Assert.Equal(withTangent.Position, SkinningEvaluation.Position(source, SkinningProfile.DualQuaternionRigid, [], palette));
        }

        [Fact]
        public void Zero_tangent_and_handedness_survive()
        {
            var source = DqFixtureMeshes.Vertex(Vector3.UnitX, Vector4.UnitX);
            RigidDualQuaternion[] palette = Prepare(QuarterTurn, Matrix4x4.Identity);
            foreach (float handedness in new[] { -1f, 1f, -0f })
            {
                source.Tangent.W = handedness;
                ModelVertex withTangent = DualQuaternionSkinning.Vertex(source, palette);
                Assert.Equal(BitConverter.SingleToUInt32Bits(source.Tangent.W), BitConverter.SingleToUInt32Bits(withTangent.Tangent.W));
            }
            source.Tangent = new Vector4(0, 0, 0, -1);
            source.Normal = Vector3.Zero;
            ModelVertex withoutTangent = DualQuaternionSkinning.Vertex(source, palette);
            Assert.Equal(Vector4.Zero, withoutTangent.Tangent);
            Assert.Equal(0u, BitConverter.SingleToUInt32Bits(withoutTangent.Tangent.W));
            Assert.Equal(Vector3.Zero, withoutTangent.Normal);
            AssertAttributes(source, withoutTangent);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Exact_identity_and_unweighted_paths_keep_position_words_and_normalize_directions(int mode)
        {
            Vector4 weights = mode == 0 ? Vector4.UnitX : mode == 1 ? Vector4.Zero : new Vector4(MathF.BitDecrement(1e-8f), 0, 0, 0);
            var source = DqFixtureMeshes.Vertex(new Vector3(-0f, 2, -0f), weights);
            source.Normal = new Vector3(0, 3, 4);
            source.Tangent = new Vector4(2, 0, 0, -1);
            RigidDualQuaternion[] palette = Prepare(mode == 0 ? Matrix4x4.Identity : QuarterTurn, QuarterTurn);
            ModelVertex actual = DualQuaternionSkinning.Vertex(source, palette);
            Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(actual.Position.X));
            Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(actual.Position.Z));
            Assert.Equal(source.Position.Y, actual.Position.Y);
            AssertDirection(new Vector3(0, .6f, .8f), actual.Normal);
            AssertDirection(Vector3.UnitX, TangentDirection(actual));
            Assert.Equal(source.Tangent.W, actual.Tangent.W);
            AssertAttributes(source, actual);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public void Direction_fallback_is_strictly_above_1e_8(int side)
        {
            float length = side < 0 ? MathF.BitDecrement(1e-8f) : side > 0 ? MathF.BitIncrement(1e-8f) : 1e-8f;
            var source = DqFixtureMeshes.Vertex(Vector3.Zero, Vector4.UnitX);
            source.Normal = new Vector3(length, 0, 0);
            ModelVertex actual = DualQuaternionSkinning.Vertex(source, Prepare(QuarterTurn, Matrix4x4.Identity));
            if (side <= 0) Assert.Equal(source.Normal, actual.Normal);
            else AssertDirection(Vector3.UnitY, actual.Normal);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public void Source_tangent_squared_threshold_is_strictly_above_1e_12(int side)
        {
            float length = side < 0 ? MathF.BitDecrement(1e-6f) : side > 0 ? MathF.BitIncrement(1e-6f) : 1e-6f;
            if (side == 0) Assert.Equal(1e-12f, length * length);
            var source = DqFixtureMeshes.Vertex(Vector3.Zero, Vector4.UnitX);
            source.Tangent = new Vector4(length, 0, 0, -1);
            ModelVertex actual = DualQuaternionSkinning.Vertex(source, Prepare(QuarterTurn, Matrix4x4.Identity));
            if (side <= 0) Assert.Equal(Vector4.Zero, actual.Tangent);
            else
            {
                AssertDirection(Vector3.UnitY, TangentDirection(actual));
                Assert.Equal(-1f, actual.Tangent.W);
            }
        }

        [Fact]
        public void Scalar_direction_normalization_has_independent_binary32_words()
        {
            // The exact (.5,.5,.5,.5) rotation maps (x,y,z) to (z,x,y). Both lengths are exactly 3.
            var source = DqFixtureMeshes.Vertex(Vector3.Zero, Vector4.UnitX);
            source.Normal = new Vector3(1, 2, 2);
            source.Tangent = new Vector4(2, 2, 1, -1);
            ModelVertex actual = DualQuaternionSkinning.Vertex(source, [new(new Vector4(.5f), Vector4.Zero)]);
            AssertWords(actual.Normal, 0x3f2aaaab, 0x3eaaaaab, 0x3f2aaaab);
            AssertWords(TangentDirection(actual), 0x3eaaaaab, 0x3f2aaaab, 0x3f2aaaab);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Nonfinite_direction_arithmetic_refuses(bool tangent)
        {
            var source = DqFixtureMeshes.Vertex(Vector3.Zero, Vector4.UnitX);
            if (tangent) source.Tangent.X = float.MaxValue;
            else source.Normal.X = float.MaxValue;
            RigidDualQuaternion[] palette = Prepare(QuarterTurn, Matrix4x4.Identity);
            Assert.Throws<InvalidOperationException>(() => DualQuaternionSkinning.Vertex(source, palette));
        }

        [Fact]
        public void Linear_profile_keeps_existing_scaled_raw_weight_results()
        {
            Matrix4x4[] lbs = [Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(1, -2, 3), QuarterTurn];
            var source = DqFixtureMeshes.Vertex(new Vector3(2, 3, 4), new Vector4(2, 1, 0, 0));
            ModelVertex expected = SkinningMath.SkinVertex(source, lbs);
            Assert.Equal(expected, SkinningEvaluation.Vertex(source, SkinningProfile.LinearBlend, lbs, []));
            Assert.Equal(Vector3.Transform(source.Position, SkinningMath.BlendSkinMatrix(lbs, source.BoneIndices, source.BoneWeights)),
                SkinningEvaluation.Position(source, SkinningProfile.LinearBlend, lbs, []));
        }

        [Fact]
        public void Opposite_twist_constant_weight_tube_keeps_radius_while_LBS_shrinks()
        {
            DqFixtureMeshes.Mesh mesh = DqFixtureMeshes.ConstantWeightTube();
            Matrix4x4[] lbs = [Matrix4x4.CreateRotationZ(MathF.PI / 3), Matrix4x4.CreateRotationZ(-MathF.PI / 3)];
            RigidDualQuaternion[] dq = Prepare(lbs);
            float bound = PositionBound(mesh, lbs);
            foreach (SkinnedVertex source in mesh.Vertices)
            {
                ModelVertex actual = SkinningEvaluation.Vertex(source, SkinningProfile.DualQuaternionRigid, lbs, dq);
                AssertPoint(source.Position, actual.Position, bound);
                Assert.InRange(MathF.Abs(new Vector2(actual.Position.X, actual.Position.Y).Length() - 1f), 0, bound);
                ModelVertex linear = SkinningEvaluation.Vertex(source, SkinningProfile.LinearBlend, lbs, dq);
                Assert.InRange(new Vector2(linear.Position.X, linear.Position.Y).Length(), .49999f, .50001f);
            }
        }

        [Fact]
        public void Constant_weight_patch_is_rigid()
        {
            DqFixtureMeshes.Mesh mesh = DqFixtureMeshes.ClosedPatch();
            Matrix4x4[] lbs = [Matrix4x4.CreateTranslation(3, -2, 1), QuarterTurn * Matrix4x4.CreateTranslation(3, -2, 1)];
            Vector3[] actual = Evaluate(mesh, Prepare(lbs));
            float bound = PositionBound(mesh, lbs), half = MathF.Sqrt(.5f);
            for (int i = 0; i < actual.Length; i++)
            {
                Vector3 p = mesh.Vertices[i].Position;
                AssertPoint(new Vector3(half * (p.X - p.Y) + 3, half * (p.X + p.Y) - 2, p.Z + 1), actual[i], bound);
            }
            // Each vertex error has norm <= sqrt(3)*bound, so each unit tetrahedron edge error is <= eta.
            // Determinant multilinearity bounds volume error by (3*eta + 3*eta^2 + eta^3)/6.
            double eta = 2 * Math.Sqrt(3) * bound;
            double volumeBound = (3 * eta + 3 * eta * eta + eta * eta * eta) / 6;
            Assert.InRange(Math.Abs(Volume(actual, mesh.Indices) - 1d / 6), 0, volumeBound);
        }

        [Fact]
        public void Varying_weights_can_bulge()
        {
            DqFixtureMeshes.Mesh mesh = DqFixtureMeshes.VaryingWeightBend();
            Matrix4x4[] lbs = [Matrix4x4.Identity, QuarterTurn * Matrix4x4.CreateTranslation(0, 2, 0)];
            Vector3[] actual = Evaluate(mesh, Prepare(lbs));
            float half = MathF.Sqrt(.5f), bound = PositionBound(mesh, lbs);
            // Halfway rotation about (-1,1,0) takes (0,1,0) to (-1+sqrt(.5),1+sqrt(.5),0).
            AssertPoint(new Vector3(-1 + half, 1 + half, 0), actual[2], bound);
            double volume = Volume(actual, mesh.Indices);
            Assert.InRange(Math.Abs(volume - (1d + Math.Sqrt(.5)) / 6), 0, bound);
            Assert.True(volume > 1.7 / 6, "Varying weights have no constant-volume guarantee.");
        }

        [Fact]
        public void Ambiguous_four_influences_retain_stored_order()
        {
            SkinnedVertex source = DqFixtureMeshes.AmbiguousFourInfluenceMesh().Vertices[1];
            RigidDualQuaternion[] dq = [new(Vector4.UnitX, Vector4.Zero), new(Vector4.UnitY, Vector4.Zero),
                new(new Vector4(.5f, -.5f, .5f, .5f), Vector4.Zero), new(Vector4.UnitZ, Vector4.Zero)];
            AssertPoint(new Vector3(0, .6f, .8f), SkinningEvaluation.Position(source, SkinningProfile.DualQuaternionRigid, [], dq), 2e-5f);
            source.BoneIndices = new Vector4(0, 2, 1, 3);
            AssertPoint(new Vector3(-2f / 7, -3f / 7, 6f / 7), SkinningEvaluation.Position(source, SkinningProfile.DualQuaternionRigid, [], dq), 2e-5f);
        }

        static RigidDualQuaternion[] Prepare(params Matrix4x4[] matrices)
        {
            var result = new RigidDualQuaternion[matrices.Length];
            for (int i = 0; i < result.Length; i++) result[i] = DualQuaternionPalette.FromComposed(matrices[i], Context, i);
            return result;
        }

        static Vector3[] Evaluate(DqFixtureMeshes.Mesh mesh, RigidDualQuaternion[] palette)
        {
            var positions = new Vector3[mesh.Vertices.Length];
            for (int i = 0; i < positions.Length; i++) positions[i] = DualQuaternionSkinning.Vertex(mesh.Vertices[i], palette).Position;
            return positions;
        }

        static float PositionBound(DqFixtureMeshes.Mesh mesh, Matrix4x4[] matrices)
        {
            float scale = 1;
            foreach (SkinnedVertex v in mesh.Vertices) scale = MathF.Max(scale, v.Position.Length());
            foreach (Matrix4x4 m in matrices) scale = MathF.Max(scale, new Vector3(m.M41, m.M42, m.M43).Length());
            return 2e-5f * scale;
        }

        static double Volume(Vector3[] positions, int[] indices)
        {
            double sum = 0;
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = positions[indices[i]], b = positions[indices[i + 1]], c = positions[indices[i + 2]];
                sum += a.X * ((double)b.Y * c.Z - (double)b.Z * c.Y)
                    + a.Y * ((double)b.Z * c.X - (double)b.X * c.Z)
                    + a.Z * ((double)b.X * c.Y - (double)b.Y * c.X);
            }
            return sum / 6;
        }

        static Vector3 TangentDirection(ModelVertex vertex) => new(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);

        static void AssertPoint(Vector3 expected, Vector3 actual, float bound)
        {
            Assert.InRange(MathF.Abs(expected.X - actual.X), 0, bound);
            Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, bound);
            Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, bound);
        }

        static void AssertDirection(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(Vector3.Distance(expected, actual), 0, 2e-5f);
            Assert.InRange(Math.Atan2(Vector3.Cross(expected, actual).Length(), Vector3.Dot(expected, actual)), 0, 1e-4);
        }

        static void AssertAttributes(SkinnedVertex source, ModelVertex actual)
        {
            Assert.Equal(source.Color, actual.Color);
            Assert.Equal(source.Uv, actual.Uv);
        }

        static void AssertWords(Vector3 actual, uint x, uint y, uint z)
        {
            Assert.Equal(x, BitConverter.SingleToUInt32Bits(actual.X));
            Assert.Equal(y, BitConverter.SingleToUInt32Bits(actual.Y));
            Assert.Equal(z, BitConverter.SingleToUInt32Bits(actual.Z));
        }
    }
}
