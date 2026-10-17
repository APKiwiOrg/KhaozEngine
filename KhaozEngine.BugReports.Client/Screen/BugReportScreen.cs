using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.BugReports.Contracts;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.BugReports.Client;

/// <summary>What <see cref="BugReportScreen"/> draws and measures with.</summary>
/// <param name="Measurer">Lays out the description when <paramref name="Font"/> is null.</param>
/// <param name="Font">The UI font, or null to update headlessly. Text is not drawn without it.</param>
/// <param name="White">A 1x1 white texture, or null to update headlessly. Nothing is drawn without it.</param>
/// <param name="Viewport">The design viewport the screen lays out in and draws through.</param>
public sealed record BugReportScreenResources(ITextMeasurer Measurer, SpriteFont? Font, Texture2D? White, IDesignViewport Viewport);

/// <summary>Where <see cref="BugReportScreen"/> stands: still open, or closed by Send or Cancel.</summary>
public enum BugReportScreenResult
{
    Open,
    Send,
    Cancel,
}

/// <summary>
/// The modal confirmation screen a player sees after the report trigger: a description box, a category selector,
/// screenshot and clip toggles, the thumbnails and Send and Cancel. Widget state lives here and the rules live in
/// <see cref="Model"/>, which this screen updates every frame.
/// <para>The description is focused on open. Escape closes an open category list, otherwise it cancels. Ctrl or Cmd
/// plus Enter sends when <see cref="BugReportScreenModel.CanSend"/>, and the Send button is disabled until then. The
/// screen acts only on press edges, so a trigger key still held from the frame that opened it does nothing.</para>
/// <para>Closing sets <see cref="Result"/> and raises <see cref="Closed"/> once. The screen does not remove itself.
/// Its owner pops it. Every player-facing line resolves through <see cref="BugReportStrings"/> each frame. Frame
/// thread only.</para>
/// </summary>
public sealed class BugReportScreen : Screen
{
    const int ScreenDrawOrder = 20000;
    const float TextMargin = 20f;

    static readonly Vector4 ScrimColor = new(0f, 0f, 0f, 0.6f);

    readonly BugReportScreenResources _resources;
    readonly TextArea _description;
    readonly Dropdown _category;
    readonly Toggle _screenshot;
    readonly Toggle _clip;
    readonly Button _send;
    readonly Button _cancel;
    Texture2D? _thumbnail;
    IReadOnlyList<Texture2D> _strip = Array.Empty<Texture2D>();
    bool _thumbnailsSet;

    /// <summary>Creates the screen over <paramref name="model"/>. Add it to a <see cref="ScreenStack"/> to show it.</summary>
    public BugReportScreen(BugReportScreenModel model, BugReportScreenResources resources)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(resources.Measurer);
        ArgumentNullException.ThrowIfNull(resources.Viewport);
        Model = model;
        _resources = resources;
        DrawOrder = ScreenDrawOrder;
        PassUpdateThrough = false;
        Layout = BugReportScreenLayout.Compute(resources.Viewport.WindowBounds);

        _description = new TextArea(Layout.Description, resources.Measurer, resources.Font)
        {
            MaxLength = BugReportLimits.MaxDescriptionChars,
        };
        _description.SetText(model.Description);
        _description.Focus();

        var options = new DropdownOption[model.Categories.Count];
        for (int i = 0; i < options.Length; i++)
            options[i] = new DropdownOption(LocalizedText.Raw(BugReportStrings.CategoryLabel(model.Categories[i])), i);
        _category = new Dropdown(options, Layout.Category) { ShowChevron = true };
        _category.SelectByValue(model.CategoryIndex);

        _screenshot = new Toggle(Layout.ScreenshotToggle, model.IncludeScreenshot) { Enabled = model.ScreenshotAvailable };
        _clip = new Toggle(Layout.ClipToggle, model.IncludeClip) { Enabled = model.ClipAvailable };

