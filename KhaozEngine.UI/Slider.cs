using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Reusable horizontal slider component. Renders a label on the left,
/// a percentage value on the right, and a draggable track in between.
/// Tap anywhere on the track to jump; drag the thumb to adjust.
///
/// Usage:
/// 1. Create with font, renderer, input
/// 2. Call <see cref="Update"/> each frame with row bounds
/// 3. Check <see cref="WasChanged"/> or read <see cref="Value"/>
/// 4. Call <see cref="Draw"/> to render with a label string
/// </summary>
public sealed class Slider
{
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _font;

    private const int TrackHeight = 10;
    private const int ThumbWidth = 10;
    private const int ThumbHeight = 16;
    private const int TrackRightPad = 40; // space for percentage text

    private bool _isDragging;

    /// <summary>Current value between 0.0 and 1.0.</summary>
    public float Value { get; set; } = 1.0f;

    /// <summary>Whether the slider is interactive.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True on the frame the value changed via interaction.</summary>
    public bool WasChanged { get; private set; }

    /// <summary>
    /// Creates a new Slider.
    /// </summary>
    public Slider(SpriteFont font, PrimitiveRenderer renderer, InputManager input)
    {
        _font = font;
        _renderer = renderer;
        _input = input;
    }

    /// <summary>
    /// Updates the slider state. Call each frame.
    /// </summary>
    /// <param name="bounds">Full row bounds (label + track area).</param>
    public void Update(Rectangle bounds)
    {
        WasChanged = false;
        if (!Enabled) return;

        Rectangle trackBounds = GetTrackBounds(bounds);

        // Start drag if pointer just pressed inside the track area
        if (_input.IsPointerJustPressed && trackBounds.Contains(_input.PointerPosition))
        {
            _isDragging = true;
        }

        // Continue drag while pointer is held
        if (_isDragging && _input.IsPointerDown)
        {
            float relativeX = _input.PointerPosition.X - trackBounds.X;
            float newValue = MathHelper.Clamp(relativeX / trackBounds.Width, 0f, 1f);
            if (newValue != Value)
            {
                Value = newValue;
                WasChanged = true;
            }
        }

        // End drag on release
        if (_isDragging && !_input.IsPointerDown)
        {
            _isDragging = false;
        }
    }

    /// <summary>
    /// Draws the slider with a label on the left and percentage on the right.
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

        // Track
        Rectangle trackBounds = GetTrackBounds(bounds);
        int trackY = bounds.Y + (bounds.Height - TrackHeight) / 2;
        var trackRect = new Rectangle(trackBounds.X, trackY, trackBounds.Width, TrackHeight);

        Color trackBg = Enabled
            ? new Color(30, 30, 40) * alpha
            : new Color(20, 20, 25) * alpha;
        Color trackBorder = Enabled
            ? new Color(55, 55, 65) * alpha
            : new Color(35, 35, 42) * alpha;
        _renderer.DrawFilledRect(spriteBatch, trackRect, trackBg);
        _renderer.DrawRect(spriteBatch, trackRect, trackBorder, 1);

        // Filled portion
        int fillWidth = (int)(trackBounds.Width * Value);
        if (fillWidth > 0)
        {
            Color fillColor = Enabled
                ? new Color(40, 100, 180) * alpha
                : new Color(25, 50, 90) * alpha;
            _renderer.DrawFilledRect(spriteBatch,
                new Rectangle(trackBounds.X, trackY, fillWidth, TrackHeight), fillColor);
        }

        // Thumb
        int thumbX = trackBounds.X + fillWidth - ThumbWidth / 2;
        int thumbY = bounds.Y + (bounds.Height - ThumbHeight) / 2;
        Color thumbColor = Enabled
            ? (_isDragging ? new Color(100, 180, 255) * alpha : Color.White * alpha)
            : new Color(80, 80, 90) * alpha;
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(thumbX, thumbY, ThumbWidth, ThumbHeight), thumbColor);

        // Percentage text on right
        int pct = (int)(Value * 100);
        string pctText = $"{pct}%";
        int textX = trackBounds.Right + 6;
        int textY = bounds.Y + (bounds.Height - _font.LineSpacing) / 2;
        TextHelper.Draw(spriteBatch, _font, pctText, textX, textY,
            Enabled ? new Color(160, 165, 175) * alpha : new Color(70, 70, 80) * alpha);
    }

    private Rectangle GetTrackBounds(Rectangle bounds)
    {
        // Track sits in the right portion of the row, leaving space for label and percentage
        int trackWidth = bounds.Width / 2;
        int trackX = bounds.Right - trackWidth - TrackRightPad;
        return new Rectangle(trackX, bounds.Y, trackWidth, bounds.Height);
    }
}
