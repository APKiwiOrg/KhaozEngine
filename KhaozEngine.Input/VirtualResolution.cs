using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Input;

// Adaptive virtual resolution. Mobile: fixed virtual width, scale to fill.
// Desktop: scale 1.0, virtual size = window size. No render target, no letterboxing.
public sealed class VirtualResolution : ICoordinateTransform
{
    private readonly GraphicsDeviceManager _graphicsDeviceManager;
    private readonly bool _isMobile;
    private float _scale;
    private int _width;
    private int _height;

    public int BaseWidth { get; }
    public int ReferenceHeight { get; }
    public int Width => _width;
    public int Height => _height;
    public float Scale => _scale;
    public Matrix ScaleMatrix { get; private set; }
    public SafeAreaInsets SafeArea { get; set; } = SafeAreaInsets.Zero;
    public Rectangle FullRect => new(0, 0, _width, _height);
    public Rectangle? VirtualBounds => FullRect;

    public VirtualResolution(
        GraphicsDeviceManager graphicsDeviceManager,
        bool isMobile,
        int baseWidth = 440,
        int referenceHeight = 956)
    {
        _graphicsDeviceManager = graphicsDeviceManager;
        _isMobile = isMobile;
        BaseWidth = baseWidth;
        ReferenceHeight = referenceHeight;
        _width = baseWidth;
        _height = referenceHeight;
        _scale = 1f;
        ScaleMatrix = Matrix.Identity;
    }

    public void Initialize()
    {
        GraphicsDevice graphicsDevice = _graphicsDeviceManager.GraphicsDevice;
        int screenWidth = graphicsDevice.PresentationParameters.BackBufferWidth;
        int screenHeight = graphicsDevice.PresentationParameters.BackBufferHeight;
        if (screenWidth <= 0 || screenHeight <= 0) return;

        if (_isMobile)
        {
            _scale = (float)screenWidth / BaseWidth;
            _width = BaseWidth;
            _height = (int)(screenHeight / _scale);
            ScaleMatrix = Matrix.CreateScale(_scale, _scale, 1f);
        }
        else
        {
            _scale = 1f;
            _width = screenWidth;
            _height = screenHeight;
            ScaleMatrix = Matrix.Identity;
        }
    }

    public Vector2 ScreenToVirtual(Vector2 screenPosition) => screenPosition / _scale;
}
