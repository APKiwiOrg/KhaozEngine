using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Input;

/// <summary>
/// Adaptive virtual resolution and the matching coordinate transform. Mobile: a fixed virtual
/// width, scaled to fill the screen (height adapts to aspect ratio). Desktop: scale 1.0, virtual
/// size equals the window size. No render target and no letterboxing — rendering uses
/// <see cref="ScaleMatrix"/> via <c>SpriteBatch.Begin</c>. Implements <see cref="ICoordinateTransform"/>
/// so it can be passed straight to the <see cref="InputManager"/>.
/// </summary>
public sealed class VirtualResolution : ICoordinateTransform
{
    private readonly GraphicsDeviceManager _graphicsDeviceManager;
    private readonly bool _isMobile;
    private float _scale;
    private int _width;
    private int _height;

    /// <summary>Reference virtual width used for mobile scaling.</summary>
    public int BaseWidth { get; }

    /// <summary>Reference virtual height (design baseline).</summary>
    public int ReferenceHeight { get; }

    /// <summary>Current virtual width. On mobile this is <see cref="BaseWidth"/>; on desktop, the window width.</summary>
    public int Width => _width;

    /// <summary>Current virtual height (depends on screen size and scale).</summary>
    public int Height => _height;

    /// <summary>Scale factor from virtual coordinates to screen pixels (mobile: screenWidth / BaseWidth; desktop: 1).</summary>
    public float Scale => _scale;

    /// <summary>Transform to pass to <c>SpriteBatch.Begin</c> to map virtual coordinates to screen pixels.</summary>
    public Matrix ScaleMatrix { get; private set; }

    /// <summary>Safe-area insets in virtual pixels; launchers set these for notches/cutouts.</summary>
    public SafeAreaInsets SafeArea { get; set; } = SafeAreaInsets.Zero;

    /// <summary>Convenience rectangle covering the full virtual viewport (0, 0, Width, Height).</summary>
    public Rectangle FullRect => new(0, 0, _width, _height);

    /// <inheritdoc/>
    public Rectangle? VirtualBounds => FullRect;

    /// <summary>Creates an adaptive virtual resolution.</summary>
    /// <param name="graphicsDeviceManager">Used to read the current back-buffer size in <see cref="Initialize"/>.</param>
    /// <param name="isMobile">True on touch platforms (fixed virtual width); false for 1:1 desktop.</param>
    /// <param name="baseWidth">Reference virtual width for mobile scaling.</param>
    /// <param name="referenceHeight">Reference virtual height (design baseline).</param>
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

    /// <summary>
    /// Recomputes virtual size and scale from the current back-buffer size. Call on startup and
    /// whenever the window size changes.
    /// </summary>
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

    /// <inheritdoc/>
    public Vector2 ScreenToVirtual(Vector2 screenPosition) => screenPosition / _scale;
}
