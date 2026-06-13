using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Reusable toggle switch component. Renders a label on the left and a
/// sliding on/off track with thumb on the right. Tap anywhere on the row
/// bounds to toggle.
///
/// Usage:
/// 1. Create with font, renderer, input
/// 2. Call <see cref="Update"/> each frame with row bounds
/// 3. Check <see cref="WasToggled"/> or read <see cref="IsOn"/>
/// 4. Call <see cref="Draw"/> to render with a label string
/// </summary>
public sealed class Toggle
{
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _font;

    private const int TrackWidth = 36;
    private const int TrackHeight = 18;
    private const int ThumbSize = 14;

    /// <summary>Whether the toggle is currently on.</summary>
    public bool IsOn { get; set; }

    /// <summary>Whether the toggle is interactive.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True on the frame the toggle was clicked (state changed).</summary>
    public bool WasToggled { get; private set; }

    /// <summary>
    /// Creates a new Toggle.
    /// </summary>
    public Toggle(SpriteFont font, PrimitiveRenderer renderer, InputManager input)
    {
        _font = font;
        _renderer = renderer;
        _input = input;
    }

    /// <summary>
    /// Updates the toggle state. Call each frame.
    /// </summary>
    /// <param name="bounds">Full row bounds (label + toggle area).</param>
    public void Update(Rectangle bounds)
    {
        WasToggled = false;
        if (!Enabled) return;

        if (_input.IsTapIn(bounds))
        {
            IsOn = !IsOn;
            WasToggled = true;
        }
    }

    /// <summary>
    /// Draws the toggle with a label on the left and switch on the right.
    /// Must be inside an active SpriteBatch.
    /// </summary>
    public void Draw(SpriteBatch spriteBatch, Rectangle bounds, string label, float alpha = 1f)
    {
        // Label on left
        Color labelColor = Enabled
            ? new Color(200, 205, 215) * alpha
            : new Color(70, 70, 80) * alpha;
        int labelY = bounds.Y + (bounds.Height - _font.LineSpacing) / 2;
        TextHelper.Draw(spriteBatch, _font, label, bounds.X, labelY, labelColor);

        // Track on right
        int trackX = bounds.Right - TrackWidth - 4;
        int trackY = bounds.Y + (bounds.Height - TrackHeight) / 2;

        Color trackColor = IsOn
            ? new Color(40, 100, 180) * alpha
            : new Color(40, 40, 50) * alpha;
        Color trackBorder = IsOn
            ? new Color(60, 130, 220) * alpha
            : new Color(55, 55, 65) * alpha;

        if (!Enabled)
        {
            trackColor = new Color(25, 25, 30) * alpha;
            trackBorder = new Color(35, 35, 42) * alpha;
        }

        _renderer.DrawFilledRect(spriteBatch, new Rectangle(trackX, trackY, TrackWidth, TrackHeight), trackColor);
        _renderer.DrawRect(spriteBatch, new Rectangle(trackX, trackY, TrackWidth, TrackHeight), trackBorder, 1);

        // Thumb
        int thumbX = IsOn ? trackX + TrackWidth - ThumbSize - 2 : trackX + 2;
        int thumbY = trackY + (TrackHeight - ThumbSize) / 2;
        Color thumbColor = Enabled
            ? Color.White * alpha
            : new Color(80, 80, 90) * alpha;
        _renderer.DrawFilledRect(spriteBatch, new Rectangle(thumbX, thumbY, ThumbSize, ThumbSize), thumbColor);
    }
}
