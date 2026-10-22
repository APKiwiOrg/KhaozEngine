using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class DqProfileValidationTests
    {
        static readonly SkinningValidationContext Context = new("public-dq-fixture", "bind-check", 17);

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Old_constructors_select_LBS(bool wideIndices)
        {
            var vertices = new[] { ValidVertex() };
            var bones = new[] { Matrix4x4.Identity };
            var mesh = wideIndices
                ? new SkinnedGltfMesh(vertices, new uint[] { 0 }, bones, bones)
                : new SkinnedGltfMesh(vertices, new ushort[] { 0 }, bones, bones);

            Assert.Equal(SkinningMethod.LinearBlend, mesh.Profile.Method);
            Assert.Equal(1, mesh.Profile.AlgorithmVersion);
            Assert.Same(SkinningProfile.LinearBlend, mesh.Profile);
        }

        [Fact]
        public void Profile_is_immutable_and_value_equal()
        {
            Assert.Equal(0, (int)SkinningMethod.LinearBlend);
            Assert.Equal(1, (int)SkinningMethod.DualQuaternionRigid);
            Assert.True(typeof(SkinningProfile).IsSealed);
            Assert.Empty(typeof(SkinningProfile).GetConstructors());
            Assert.All(typeof(SkinningProfile).GetProperties(), property => Assert.Null(property.SetMethod));
            Assert.Null(typeof(SkinnedGltfMesh).GetProperty(nameof(SkinnedGltfMesh.Profile))!.SetMethod);

            foreach (var profile in new[] { SkinningProfile.LinearBlend, SkinningProfile.DualQuaternionRigid })
            {
                SkinningProfile copy = profile with { };
                Assert.NotSame(profile, copy);
                Assert.Equal(profile, copy);
                Assert.True(profile == copy);
                Assert.Equal(profile.GetHashCode(), copy.GetHashCode());
                Assert.Equal(1, profile.AlgorithmVersion);
            }
            Assert.Equal(SkinningMethod.DualQuaternionRigid, SkinningProfile.DualQuaternionRigid.Method);
            Assert.NotEqual(SkinningProfile.LinearBlend, SkinningProfile.DualQuaternionRigid);
        }

        public static TheoryData<string, int, float, bool> InvalidSources()
        {
            var rows = new TheoryData<string, int, float, bool>();
            (string Field, int Components)[] fields =
            {
                ("Position", 3), ("Normal", 3), ("Color", 4), ("Uv", 2),
                ("BoneIndices", 4), ("BoneWeights", 4), ("Tangent", 4),
            };
            foreach (bool wide in new[] { false, true })
            {
                foreach (var (field, components) in fields)
                    for (int component = 0; component < components; component++)
                        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                            rows.Add(field, component, value, wide);
                for (int component = 0; component < 4; component++)
                {
                    foreach (float value in new[] { -1f, -0.5f, 0.5f, 4f, float.MaxValue })
                        rows.Add("BoneIndices", component, value, wide);
                    rows.Add("BoneWeights", component, -0.25f, wide);
                    rows.Add("BoneWeights", component, BitConverter.Int32BitsToSingle(unchecked((int)0x80000001)), wide);
                }
            }
            return rows;
        }

        [Theory]
        [MemberData(nameof(InvalidSources))]
        public void DQ_rejects_invalid_source(string field, int component, float value, bool wideIndices)
        {
            SkinnedVertex invalid = ValidVertex();
            // Invalid unused slots must fail even on the entirely unweighted identity path.
            if (field == "BoneIndices") invalid.BoneWeights = Vector4.Zero;
            var vertices = new[] { ValidVertex(), WithComponent(invalid, field, component, value) };
            byte[] snapshot = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                SkinningValidation.ValidateSource(vertices, 4, SkinningProfile.DualQuaternionRigid, Context));
            Assert.Contains(Context.Asset, error.Message);
            Assert.Contains(Context.Clip!, error.Message);
            Assert.Contains("17", error.Message);
            Assert.Contains("DualQuaternionRigid", error.Message);
            Assert.Contains("version 1", error.Message);
            Assert.Contains("vertex 1", error.Message);
            Assert.Contains(field, error.Message);
            Assert.Throws<ArgumentException>(() => CreateMesh(vertices, 4, wideIndices, SkinningProfile.DualQuaternionRigid));
            Assert.Equal(snapshot, MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray());
        }

        [Theory]
        [InlineData(false, -1)]
        [InlineData(true, -1)]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 129)]
        [InlineData(true, 129)]
        public void DQ_rejects_invalid_joint_count(bool wideIndices, int boneCount)
        {
            var vertices = new[] { ValidVertex() };
            Assert.ThrowsAny<ArgumentException>(() =>
                SkinningValidation.ValidateSource(vertices, boneCount, SkinningProfile.DualQuaternionRigid, Context));
            if (boneCount >= 0)
                Assert.ThrowsAny<ArgumentException>(() => CreateMesh(vertices, boneCount, wideIndices, SkinningProfile.DualQuaternionRigid));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DQ_accepts_128_joints_duplicate_indices_and_zero_weights_without_repair(bool wideIndices)
        {
            SkinnedVertex duplicate = ValidVertex();
            duplicate.BoneIndices = new Vector4(127, 127, 127, 127);
            duplicate.BoneWeights = new Vector4(0.125f, 0.25f, 0.5f, 0.125f);
            SkinnedVertex unweighted = duplicate;
            unweighted.BoneWeights = new Vector4(0f, -0f, 0f, -0f);
            var vertices = new[] { duplicate, unweighted };
            byte[] snapshot = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();

            SkinningValidation.ValidateSource(vertices, 128, SkinningProfile.DualQuaternionRigid, Context);
            SkinnedGltfMesh mesh = CreateMesh(vertices, 128, wideIndices, SkinningProfile.DualQuaternionRigid);

            Assert.Same(SkinningProfile.DualQuaternionRigid, mesh.Profile);
            Assert.Equal(128, mesh.BoneCount);
            Assert.Equal(snapshot, MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()).ToArray());
        }

        [Fact]
        public void DQ_profile_preserves_a_genuinely_32_bit_index_buffer()
        {
            var vertices = new SkinnedVertex[65537];
            vertices[65536] = ValidVertex();
            var bones = new[] { Matrix4x4.Identity };
            var mesh = new SkinnedGltfMesh(vertices, new uint[] { 0, 1, 65536 }, bones, bones,
                null, SkinningProfile.DualQuaternionRigid);

            Assert.Equal(KhaozEngine.Gpu.GpuIndexFormat.UInt32, mesh.IndexFormat);
            Assert.Equal(new uint[] { 0, 1, 65536 }, mesh.Indices32);
            Assert.Throws<InvalidOperationException>(() => mesh.Indices);
            Assert.Same(SkinningProfile.DualQuaternionRigid, mesh.Profile);
        }

        public static TheoryData<int, bool, bool> WeightTotalBoundaries()
        {
            var rows = new TheoryData<int, bool, bool>();
            (int Word, bool Accepted)[] totals =
            {
                (0x00000000, true), (0x00000001, true),
                (0x322bcc76, true), (0x322bcc77, false), (0x322bcc78, false), // 1e-8 and its neighbours.
                (0x3f7fffef, false), (0x3f7ffff0, true), (0x3f7ffff1, true), // 1 - 2^-20.
                (0x3f800000, true),
                (0x3f800007, true), (0x3f800008, true), (0x3f800009, false), // 1 + 2^-20.
                (0x3f000000, false), (0x40000000, false),
            };
            foreach (bool wide in new[] { false, true })
                foreach (var (word, accepted) in totals)
                    rows.Add(word, accepted, wide);
            return rows;
        }

        [Theory]
        [MemberData(nameof(WeightTotalBoundaries))]
        public void DQ_weight_total_boundaries_are_pinned_by_adjacent_float_words(int word, bool accepted, bool wideIndices)
        {
            SkinnedVertex vertex = ValidVertex();
            vertex.BoneWeights = new Vector4(BitConverter.Int32BitsToSingle(word), 0f, 0f, 0f);
            var vertices = new[] { vertex };
            if (accepted)
            {
                SkinningValidation.ValidateSource(vertices, 1, SkinningProfile.DualQuaternionRigid, Context);
                SkinnedGltfMesh mesh = CreateMesh(vertices, 1, wideIndices, SkinningProfile.DualQuaternionRigid);
                Assert.Equal(word, BitConverter.SingleToInt32Bits(mesh.Vertices[0].BoneWeights.X));
            }
            else
            {
                Assert.Throws<ArgumentException>(() =>
                    SkinningValidation.ValidateSource(vertices, 1, SkinningProfile.DualQuaternionRigid, Context));
                Assert.Throws<ArgumentException>(() => CreateMesh(vertices, 1, wideIndices, SkinningProfile.DualQuaternionRigid));
            }
        }

        [Theory]
        [InlineData(0x3f800008, 0x33800000)]
        [InlineData(0x322bcc76, 0x26000000)]
        public void DQ_total_uses_three_left_associated_binary32_adds(int firstWord, int smallWord)
        {
            SkinnedVertex vertex = ValidVertex();
            float small = BitConverter.Int32BitsToSingle(smallWord);
            vertex.BoneWeights = new Vector4(BitConverter.Int32BitsToSingle(firstWord), small, small, small);
            Vector4 weights = vertex.BoneWeights;
            Assert.Equal(firstWord, BitConverter.SingleToInt32Bits(((weights.X + weights.Y) + weights.Z) + weights.W));

            SkinningValidation.ValidateSource(new[] { vertex }, 1, SkinningProfile.DualQuaternionRigid, Context);
        }

        [Fact]
        public void DQ_rejects_overflowed_total_and_reports_context_without_a_clip()
        {
            SkinnedVertex vertex = ValidVertex();
            vertex.BoneWeights = new Vector4(float.MaxValue);
            var context = new SkinningValidationContext("overflow-fixture", null, 23);
            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                SkinningValidation.ValidateSource(new[] { vertex }, 1, SkinningProfile.DualQuaternionRigid, context));
            Assert.Contains(context.Asset, error.Message);
            Assert.Contains("23", error.Message);
            Assert.Contains("BoneWeights", error.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LBS_raw_weights_and_existing_scaling_are_unchanged(bool wideIndices)
        {
            SkinnedVertex vertex = ValidVertex();
            vertex.BoneIndices = new Vector4(0.75f, 0f, 0f, 0f);
            vertex.BoneWeights = new Vector4(2f, 0f, 0f, 0f);
            var vertices = new[] { vertex };
            byte[] snapshot = MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray();
            var bones = new[] { Matrix4x4.CreateTranslation(4, 0, 0) };
            var mesh = wideIndices
                ? new SkinnedGltfMesh(vertices, new uint[] { 0 }, bones, bones)
                : new SkinnedGltfMesh(vertices, new ushort[] { 0 }, bones, bones);

            SkinningValidation.ValidateSource(vertices, 1, SkinningProfile.LinearBlend, Context);
            CreateMesh(vertices, 1, wideIndices, SkinningProfile.LinearBlend);
            ModelVertex skinned = SkinningMath.SkinVertex(mesh.Vertices[0], bones);

            Assert.Equal(new Vector3(10, 4, 6), skinned.Position);
            Assert.Equal(Vector3.UnitY, skinned.Normal);
            Assert.Equal(snapshot, MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()).ToArray());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LBS_constructors_do_not_acquire_DQ_source_rejections(bool wideIndices)
        {
            SkinnedVertex vertex = ValidVertex();
            vertex.Position.X = float.NaN;
            vertex.BoneIndices = new Vector4(-1f, 0.5f, 129f, float.PositiveInfinity);
            vertex.BoneWeights = new Vector4(-1f, float.NaN, float.PositiveInfinity, 2f);
            var vertices = new[] { vertex };
            var bones = new Matrix4x4[129];
            var mesh = wideIndices
                ? new SkinnedGltfMesh(vertices, new uint[] { 0 }, bones, bones)
                : new SkinnedGltfMesh(vertices, new ushort[] { 0 }, bones, bones);

            SkinningValidation.ValidateSource(vertices, 129, SkinningProfile.LinearBlend, Context);
            CreateMesh(vertices, 129, wideIndices, SkinningProfile.LinearBlend);

            Assert.Equal(SkinningProfile.LinearBlend, mesh.Profile);
            Assert.Same(vertices, mesh.Vertices);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Explicit_profile_cannot_be_null(bool wideIndices)
        {
            var vertices = new[] { ValidVertex() };
            Assert.Throws<ArgumentNullException>(() => CreateMesh(vertices, 1, wideIndices, null!));
            Assert.Throws<ArgumentNullException>(() => SkinningValidation.ValidateSource(vertices, 1, null!, Context));
        }

        static SkinnedGltfMesh CreateMesh(SkinnedVertex[] vertices, int boneCount, bool wideIndices, SkinningProfile profile)
        {
            var bones = new Matrix4x4[boneCount];
            Array.Fill(bones, Matrix4x4.Identity);
            return wideIndices
                ? new SkinnedGltfMesh(vertices, new uint[] { 0 }, bones, bones, null, profile)
                : new SkinnedGltfMesh(vertices, new ushort[] { 0 }, bones, bones, null, profile);
        }

        static SkinnedVertex ValidVertex() => new()
        {
            Position = new Vector3(1, 2, 3),
            Normal = Vector3.UnitY,
            Color = Vector4.One,
            Uv = new Vector2(0.25f, 0.75f),
            BoneIndices = Vector4.Zero,
            BoneWeights = Vector4.UnitX,
            Tangent = new Vector4(1, 0, 0, -1),
        };

        static SkinnedVertex WithComponent(SkinnedVertex vertex, string field, int component, float value)
        {
            int offset = field switch
            {
                "Position" => 0, "Normal" => 3, "Color" => 6, "Uv" => 10,
                "BoneIndices" => 12, "BoneWeights" => 16, "Tangent" => 20,
                _ => throw new ArgumentException("Unknown vertex attribute.", nameof(field)),
            };
            Span<SkinnedVertex> storage = stackalloc SkinnedVertex[1];
            storage[0] = vertex;
            MemoryMarshal.Cast<SkinnedVertex, float>(storage)[offset + component] = value;
            return storage[0];
        }
    }
}
