using System;
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A 2D camera: <see cref="Position"/> is the world point shown at the centre of the viewport,
    /// with <see cref="Zoom"/> (>1 = zoomed in) and <see cref="Rotation"/> (radians). Pure System.Numerics,
    /// headless, no GPU. Produces the view-projection a <see cref="SpriteBatch"/> uploads.
    /// </summary>
    public sealed class Camera2D
    {
        public Vector2 Position = Vector2.Zero;
        public float Zoom = 1f;
        public float Rotation = 0f;

        /// <summary>World -> screen-pixel transform (top-left origin, y-down).</summary>
        public Matrix4x4 GetView(int viewportWidth, int viewportHeight)
        {
            var center = new Vector3(viewportWidth * 0.5f, viewportHeight * 0.5f, 0f);
            return Matrix4x4.CreateTranslation(-Position.X, -Position.Y, 0f)
                 * Matrix4x4.CreateRotationZ(-Rotation)
                 * Matrix4x4.CreateScale(Zoom, Zoom, 1f)
                 * Matrix4x4.CreateTranslation(center);
        }

        /// <summary>World -> clip transform (the matrix the sprite batch uses).</summary>
        public Matrix4x4 GetViewProjection(int viewportWidth, int viewportHeight)
        {
            // y-down ortho lands the right way up in the Metal render target (no clip-Y flip needed).
            var ortho = Matrix4x4.CreateOrthographicOffCenter(0, viewportWidth, viewportHeight, 0, -1, 1);
            return GetView(viewportWidth, viewportHeight) * ortho;
        }

        public Vector2 WorldToScreen(Vector2 world, int viewportWidth, int viewportHeight)
        {
            var v = Vector2.Transform(world, GetView(viewportWidth, viewportHeight));
            return v;
        }

        public Vector2 ScreenToWorld(Vector2 screen, int viewportWidth, int viewportHeight)
        {
            Matrix4x4.Invert(GetView(viewportWidth, viewportHeight), out var inv);
            return Vector2.Transform(screen, inv);
        }
    }
}
