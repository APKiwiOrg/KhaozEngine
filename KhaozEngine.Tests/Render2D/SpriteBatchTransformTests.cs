using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Render2D
{
    // SpriteBatch.Begin(..., Matrix4x4 transform) pre-multiplies a model transform before the view-projection so
    // a whole composed draw (panel + icon + text) rotates/translates about a pivot - DrawString has no rotation,
    // so it must happen at the batch level. The order-of-composition is the easy thing to get backwards; this
    // pins it without a GPU.
    public class SpriteBatchTransformTests
    {
        [Fact]
        public void Compose_applies_the_model_transform_before_the_view_projection()
        {
            // A non-commutative pair: rotate then a distinct projection. The model must be applied first.
            var model = Matrix4x4.CreateRotationZ(0.5f) * Matrix4x4.CreateTranslation(10, -3, 0);
            var viewProjection = Matrix4x4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);
            var p = new Vector4(7, 11, 0, 1);

            Vector4 composed = Vector4.Transform(p, SpriteBatch.ComposeModelViewProjection(model, viewProjection));
            Vector4 stepwise = Vector4.Transform(Vector4.Transform(p, model), viewProjection);

            Assert.Equal(stepwise.X, composed.X, 5);
            Assert.Equal(stepwise.Y, composed.Y, 5);
        }

        [Fact]
        public void Compose_with_identity_model_equals_the_view_projection_alone()
        {
            var viewProjection = Matrix4x4.CreateOrthographicOffCenter(0, 800, 600, 0, -1, 1);
            var p = new Vector4(123, 45, 0, 1);

            Vector4 composed = Vector4.Transform(p, SpriteBatch.ComposeModelViewProjection(Matrix4x4.Identity, viewProjection));
            Vector4 plain = Vector4.Transform(p, viewProjection);

            Assert.Equal(plain.X, composed.X, 5);
            Assert.Equal(plain.Y, composed.Y, 5);
        }
    }
}
