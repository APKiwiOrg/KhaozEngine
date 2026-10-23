using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class DqPaletteTests
    {
        static readonly SkinningValidationContext Context = new("synthetic-palette", "rigid-pivot", 23);

        [Fact]
        public void Composition_order_is_inverse_bind_then_joint()
        {
            Assert.Equal(32, Marshal.SizeOf<RigidDualQuaternion>());
            RigidDualQuaternion identity = DualQuaternionPalette.FromComposed(Matrix4x4.Identity, Context, 0);
            Assert.Equal(new Vector4(0f, 0f, 0f, 1f), identity.Real);
            Assert.Equal(Vector4.Zero, identity.Dual);
            AssertWords(identity, [0, 0, 0, 0x3f800000, 0, 0, 0, 0]);

            var destination = new RigidDualQuaternion[1];
            DualQuaternionPalette.Prepare([Matrix4x4.CreateTranslation(6, -8, 10)],
                [Matrix4x4.CreateTranslation(-2, 4, -6)], destination, Context);
            AssertWords(destination[0], [0, 0, 0, 0x3f800000, 0x40000000, 0xc0000000, 0x40000000, 0x80000000]);

            // Analytic +Z quarter turn sends +X to +Y. No trigonometric input production.
            var quarter = new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            RigidDualQuaternion turn = DualQuaternionPalette.FromComposed(quarter, Context, 0);
            AssertPoint(new Vector3(0, 1, 0), Transform(turn, Vector3.UnitX));
            AssertPoint(new Vector3(-1, 0, 0), Transform(turn, Vector3.UnitY));

            // Rotation q=(1,2,2,4)/5 about (2,-3,5), derived with exact rational arithmetic.
            var joint = new Matrix4x4(.36f, .8f, -.48f, 0, -.48f, .6f, .64f, 0,
                .8f, 0, .6f, 0, 2, -3, 5, 1);
            DualQuaternionPalette.Prepare([joint], [Matrix4x4.CreateTranslation(-2, 3, -5)], destination, Context);
            AssertWords(destination[0], [0x3e4ccccc, 0x3ecccccc, 0x3ecccccc, 0x3f4ccccd,
                0xc04ccccc, 0x3e4cccc4, 0x3fb33334, 0xb3800000]);
            AssertPoint(new Vector3(2, -3, 5), Transform(destination[0], new Vector3(2, -3, 5)));
            AssertPoint(new Vector3(-4.8f, 1, 2.4f), Transform(destination[0], new Vector3(4, 1, -2)));
        }

        // Compact portable input/output words for the later ARM64/x64 hosted gate. Expectations were
        // frozen before implementation using independent Python struct-pack binary32 operations.
        // Rational rotations use q permutations of (1,2,2,4)/5 and the listed half-turn axes.
        // Tie matrices perturb an off-diagonal by 2^-20, within the Gram allowance, so choosing
        // a different equal candidate changes output words rather than producing an invisible tie.
        public static TheoryData<string, uint[], uint[]> PaletteWords() => new()
        {
            { "identity",
                [ 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000 ],
                [ 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000 ] },
            { "translation",
                [ 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x3f800000, 0xc0000000, 0x40400000, 0x80000000 ] },
            { "positive-z-quarter",
                [ 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000 ],
                [ 0x00000000, 0x00000000, 0x3f3504f4, 0x3f3504f4, 0x00000000, 0x00000000, 0x00000000, 0x80000000 ] },
            { "unique-x",
                [ 0x3eb851ec, 0x3f4ccccd, 0x3ef5c28f, 0x00000000, 0x3ef5c28f, 0xbf19999a, 0x3f23d70a, 0x00000000, 0x3f4ccccd, 0x00000000, 0xbf19999a, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3f4ccccd, 0x3ecccccc, 0x3ecccccc, 0x3e4ccccc, 0xbfe66666, 0x3fcccccd, 0x40266666, 0xbf99999a ] },
            { "unique-y",
                [ 0xbf19999a, 0x3f23d70a, 0xbef5c28f, 0x00000000, 0x00000000, 0x3f19999a, 0x3f4ccccd, 0x00000000, 0x3f4ccccd, 0x3ef5c28f, 0xbeb851ec, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3e4ccccc, 0x3f4ccccd, 0x3ecccccc, 0x3ecccccc, 0xc0333334, 0xbf199999, 0x4019999a, 0x3e4cccd8 ] },
            { "unique-z",
                [ 0xbf19999a, 0x3f4ccccd, 0x00000000, 0x00000000, 0xbef5c28f, 0xbeb851ec, 0x3f4ccccd, 0x00000000, 0x3f23d70a, 0x3ef5c28f, 0x3f19999a, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3e4ccccc, 0x3ecccccc, 0x3f4ccccd, 0x3ecccccc, 0xc019999a, 0xbf800000, 0x3fffffff, 0xbfe66668 ] },
            { "unique-w",
                [ 0x3eb851ec, 0x3f4ccccd, 0xbef5c28f, 0x00000000, 0xbef5c28f, 0x3f19999a, 0x3f23d70a, 0x00000000, 0x3f4ccccd, 0x00000000, 0x3f19999a, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3e4ccccc, 0x3ecccccc, 0x3ecccccc, 0x3f4ccccd, 0xbf999998, 0xbfb33333, 0x404cccce, 0xbf199999 ] },
            { "negative-w-x",
                [ 0x3f19999a, 0x3f23d70a, 0xbef5c28f, 0x00000000, 0x00000000, 0xbf19999a, 0xbf4ccccd, 0x00000000, 0xbf4ccccd, 0x3ef5c28f, 0xbeb851ec, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0xbf4ccccd, 0xbe4ccccc, 0x3ecccccc, 0x3ecccccc, 0x3e4ccccc, 0xc0666666, 0xbf19999b, 0xbf4ccccb ] },
            { "negative-w-y",
                [ 0xbf19999a, 0x3f23d70a, 0x3ef5c28f, 0x00000000, 0x00000000, 0x3f19999a, 0xbf4ccccd, 0x00000000, 0xbf4ccccd, 0xbef5c28f, 0xbeb851ec, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0xbe4ccccc, 0xbf4ccccd, 0x3ecccccc, 0x3ecccccc, 0x40000000, 0xbfe66665, 0xb3800000, 0xc0266666 ] },
            { "negative-w-z",
                [ 0xbf19999a, 0xbf4ccccd, 0x00000000, 0x00000000, 0x3ef5c28f, 0xbeb851ec, 0xbf4ccccd, 0x00000000, 0x3f23d70a, 0xbef5c28f, 0x3f19999a, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0xbe4ccccc, 0x3ecccccc, 0xbf4ccccd, 0x3ecccccc, 0x3f4cccce, 0xbf199997, 0x3f999999, 0x4059999a ] },
            { "half-x",
                [ 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x40400000, 0x40000000, 0xbf800000 ] },
            { "half-y",
                [ 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0xc0400000, 0x00000000, 0x3f800000, 0x40000000 ] },
            { "half-z",
                [ 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0xc0000000, 0xbf800000, 0x00000000, 0xc0400000 ] },
            { "half-negative-x",
                [ 0xbf471c72, 0xbee38e39, 0xbee38e39, 0x00000000, 0xbee38e39, 0xbde38e39, 0x3f638e39, 0x00000000, 0xbee38e39, 0x3f638e39, 0xbde38e39, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3eaaaaab, 0xbf2aaaab, 0xbf2aaaab, 0x80000000, 0x40555556, 0x3fd55556, 0x80000000, 0x3eaaaaa8 ] },
            { "half-negative-y",
                [ 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xbe8f5c29, 0xbf75c28f, 0x00000000, 0x00000000, 0xbf75c28f, 0x3e8f5c29, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x80000000, 0x3f199999, 0xbf4ccccd, 0x80000000, 0xbe4cccc8, 0x3f4ccccd, 0x3f199999, 0x40666666 ] },
            { "tie-wx",
                [ 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800008, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3f3504f6, 0x00000000, 0x00000000, 0x3f3504f0, 0x3f3504f0, 0x3f350500, 0x4062462f, 0xbf3504f6 ] },
            { "tie-wy",
                [ 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x3f800008, 0x00000000, 0x00000000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x00000000, 0x3f3504f6, 0x00000000, 0x3f3504f0, 0xbfb504f8, 0xbfb504f0, 0x403504f2, 0x3fb504f6 ] },
            { "tie-wz",
                [ 0x00000000, 0x3f800008, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x00000000, 0x00000000, 0x3f3504f6, 0x3f3504f0, 0xbf3504fc, 0xc007c3b6, 0x4007c3b4, 0xc007c3b8 ] },
            { "tie-xy",
                [ 0x00000000, 0x3f800008, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3f3504f0, 0x3f3504f6, 0x00000000, 0x00000000, 0xc007c3b8, 0x4007c3b4, 0x4007c3b6, 0x3f3504fc ] },
            { "tie-xz",
                [ 0x00000000, 0x00000000, 0x3f800008, 0x00000000, 0x00000000, 0xbf800000, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3f3504f0, 0x00000000, 0x3f3504f6, 0x00000000, 0xbfb504f6, 0x3fb504ed, 0x3fb504f0, 0xc03504f4 ] },
            { "tie-yz",
                [ 0xbf800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800008, 0x00000000, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x00000000, 0x3f3504f0, 0x3f3504f6, 0x00000000, 0xc062462f, 0xbf3504f6, 0x3f3504f0, 0xbf350500 ] },
            { "tie-wxyz",
                [ 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x00000000, 0x3f800008, 0x00000000, 0x3f800000, 0x00000000, 0x00000000, 0x00000000, 0x40000000, 0xc0800000, 0x40c00000, 0x3f800000 ],
                [ 0x3f000006, 0x3efffffc, 0x3efffffc, 0x3efffffc, 0xbffffffc, 0x35c00000, 0x40400001, 0xbf800002 ] },
        };

        [Theory]
        [MemberData(nameof(PaletteWords))]
        public void All_extraction_branches_and_ties_are_pinned(string name, uint[] input, uint[] expected)
        {
            Matrix4x4 matrix = MemoryMarshal.Cast<uint, Matrix4x4>(input.AsSpan())[0];
            RigidDualQuaternion converted = DualQuaternionPalette.FromComposed(matrix, Context, 7);
            AssertWords(converted, expected);
            var sentinel = new RigidDualQuaternion(Vector4.One, new Vector4(-1));
            var destination = new[] { sentinel, sentinel };
            DualQuaternionPalette.Prepare([matrix], [Matrix4x4.Identity], destination, Context);
            AssertWords(destination[0], expected);
            Assert.Equal(sentinel.Real, destination[1].Real);
            Assert.Equal(sentinel.Dual, destination[1].Dual);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }

        [Fact]
        public void Equivalent_signs_are_canonical()
        {
            // Both antipodes encode the same rotation. X is largest but W must decide the sign.
            var positive = QuaternionMatrix(.8, .2, -.4, -.4);
            var negative = QuaternionMatrix(-.8, -.2, .4, .4);
            uint[] expected = [0xbf4ccccd, 0xbe4ccccc, 0x3ecccccc, 0x3ecccccc,
                0x3e4ccccc, 0xc0666666, 0xbf19999b, 0xbf4ccccb];
            AssertWords(DualQuaternionPalette.FromComposed(positive, Context, 0), expected);
            AssertWords(DualQuaternionPalette.FromComposed(negative, Context, 0), expected);
        }

        [Fact]
        public void Only_composed_rigidity_controls_eligibility()
        {
            var destination = new RigidDualQuaternion[1];
            DualQuaternionPalette.Prepare([Matrix4x4.CreateScale(2, .5f, 4)],
                [Matrix4x4.CreateScale(.5f, 2, .25f)], destination, Context);
            AssertWords(destination[0], [0, 0, 0, 0x3f800000, 0, 0, 0, 0]);

            const float allowance = 1f / 65536;
            foreach (float sign in new[] { 1f, -1f })
            {
                var boundary = Matrix4x4.Identity;
                boundary.M12 = sign * allowance;
                DualQuaternionPalette.FromComposed(boundary, Context, 0);
                DualQuaternionPalette.Prepare([boundary], [Matrix4x4.Identity], destination, Context);
                boundary.M12 = sign * MathF.BitIncrement(allowance);
                AssertDiagnostic(boundary, "Gram");

                boundary = Matrix4x4.Identity;
                boundary.M11 = 1f + sign / 131072;
                DualQuaternionPalette.FromComposed(boundary, Context, 0);
                boundary.M11 = sign > 0 ? MathF.BitIncrement(boundary.M11) : MathF.BitDecrement(boundary.M11);
                AssertDiagnostic(boundary, "Gram");
            }
        }

        public static TheoryData<int, float> NonfiniteWords()
        {
            var rows = new TheoryData<int, float>();
            for (int component = 0; component < 16; component++)
                foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                    rows.Add(component, value);
            return rows;
        }

        [Theory]
        [MemberData(nameof(NonfiniteWords))]
        public void Every_matrix_component_must_be_finite(int component, float value)
        {
            var matrix = Matrix4x4.Identity;
            MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateSpan(ref matrix, 1))[component] = value;
            AssertDiagnostic(matrix, "finite");
        }

        [Theory]
        [InlineData(3, float.Epsilon)]
        [InlineData(7, -float.Epsilon)]
        [InlineData(11, float.Epsilon)]
        [InlineData(15, 0.99999994f)]
        [InlineData(15, 1.0000001f)]
        public void Affine_last_column_is_exact(int component, float value)
        {
            var matrix = Matrix4x4.Identity;
            MemoryMarshal.Cast<Matrix4x4, float>(MemoryMarshal.CreateSpan(ref matrix, 1))[component] = value;
            AssertDiagnostic(matrix, "affine");
        }

        [Theory]
        [InlineData(-1, 1, 1, "determinant")]
        [InlineData(0, 1, 1, "Gram")]
        [InlineData(2, 2, 2, "Gram")]
        [InlineData(1, 2, .5f, "Gram")]
        [InlineData(1.0001f, 1.0001f, 1.0001f, "Gram")]
        [InlineData(float.MaxValue, 1, 1, "Gram")]
        public void Reflection_collapse_and_both_scale_kinds_are_rejected(float x, float y, float z, string detail)
        {
            AssertDiagnostic(Matrix4x4.CreateScale(x, y, z), detail);
        }

        [Fact]
        public void Shear_and_nonfinite_intermediate_arithmetic_are_rejected()
        {
            var shear = Matrix4x4.Identity;
            shear.M21 = .01f;
            AssertDiagnostic(shear, "Gram");
            var hugeTranslation = QuaternionMatrix(.6, .8, 0, 0);
            hugeTranslation.M41 = hugeTranslation.M42 = float.MaxValue;
            hugeTranslation.M43 = 0;
            AssertDiagnostic(hugeTranslation, "finite");

            var destination = new[] { new RigidDualQuaternion(Vector4.One, Vector4.One) };
            byte[] before = Bytes(destination);
            Assert.Throws<ArgumentException>(() => DualQuaternionPalette.Prepare(
                [Matrix4x4.CreateScale(2)], [Matrix4x4.CreateScale(float.MaxValue)], destination, Context));
            Assert.Equal(before, Bytes(destination));
        }

        [Fact]
        public void Failed_preparation_never_publishes_a_partial_palette()
        {
            var sentinel = new RigidDualQuaternion(new Vector4(2, 3, 4, 5), new Vector4(6, 7, 8, 9));
            var destination = new[] { sentinel, sentinel, sentinel };
            byte[] before = Bytes(destination);
            var joints = new[] { Matrix4x4.CreateTranslation(2, 4, 6), Matrix4x4.CreateScale(2) };
            var inverseBind = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                DualQuaternionPalette.Prepare(joints, inverseBind, destination, Context));
            AssertContext(error, 1);
            Assert.Equal(before, Bytes(destination));
            Assert.Equal(Matrix4x4.CreateScale(2), joints[1]);

            var lateOverflow = QuaternionMatrix(.6, .8, 0, 0);
            lateOverflow.M41 = lateOverflow.M42 = float.MaxValue;
            lateOverflow.M43 = 0;
            joints[1] = lateOverflow;
            Assert.Throws<ArgumentException>(() => DualQuaternionPalette.Prepare(joints, inverseBind, destination, Context));
            Assert.Equal(before, Bytes(destination));
        }

        [Theory]
        [InlineData(1, 0, 1)]
        [InlineData(0, 1, 1)]
        [InlineData(2, 2, 1)]
        [InlineData(129, 129, 129)]
        public void Invalid_span_lengths_do_not_write(int joints, int binds, int capacity)
        {
            var destination = new RigidDualQuaternion[capacity];
            Array.Fill(destination, new RigidDualQuaternion(Vector4.One, Vector4.One));
            byte[] before = Bytes(destination);
            Assert.Throws<ArgumentException>(() => DualQuaternionPalette.Prepare(
                new Matrix4x4[joints], new Matrix4x4[binds], destination, Context));
            Assert.Equal(before, Bytes(destination));
        }

        [Fact]
        public void Empty_and_maximum_palettes_are_supported()
        {
            DualQuaternionPalette.Prepare([], [], [], Context);
            var matrices = new Matrix4x4[128];
            Array.Fill(matrices, Matrix4x4.Identity);
            var destination = new RigidDualQuaternion[128];
            DualQuaternionPalette.Prepare(matrices, matrices, destination, Context);
            Assert.All(destination, value => AssertWords(value, [0, 0, 0, 0x3f800000, 0, 0, 0, 0]));
        }

        static void AssertDiagnostic(Matrix4x4 matrix, string detail)
        {
            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                DualQuaternionPalette.FromComposed(matrix, Context, 7));
            AssertContext(error, 7);
            Assert.Contains(detail, error.Message, StringComparison.OrdinalIgnoreCase);
        }

        static void AssertContext(ArgumentException error, int joint)
        {
            Assert.Contains(Context.Asset, error.Message);
            Assert.Contains(Context.Clip!, error.Message);
            Assert.Contains("sample 23", error.Message);
            Assert.Contains($"joint {joint}", error.Message);
            Assert.Contains("DualQuaternionRigid", error.Message);
            Assert.Contains("version 1", error.Message);
        }

        static byte[] Bytes(RigidDualQuaternion[] palette) => MemoryMarshal.AsBytes(palette.AsSpan()).ToArray();

        static void AssertWords(RigidDualQuaternion value, uint[] expected)
        {
            ReadOnlySpan<RigidDualQuaternion> storage = MemoryMarshal.CreateReadOnlySpan(ref value, 1);
            Assert.Equal(expected, MemoryMarshal.Cast<RigidDualQuaternion, uint>(storage).ToArray());
        }

        static Matrix4x4 QuaternionMatrix(double x, double y, double z, double w) => new(
            (float)(1 - 2 * (y * y + z * z)), (float)(2 * (x * y + z * w)), (float)(2 * (x * z - y * w)), 0,
            (float)(2 * (x * y - z * w)), (float)(1 - 2 * (x * x + z * z)), (float)(2 * (y * z + x * w)), 0,
            (float)(2 * (x * z + y * w)), (float)(2 * (y * z - x * w)), (float)(1 - 2 * (x * x + y * y)), 0,
            2, -4, 6, 1);

        // Independent double Hamilton sandwich and 2*d*conjugate(r), used only for analytic geometry.
        static Vector3 Transform(RigidDualQuaternion value, Vector3 point)
        {
            double x = value.Real.X, y = value.Real.Y, z = value.Real.Z, w = value.Real.W;
            double dx = value.Dual.X, dy = value.Dual.Y, dz = value.Dual.Z, dw = value.Dual.W;
            double dot = x * point.X + y * point.Y + z * point.Z;
            double scale = w * w - x * x - y * y - z * z;
            return new Vector3(
                (float)(scale * point.X + 2 * dot * x + 2 * w * (y * point.Z - z * point.Y)
                    + 2 * (-dw * x + dx * w - dy * z + dz * y)),
                (float)(scale * point.Y + 2 * dot * y + 2 * w * (z * point.X - x * point.Z)
                    + 2 * (-dw * y + dy * w - dz * x + dx * z)),
                (float)(scale * point.Z + 2 * dot * z + 2 * w * (x * point.Y - y * point.X)
                    + 2 * (-dw * z + dz * w - dx * y + dy * x)));
        }

        static void AssertPoint(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(MathF.Abs(expected.X - actual.X), 0, 2e-5f);
            Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, 2e-5f);
            Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, 2e-5f);
        }
    }
}
