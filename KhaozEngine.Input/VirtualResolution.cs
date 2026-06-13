using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace KhaozEngine.Input;

/// <summary>
/// Adaptive virtual resolution and the matching coordinate transform. Two scaling modes, both
/// without a render target or letterbox bars (rendering uses <see cref="ScaleMatrix"/> via
/// <c>SpriteBatch.Begin</c>):
/// <list type="bullet">
/// <item><b>Design-scaled</b> (mobile, and opt-in on desktop via <see cref="DesignScaled"/>): a
/// fixed <see cref="BaseWidth"/> design space scaled to fill the window width, with the height axis
/// adapting to the aspect ratio (fill-the-width, adaptive-height — no letterbox, no offset). A
/// large/Retina window scales the whole design space up, so UI keeps the same fixed design size.</item>
/// <item><b>One-to-one</b> (the desktop default): scale 1.0, virtual size equals the window size,
/// <see cref="ScaleMatrix"/> is identity — UI sizes in raw back-buffer pixels.</item>
/// </list>
/// Implements <see cref="ICoordinateTransform"/> so it can be passed straight to the
/// <see cref="InputManager"/>; <see cref="ScreenToVirtual"/> divides by <see cref="Scale"/> in both
/// modes, so pointer hit-testing lines up with whatever scaling is in effect. Also implements
/// <see cref="IDesignViewport"/> so screens can render/lay out against that fakeable seam.
/// </summary>
public sealed class VirtualResolution : ICoordinateTransform, IDesignViewport
{
    private readonly GraphicsDeviceManager? _graphicsDeviceManager;
    private readonly bool _designScaled;
    private float _scale;
    private int _width;
    private int _height;

    /// <summary>Reference virtual width: the design baseline the design-scaled mode fills the window with.</summary>
    public int BaseWidth { get; }

    /// <summary>Reference virtual height (design baseline).</summary>
    public int ReferenceHeight { get; }

    /// <summary>Current virtual width. Design-scaled: <see cref="BaseWidth"/>; one-to-one: the window width.</summary>
    public int Width => _width;

    /// <summary>Current virtual height (depends on screen size and scale).</summary>
    public int Height => _height;

    /// <summary>Scale factor from virtual coordinates to screen pixels (design-scaled: screenWidth / BaseWidth; one-to-one: 1).</summary>
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
    /// <param name="graphicsDeviceManager">Used to read the current back-buffer size in <see cref="Initialize"/>.
    /// May be null when the size is driven manually via <see cref="Configure"/> (e.g. headless tests).</param>
    /// <param name="isMobile">True on touch platforms: enables the design-scaled mode (fixed virtual width).
    /// False gives the 1:1 desktop default. For an opt-in design-scaled desktop, prefer <see cref="DesignScaled"/>.</param>
    /// <param name="baseWidth">Reference virtual width (design baseline) for the design-scaled mode.</param>
    /// <param name="referenceHeight">Reference virtual height (design baseline).</param>
    public VirtualResolution(
        GraphicsDeviceManager? graphicsDeviceManager,
        bool isMobile,
        int baseWidth = 440,
        int referenceHeight = 956)
    {
        _graphicsDeviceManager = graphicsDeviceManager;
        _designScaled = isMobile;
        BaseWidth = baseWidth;
        ReferenceHeight = referenceHeight;
        _width = baseWidth;
        _height = referenceHeight;
        _scale = 1f;
        ScaleMatrix = Matrix.Identity;
    }

    /// <summary>
    /// Creates a design-scaled virtual resolution: a fixed <paramref name="baseWidth"/> design space
    /// scaled to fill the window width (height adapts), the same behaviour mobile uses. Use this to opt
    /// a <b>desktop</b> game into design-scaling so it presents the same fixed design space as mobile
    /// instead of raw back-buffer pixels. The result is still a desktop <see cref="InputManager"/>
    /// (pass <c>isMobile:false</c> there); only the scaling differs.
    /// </summary>
    public static VirtualResolution DesignScaled(
        GraphicsDeviceManager? graphicsDeviceManager,
        int baseWidth = 440,
        int referenceHeight = 956) =>
        new(graphicsDeviceManager, isMobile: true, baseWidth, referenceHeight);

    /// <summary>
    /// Recomputes virtual size and scale from the current back-buffer size. Call on startup and
    /// whenever the window size changes. No-op if no <c>GraphicsDeviceManager</c> was supplied; in
    /// that case drive the size with <see cref="Configure"/> instead.
    /// </summary>
    public void Initialize()
    {
        if (_graphicsDeviceManager?.GraphicsDevice is not GraphicsDevice graphicsDevice) return;
        Configure(graphicsDevice.PresentationParameters.BackBufferWidth,
                  graphicsDevice.PresentationParameters.BackBufferHeight);
    }

    /// <summary>
    /// Recomputes virtual size and scale from an explicit screen (back-buffer) size. <see cref="Initialize"/>
    /// calls this with the live back-buffer; callers can drive it directly from a known size (e.g. a fixed
    /// configuration, or a headless test with no <c>GraphicsDevice</c>). Ignores non-positive sizes.
    /// </summary>
    public void Configure(int screenWidth, int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0) return;

        if (_designScaled)
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