        // Button only reads its font in Draw, which assigns the real one first and is skipped without one.
        _send = new Button(Layout.Send, default, null!, () => Close(BugReportScreenResult.Send));
        _cancel = new Button(Layout.Cancel, default, null!, () => Close(BugReportScreenResult.Cancel));
        _send.Enabled = model.CanSend;
        RefreshText();
    }

    /// <summary>The rules and the player's choices.</summary>
    public BugReportScreenModel Model { get; }

    /// <summary><see cref="BugReportScreenResult.Open"/> until Send or Cancel closes the screen.</summary>
    public BugReportScreenResult Result { get; private set; }

    /// <summary>Raised once, on the frame Send or Cancel closes the screen.</summary>
    public event Action<BugReportScreenResult>? Closed;

    /// <summary>The current layout, recomputed from the viewport every update.</summary>
    internal BugReportScreenLayout Layout { get; private set; }

    internal TextArea DescriptionArea => _description;

    internal Dropdown CategoryDropdown => _category;

    internal Toggle ScreenshotToggle => _screenshot;

    internal Toggle ClipToggle => _clip;

    internal Button SendButton => _send;

    /// <summary>
    /// Shows the decoded thumbnails in place of the Capturing line. <paramref name="screenshot"/> is null when there
    /// is no screenshot thumbnail. The caller keeps ownership of the textures and disposes them after the screen is
    /// gone.
    /// </summary>
    public void SetThumbnails(Texture2D? screenshot, IReadOnlyList<Texture2D> strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        _thumbnail = screenshot;
        _strip = strip;
        _thumbnailsSet = true;
    }

    /// <summary>The resolved Included line, plus the SignedOut line when nobody is signed in.</summary>
    internal IReadOnlyList<string> InfoLines()
    {
        var lines = new string[InfoLineCount];
        for (int i = 0; i < lines.Length; i++) lines[i] = InfoLine(i);
        return lines;
    }

    int InfoLineCount => Model.SignedIn ? 1 : 2;

    static string InfoLine(int index) =>
        BugReportStrings.Resolve(index == 0 ? BugReportStrings.Included : BugReportStrings.SignedOut);

    public override bool Update(float dt, bool receivesInput)
    {
        if (!receivesInput) return false;
        if (Result != BugReportScreenResult.Open) return true;

        ApplyLayout();
        RefreshText();
        InputState input = Manager.Input;

        if (input.WasPressed(Key.Escape))
        {
            if (_category.IsOpen) _category.Close();
            else Close(BugReportScreenResult.Cancel);
            return true;
        }

        if (_category.IsOpen)
        {
            // The open list covers the widgets under it, so it alone takes this frame's input.
            _category.Update(Manager.InputManager, focused: true);
            Model.CategoryIndex = _category.SelectedIndex;
            return true;
        }

        _description.Update(Manager.Pointer, input, dt);
        if (_description.TextChanged) Model.Description = _description.Text;

        _category.Update(Manager.Pointer);
        Model.CategoryIndex = _category.SelectedIndex;

        UpdateToggle(_screenshot, Model.ScreenshotAvailable, Model.IncludeScreenshot);
        Model.IncludeScreenshot = _screenshot.IsOn;
        UpdateToggle(_clip, Model.ClipAvailable, Model.IncludeClip);
        Model.IncludeClip = _clip.IsOn;

        _send.Enabled = Model.CanSend;
        _cancel.Update(Manager.Pointer);
        _send.Update(Manager.Pointer);

        if (Result == BugReportScreenResult.Open && Model.CanSend && input.IsCommandDown && input.WasPressed(Key.Enter))
            Close(BugReportScreenResult.Send);
        return true;
    }

    void UpdateToggle(Toggle toggle, bool available, bool included)
    {
        // The model has the final say, so a screenshot the flow marks unavailable turns its toggle off.
        toggle.Enabled = available;
        toggle.IsOn = available && included;
        toggle.Update(Manager.Pointer);
    }

    void Close(BugReportScreenResult result)
    {
        if (Result != BugReportScreenResult.Open) return;
        Result = result;
        Closed?.Invoke(result);
    }

    void ApplyLayout()
    {
        Layout = BugReportScreenLayout.Compute(_resources.Viewport.WindowBounds);
        float scale = Layout.Scale;
        _description.Bounds = Layout.Description;
        _description.TextScale = scale;
        _category.TriggerBounds = Layout.Category;
        _category.TextScale = scale;
        _screenshot.Bounds = Layout.ScreenshotToggle;
        _clip.Bounds = Layout.ClipToggle;
        _send.Bounds = Layout.Send;
        _send.LabelScale = scale;
        _cancel.Bounds = Layout.Cancel;
        _cancel.LabelScale = scale;
    }

    void RefreshText()
    {
        // Resolved through the English fallback here, because the widgets resolve against the wired catalog only.
        _description.PlaceholderContent = LocalizedText.Raw(BugReportStrings.Resolve(BugReportStrings.DescriptionPlaceholder));
        _send.Content = LocalizedText.Raw(BugReportStrings.Resolve(BugReportStrings.Send));
        _cancel.Content = LocalizedText.Raw(BugReportStrings.Resolve(BugReportStrings.Cancel));
    }

    public override void Draw(SpriteBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (_resources.White is not { } white) return;
        SpriteFont? font = _resources.Font;
        BugReportScreenLayout layout = Layout;
        GuiTheme theme = GuiTheme.Default;

        GuiDraw.Fill(batch, white, _resources.Viewport.WindowBounds, ScrimColor);
        GuiDraw.Fill(batch, white, layout.Panel, theme.Background);
        GuiDraw.Border(batch, white, layout.Panel, 1f, theme.Border);
        DrawThumbnails(batch, white, font, layout, theme);

        _description.Font = font;
        _description.Draw(batch, white);
        _screenshot.Draw(batch, white);
        _clip.Draw(batch, white);
        if (font is null) return;

        DrawLabels(batch, font, layout, theme);
        _category.Draw(batch, white, font);
        _send.Font = font;
        _cancel.Font = font;
        _cancel.Draw(batch, white);
        _send.Draw(batch, white);
        _category.DrawOverlay(batch, white, font, Manager.Pointer);
    }

    void DrawThumbnails(SpriteBatch batch, Texture2D white, SpriteFont? font, BugReportScreenLayout layout, GuiTheme theme)
    {
        GuiDraw.Fill(batch, white, layout.Thumbnail, theme.Surface);
        GuiDraw.Fill(batch, white, layout.Strip, theme.Surface);
        if (!_thumbnailsSet)
        {
            if (font is not null)
                DrawText(batch, font, BugReportStrings.Resolve(BugReportStrings.Capturing),
                    new Vector2(layout.Thumbnail.X + 8f * layout.Scale, layout.Thumbnail.Y + 8f * layout.Scale),
                    theme.TextMuted, layout.Scale);
            return;
        }

        if (_thumbnail is { } shot) batch.Draw(shot, Fit(layout.Thumbnail, shot.Width, shot.Height), Color.White);
        int count = _strip.Count;
        if (count == 0) return;
        float gap = 8f * layout.Scale;
        float slot = (layout.Strip.Width - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++)
        {
            var cell = new Rect(layout.Strip.X + i * (slot + gap), layout.Strip.Y, slot, layout.Strip.Height);
            Texture2D frame = _strip[i];
            batch.Draw(frame, Fit(cell, frame.Width, frame.Height), Color.White);
        }
    }

    void DrawLabels(SpriteBatch batch, SpriteFont font, BugReportScreenLayout layout, GuiTheme theme)
    {
        float scale = layout.Scale;
        DrawText(batch, font, BugReportStrings.Resolve(BugReportStrings.Title), layout.At(TextMargin, 14f), theme.Text, scale);
        DrawText(batch, font, BugReportStrings.Resolve(BugReportStrings.CategoryFieldLabel), layout.At(TextMargin, 208f), theme.Text, scale);
        DrawText(batch, font, BugReportStrings.Resolve(BugReportStrings.DescriptionLabel), layout.At(TextMargin, 242f), theme.Text, scale);

        string counter = BugReportStrings.Format(BugReportStrings.DescriptionCounter,
            Model.Description.Length, BugReportLimits.MaxDescriptionChars);
        float counterWidth = font.Measure(counter).X * scale;
        DrawText(batch, font, counter,
            new Vector2(layout.Description.Right - counterWidth, layout.Description.Bottom + 4f * scale), theme.TextMuted, scale);

        DrawText(batch, font, BugReportStrings.Resolve(BugReportStrings.ScreenshotToggle), layout.At(70f, 408f),
            Model.ScreenshotAvailable ? theme.Text : theme.TextDisabled, scale);
        DrawText(batch, font, BugReportStrings.Resolve(BugReportStrings.ClipToggle), layout.At(70f, 434f),
            Model.ClipAvailable ? theme.Text : theme.TextDisabled, scale);
        if (!Model.ClipAvailable)
            DrawText(batch, font, BugReportStrings.Resolve(BugReportStrings.ClipUnavailable), layout.At(70f, 456f), theme.TextMuted, scale);

        for (int i = 0; i < InfoLineCount; i++)
            DrawText(batch, font, InfoLine(i), layout.At(TextMargin, 478f + 18f * i), theme.TextMuted, scale);
    }

    static void DrawText(SpriteBatch batch, SpriteFont font, string text, Vector2 position, Vector4 color, float scale) =>
        batch.DrawString(font, text, new Vector2(MathF.Floor(position.X), MathF.Floor(position.Y)), (Color)color, scale);

    /// <summary>The largest rect with the texture's aspect ratio centred in <paramref name="area"/>.</summary>
    static Rect Fit(Rect area, int width, int height)
    {
        if (width <= 0 || height <= 0) return area;
        float scale = MathF.Min(area.Width / width, area.Height / height);
        float w = width * scale, h = height * scale;
        return new Rect(area.X + (area.Width - w) * 0.5f, area.Y + (area.Height - h) * 0.5f, w, h);
    }
}
