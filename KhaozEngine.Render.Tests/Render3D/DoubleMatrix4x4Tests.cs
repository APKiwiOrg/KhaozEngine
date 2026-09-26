using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>The double matrix the temporal reprojection is built in, against System.Numerics.</summary>
    public sealed class DoubleMatrix4x4Tests
    {
        static readonly Matrix4x4 View = Matrix4x4.CreateLookAt(new Vector3(60f, 4f, 60f), new Vector3(10f, 2f, 5f), Vector3.UnitY);
        static readonly Matrix4x4 Projection = Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 16f / 9f, 0.1f, 600f);

        [Fact]
        public void A_float_matrix_round_trips_exactly()
            => Assert.Equal(View, new DoubleMatrix4x4(View).ToSingle());

        [Fact]
        public void The_product_matches_System_Numerics()
        {
            Matrix4x4 expected = View * Projection;
            Matrix4x4 actual = (new DoubleMatrix4x4(View) * new DoubleMatrix4x4(Projection)).ToSingle();
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                    Assert.Equal(expected[row, column], actual[row, column], 4);
            }
        }

        [Fact]
        public void The_inverse_undoes_the_matrix_to_double_precision()
        {
            var m = new DoubleMatrix4x4(View * Projection);
            Assert.True(DoubleMatrix4x4.TryInvert(m, out DoubleMatrix4x4 inverse));
            DoubleMatrix4x4 identity = inverse * m;
            double[] values =
            [
                identity.M11, identity.M12, identity.M13, identity.M14, identity.M21, identity.M22, identity.M23, identity.M24,
                identity.M31, identity.M32, identity.M33, identity.M34, identity.M41, identity.M42, identity.M43, identity.M44,
            ];
            for (int i = 0; i < 16; i++)
                Assert.Equal(i % 5 == 0 ? 1.0 : 0.0, values[i], 9);
        }

        [Fact]
        public void A_singular_matrix_has_no_inverse()
        {
            Assert.False(DoubleMatrix4x4.TryInvert(new DoubleMatrix4x4(default(Matrix4x4)), out DoubleMatrix4x4 inverse));
            Assert.True(double.IsNaN(inverse.M11));
            var flat = new Matrix4x4(1f, 2f, 3f, 4f, 2f, 4f, 6f, 8f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f);
            Assert.False(DoubleMatrix4x4.TryInvert(new DoubleMatrix4x4(flat), out _));
        }
    }
}
