using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A background layer's parallax rate. <see cref="Factor"/> is per-axis relative to the camera:
    /// <c>0</c> = static (a fixed backdrop / skybox), <c>1</c> = locked to the world (moves with the
    /// foreground), <c>0.5</c> = half speed (appears farther away). The game derives a layer camera from
    /// <see cref="ViewPosition"/> and draws the layer's sprites with it (zoom/rotation shared with the main
    /// camera; parallax is translation-only).
    /// </summary>
    public readonly struct ParallaxLayer
    {
        /// <summary>Per-axis scroll rate relative to the camera.</summary>
        public readonly Vector2 Factor;

        public ParallaxLayer(Vector2 factor) => Factor = factor;

        /// <summary>Uniform factor on both axes.</summary>
        public ParallaxLayer(float factor) => Factor = new Vector2(factor, factor);

        /// <summary>World position a layer's camera should sit at for the given
        /// <paramref name="cameraPosition"/>: <c>cameraPosition * Factor</c>, per axis.</summary>
        public Vector2 ViewPosition(Vector2 cameraPosition) => cameraPosition * Factor;
    }
}
