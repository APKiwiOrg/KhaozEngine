using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class DqPositionTests
    {
        static readonly SkinningValidationContext Context = new("synthetic-position", null, 0);
        static readonly RigidDualQuaternion Identity = new(new Vector4(0, 0, 0, 1), Vector4.Zero);

        [Fact]
        public void Half_identity_half_Z90_is_DQ_not_LBS()
        {
            var quarter = new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            RigidDualQuaternion turn = DualQuaternionPalette.FromComposed(quarter, Context, 0);
            var source = Vertex(Vector3.UnitX, new Vector4(0, 1, 0, 0), new Vector4(.5f, .5f, 0, 0));
            Vector3 actual = DualQuaternionSkinning.Position(source, [Identity, turn]);
            Assert.InRange(MathF.Abs(actual.X - MathF.Sqrt(0.5f)), 0f, 2e-5f);
            Assert.InRange(MathF.Abs(actual.Y - MathF.Sqrt(0.5f)), 0f, 2e-5f);
            Assert.InRange(MathF.Abs(actual.Z), 0f, 2e-5f);
            Assert.True(MathF.Abs(actual.X - 0.5f) > 0.2f);
            Assert.True(MathF.Abs(actual.Y - 0.5f) > 0.2f);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void Every_slot_applies_rotation_about_a_pivot_plus_translation(int slot)
        {
            // +Z90 about (2,-3,5), then (4,-2,1), has model translation (3,-7,1).
            var transform = new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 3, -7, 1, 1);
            RigidDualQuaternion turn = DualQuaternionPalette.FromComposed(transform, Context, 0);
            Vector4 indices = Vector4.Zero, weights = Vector4.Zero;
            indices[slot] = 1;
            weights[slot] = 1;
            Vector3 actual = DualQuaternionSkinning.Position(Vertex(new Vector3(3, -3, 5), indices, weights),
                [Identity, turn]);
            AssertPoint(new Vector3(6, -4, 6), actual);
        }

        [Theory]
        [InlineData(1, 0, 0, 2, -3, -4)]
        [InlineData(0, 1, 0, -2, 3, -4)]
        [InlineData(0, 0, 1, -2, -3, 4)]
        public void Half_turns_preserve_the_axis_and_reverse_the_other_components(
            float x, float y, float z, float px, float py, float pz)
        {
            var halfTurn = new RigidDualQuaternion(new Vector4(x, y, z, 0), Vector4.Zero);
            AssertPoint(new Vector3(px, py, pz), DualQuaternionSkinning.Position(
                Vertex(new Vector3(2, 3, 4), Vector4.Zero, Vector4.UnitX), [halfTurn]));
        }

        public static TheoryData<string, JsonElement> WordCases()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "dual-quaternion", "rigid-words.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            var rows = new TheoryData<string, JsonElement>();
            foreach (JsonElement item in document.RootElement.GetProperty("cases").EnumerateArray())
                rows.Add(item.GetProperty("name").GetString()!, item.Clone());
            return rows;
        }

        [Theory]
        [MemberData(nameof(WordCases))]
        public void Independent_binary32_words_pin_the_ordered_graph(string name, JsonElement item)
        {
            var palette = new RigidDualQuaternion[item.GetProperty("palette").GetArrayLength()];
            for (int i = 0; i < palette.Length; i++)
            {
                JsonElement bone = item.GetProperty("palette")[i];
                palette[i] = new RigidDualQuaternion(Read4(bone.GetProperty("real")), Read4(bone.GetProperty("dual")));
            }
            JsonElement storedIndices = item.GetProperty("indices");
            var indices = new Vector4(storedIndices[0].GetInt32(), storedIndices[1].GetInt32(),
                storedIndices[2].GetInt32(), storedIndices[3].GetInt32());
            Vector4 weights = Read4(item.GetProperty("weights"));
            JsonElement position = item.GetProperty("source");
            var source = Vertex(new Vector3(ReadWord(position[0]), ReadWord(position[1]), ReadWord(position[2])),
                indices, weights);
            DualQuaternionSkinning.Blend(indices, weights, palette, out Vector4 real, out Vector4 dual);
            AssertWords(item.GetProperty("real"), [real.X, real.Y, real.Z, real.W], name);
            AssertWords(item.GetProperty("dual"), [dual.X, dual.Y, dual.Z, dual.W], name);
            Vector3 actual = DualQuaternionSkinning.Position(source, palette);
            AssertWords(item.GetProperty("expected"), [actual.X, actual.Y, actual.Z], name);
        }

        [Fact]
        public void Identity_ignores_noncontributing_bones_and_preserves_signed_zeros()
        {
            var turn = new RigidDualQuaternion(new Vector4(.5f), Vector4.Zero);
            var source = Vertex(new Vector3(-0f, 2, -0f), new Vector4(0, 1, 0, 1), Vector4.UnitX);
            Vector3 actual = DualQuaternionSkinning.Position(source, [Identity, turn]);
            Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(actual.X));
            Assert.Equal(2, actual.Y);
            Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(actual.Z));
        }

        [Fact]
        public void Weight_total_is_left_associated_and_the_unweighted_threshold_is_strict()
        {
            var source = Vertex(new Vector3(-0f, 2, -0f), Vector4.Zero,
                new Vector4(MathF.BitDecrement(1e-8f), 0, 0, 0));
            RigidDualQuaternion[] palette = [new RigidDualQuaternion(Vector4.Zero, Vector4.Zero)];
            Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(
                DualQuaternionSkinning.Position(source, palette).X));
            source.BoneWeights.X = 1e-8f;
            Assert.Throws<InvalidOperationException>(() => DualQuaternionSkinning.Position(source, palette));

            // The left-associated total equals 1e-8. Grouping the three small slots first is below it.
            float small = BitConverter.UInt32BitsToSingle(0x26000001);
            source.BoneWeights = new Vector4(BitConverter.UInt32BitsToSingle(0x322bcc74), small, small, small);
            Assert.Equal(1e-8f, ((source.BoneWeights.X + source.BoneWeights.Y) + source.BoneWeights.Z) + source.BoneWeights.W);
            Assert.True(source.BoneWeights.X + ((small + small) + small) < 1e-8f);
            Assert.Throws<InvalidOperationException>(() => DualQuaternionSkinning.Position(source, palette));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1e-12f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.MaxValue)]
        public void Nonfinite_or_tiny_real_norm_refuses_instead_of_falling_back(float w)
        {
            var source = Vertex(Vector3.UnitX, Vector4.Zero, Vector4.UnitX);
            RigidDualQuaternion[] palette = [new(new Vector4(0, 0, 0, w), Vector4.Zero)];
            Assert.Throws<InvalidOperationException>(() => DualQuaternionSkinning.Position(source, palette));
        }

        [Fact]
        public void Nonfinite_translation_or_position_arithmetic_refuses()
        {
            var source = Vertex(Vector3.UnitX, Vector4.Zero, Vector4.UnitX);
            RigidDualQuaternion[] palette = [new(new Vector4(0, 0, 0, 1), new Vector4(float.MaxValue, 0, 0, 0))];
            Assert.Throws<InvalidOperationException>(() => DualQuaternionSkinning.Position(source, palette));
            palette[0] = new RigidDualQuaternion(new Vector4(.5f), new Vector4(1e38f, 0, 0, 0));
            source.Position = new Vector3(float.MaxValue);
            Assert.Throws<InvalidOperationException>(() => DualQuaternionSkinning.Position(source, palette));
        }

        static SkinnedVertex Vertex(Vector3 position, Vector4 indices, Vector4 weights) => new()
        { Position = position, BoneIndices = indices, BoneWeights = weights };

        static float ReadWord(JsonElement word) => BitConverter.UInt32BitsToSingle(
            uint.Parse(word.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        static Vector4 Read4(JsonElement words) => new(ReadWord(words[0]), ReadWord(words[1]),
            ReadWord(words[2]), ReadWord(words[3]));

        static void AssertWords(JsonElement expected, float[] actual, string name)
        {
            for (int i = 0; i < actual.Length; i++)
            {
                uint word = uint.Parse(expected[i].GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                uint got = BitConverter.SingleToUInt32Bits(actual[i]);
                Assert.True(word == got, $"{name}[{i}]: expected {word:x8}, actual {got:x8}");
            }
        }

        static void AssertPoint(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(MathF.Abs(expected.X - actual.X), 0, 2e-5f);
            Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, 2e-5f);
            Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, 2e-5f);
        }
    }
}
