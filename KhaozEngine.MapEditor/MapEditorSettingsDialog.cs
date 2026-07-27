using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>
/// The editor's modal settings menu: a scrim, a centred card, and a <see cref="PropertyGrid"/> of live rows over
/// one <see cref="EditorSettings"/> instance, plus a Reset and a Close action. Bare Escape opens it (see
/// <see cref="MapEditorScene"/>'s shortcut handler) and Escape or Close dismisses it. Every row writes straight
/// into the settings instance and raises <c>onChanged</c>, so a change takes effect on the next frame and is
/// persisted immediately rather than on close.
/// <para>Built on <see cref="PropertyGrid"/> rather than <see cref="PopupPanel"/>: the exit dialog's popup carries
/// label / value rows plus footer buttons and has no interactive row type, while this menu is ten editable rows,
/// which is exactly what the property grid the inspector already uses is for.</para>
/// <para>Developer tooling, so the whole class is
/// <see cref="LocalizationExemptAttribute">localization-exempt</see> and its labels are raw.</para>
/// </summary>
[LocalizationExempt]
internal sealed class MapEditorSettingsDialog
{
    /// <summary>Card width in points, before the viewport clamp.</summary>
    const float CardWidth = 460f;
    const float TitleBarHeight = 34f;
    const float FooterHeight = 46f;
    const float Padding = 12f;
    const float ButtonWidth = 148f;
    const float ButtonHeight = 30f;
    const float ButtonGap = 8f;
    /// <summary>Ceiling on the card height as a fraction of the viewport, so a short window scrolls the grid rather
    /// than pushing the footer off screen.</summary>
    const float MaxHeightFraction = 0.92f;

    static readonly Color ScrimColor = new(0f, 0f, 0f, 0.6f);
    static readonly Color CardColor = new(0.075f, 0.082f, 0.12f, 0.98f);
    static readonly Color TitleBarColor = new(0.115f, 0.12f, 0.165f, 1f);
    static readonly Color TitleTextColor = new(0.92f, 0.94f, 0.98f, 1f);
    static readonly float CornerRadius = GuiStyle.Modern.CornerRadius;

    readonly EditorSettings _settings;
    readonly Action _onChanged;
    readonly PropertyGrid _grid = new(default) { EditorStyle = GuiStyle.Modern, LabelFraction = 0.52f };
    readonly Button _reset;
    readonly Button _close;

    /// <summary>Builds the rows over <paramref name="settings"/>. <paramref name="onChanged"/> fires once on any
    /// frame a row (or Reset) changed a value, which is where the host persists and re-applies.</summary>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public MapEditorSettingsDialog(EditorSettings settings, Action onChanged)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));

        BuildRows();

        // Fonts and bounds are assigned per frame (no SpriteFont resolves at construction, the toolbar Save-button
        // pattern in MapEditorScene.BuildChrome).
        _reset = new Button(default, LocalizedText.Raw("Reset to defaults"), null!, ResetToDefaults)
        {
            Style = GuiStyle.Modern,
        };
        _close = new Button(default, LocalizedText.Raw("Close"), null!, () => CloseRequested = true)
        {
            Style = GuiStyle.Modern,
        };
    }

    /// <summary>True once Escape or the Close button asked to dismiss the menu. The host reads it after
    /// <see cref="Update"/> and drops its reference.</summary>
    public bool CloseRequested { get; private set; }

    /// <summary>The row grid. Exposed so a test can drive or assert an individual row without a live viewport.</summary>
    public PropertyGrid Grid => _grid;

    /// <summary>The Reset action button. Exposed for tests.</summary>
    public Button ResetButton => _reset;

    /// <summary>The Close action button. Exposed for tests.</summary>
    public Button CloseButton => _close;

    /// <summary>The menu label for a render-distance multiplier: the head's configured profile reads "Base", and
    /// every larger tier reads as its factor. Shared by the row and its tests so neither can drift.</summary>
    public static string MultiplierLabel(float multiplier) => multiplier <= 1f
        ? "Base"
        : multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x";

    /// <summary>Keyboard step: Escape dismisses the menu, unless a row owns a live edit (a NumberField mid-type),
    /// in which case that row's own Escape cancel takes the keypress first and the menu stays open. Run BEFORE
    /// <see cref="Update"/> so the row is still holding its edit when this decides.</summary>
    public void HandleKeys(InputState input)
    {
        if (input.WasPressed(Key.Escape) && !_grid.HasActiveEditor) CloseRequested = true;
    }

    /// <summary>Pointer + widget step over a <paramref name="viewport"/>-sized design space. Fires the change hook
    /// once on any frame a row wrote a new value.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public void Update(InputManager input, Vector2 viewport, float dt)
    {
        ArgumentNullException.ThrowIfNull(input);
        Rect card = CardRect(viewport);
        // Reserve the whole scrim, not just the card: the menu is modal, so a click anywhere is the menu's, and
        // nothing beneath it may treat a miss as a viewport click.
        input.BlockInputRegion(new Rect(0f, 0f, viewport.X, viewport.Y));

        _grid.Bounds = ContentRect(card);
        bool changed = _grid.Update(input, dt);

        (Rect resetRect, Rect closeRect) = FooterRects(card);
        _reset.Bounds = resetRect;
        _reset.Update(input.Pointer);
        _close.Bounds = closeRect;
        _close.Update(input.Pointer);

        if (changed) _onChanged();
    }

    /// <summary>Draws the scrim, the card, the title, the rows, and the footer buttons.</summary>
    public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Vector2 viewport)
    {
        if (batch is null || white is null || font is null) return;
        Rect card = CardRect(viewport);

        batch.Draw(white, new Vector4(0f, 0f, viewport.X, viewport.Y), ScrimColor);
        batch.DrawRounded(white, new Vector4(card.X, card.Y, card.Width, card.Height), CardColor, CornerRadius);
        batch.DrawRounded(white, new Vector4(card.X, card.Y, card.Width, TitleBarHeight), TitleBarColor, CornerRadius);
        TextLayout.DrawAligned(batch, font, "Editor settings", card.X, card.Width,
            card.Y + (TitleBarHeight - font.LineHeight) * 0.5f, TextAlign.Center, TitleTextColor);

        _grid.Bounds = ContentRect(card);
        _grid.Draw(batch, white, font);

        (Rect resetRect, Rect closeRect) = FooterRects(card);
        _reset.Bounds = resetRect;
        _reset.Font = font;
        _reset.Draw(batch, white);
        _close.Bounds = closeRect;
        _close.Font = font;
        _close.Draw(batch, white);
    }

    /// <summary>The centred card rect for a <paramref name="viewport"/>-sized design space: as tall as its rows
    /// need, capped at <see cref="MaxHeightFraction"/> of the viewport (the grid scrolls past that).</summary>
    public Rect CardRect(Vector2 viewport)
    {
        float width = MathF.Min(CardWidth, MathF.Max(viewport.X - Padding * 2f, 0f));
        float wanted = TitleBarHeight + _grid.ContentHeight + Padding * 2f + FooterHeight;
        float height = MathF.Min(wanted, MathF.Max(viewport.Y * MaxHeightFraction, 0f));
        return new Rect(MathF.Floor((viewport.X - width) * 0.5f), MathF.Floor((viewport.Y - height) * 0.5f),
            width, height);
    }

    // The scrolling row region between the title bar and the footer.
    static Rect ContentRect(Rect card) => new(
        card.X + Padding,
        card.Y + TitleBarHeight + Padding,
        MathF.Max(card.Width - Padding * 2f, 0f),
        MathF.Max(card.Height - TitleBarHeight - FooterHeight - Padding, 0f));

    // Footer actions, laid out right to left (Close rightmost, the PopupPanel convention).
    static (Rect Reset, Rect Close) FooterRects(Rect card)
    {
        float y = card.Bottom - FooterHeight + (FooterHeight - ButtonHeight) * 0.5f;
        float closeX = card.Right - Padding - ButtonWidth;
        return (new Rect(closeX - ButtonGap - ButtonWidth, y, ButtonWidth, ButtonHeight),
                new Rect(closeX, y, ButtonWidth, ButtonHeight));
    }

    void ResetToDefaults()
    {
        _settings.ResetToDefaults();
        _onChanged();
    }

    void BuildRows()
    {
        _grid.Rows.Add(new HeaderRow(LocalizedText.Raw("View")));
        _grid.Rows.Add(new ChoiceRow(LocalizedText.Raw("Render distance"), MultiplierLabels(),
            () => MultiplierLabel(_settings.RenderDistanceMultiplier),
            label => _settings.RenderDistanceMultiplier = MultiplierFor(label),
            LocalizedText.Raw("Scales the whole render-distance set (terrain far field, prop cull, camera far " +
                "clip, ocean extent) above the profile this editor was started with. A change rebuilds the " +
                "streamed world, so expect a brief hitch. Editor view only, it never changes the document.")));

        _grid.Rows.Add(new HeaderRow(LocalizedText.Raw("Sky")));
        _grid.Rows.Add(new ChoiceRow(LocalizedText.Raw("Sky preset"), EnumLabels<EnvironmentPresetKind>(),
            () => _settings.Environment.ToString(),
            label => _settings.SelectEnvironment(ParseEnum(label, EditorSettings.DefaultEnvironment)),
            LocalizedText.Raw("Sky gradient, sun disc, and the lighting that goes with it. Picking a preset " +
                "resets the sun and lighting sliders below to that preset's own values.")));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Sun azimuth"),
            () => _settings.SunAzimuthDegrees, v => _settings.SunAzimuthDegrees = v,
            min: 0f, max: 360f, dragScale: 0.5f, decimals: 0,
            LocalizedText.Raw("Compass bearing of the sun in degrees, clockwise from north. Moves the sun disc, " +
                "the key light, and the water glint together.")));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Sun elevation"),
            () => _settings.SunElevationDegrees, v => _settings.SunElevationDegrees = v,
            min: EditorSettings.MinSunElevationDegrees, max: EditorSettings.MaxSunElevationDegrees,
            dragScale: 0.25f, decimals: 0,
            LocalizedText.Raw("Height of the sun above the horizon in degrees. 90 is straight overhead.")));

        _grid.Rows.Add(new HeaderRow(LocalizedText.Raw("Lighting")));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Key light"),
            () => _settings.KeyLightIntensity, v => _settings.KeyLightIntensity = v,
            min: 0f, max: EditorSettings.MaxLightIntensity, dragScale: 0.005f, decimals: 2,
            LocalizedText.Raw("Multiplier on the sky preset's own key-light colour. 1 is the preset value.")));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Ambient"),
            () => _settings.AmbientIntensity, v => _settings.AmbientIntensity = v,
            min: 0f, max: EditorSettings.MaxLightIntensity, dragScale: 0.005f, decimals: 2,
            LocalizedText.Raw("Multiplier on the sky preset's own ambient colour. 1 is the preset value. Raise " +
                "it to read detail in shadowed ground while authoring.")));

        _grid.Rows.Add(new HeaderRow(LocalizedText.Raw("Ocean")));
        _grid.Rows.Add(new ChoiceRow(LocalizedText.Raw("Ocean preset"), EnumLabels<OceanPresetKind>(),
            () => _settings.Ocean.ToString(),
            label => _settings.SelectOcean(ParseEnum(label, EditorSettings.DefaultOcean)),
            LocalizedText.Raw("Swell, ripple, foam, and glint as one bundle. Picking a preset resets the two " +
                "sliders below to that preset's own values.")));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Swell amplitude"),
            () => _settings.SwellAmplitude, v => _settings.SwellAmplitude = v,
            min: 0f, max: EditorSettings.MaxSwellAmplitude, dragScale: 0.005f, decimals: 2,
            LocalizedText.Raw("Height of the rolling swell in metres, on top of the ocean preset.")));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Foam strength"),
            () => _settings.FoamStrength, v => _settings.FoamStrength = v,
            min: 0f, max: EditorSettings.MaxFoamStrength, dragScale: 0.005f, decimals: 2,
            LocalizedText.Raw("How strongly whitecaps show on the wave crests, on top of the ocean preset.")));
        _grid.Rows.Add(new BoolRow(LocalizedText.Raw("Surf"),
            () => _settings.Surf, v => _settings.Surf = v,
            LocalizedText.Raw("Builds a water-depth field from this document's terrain so waves shoal and break " +
                "along the shoreline. Off by default because the depth field is rebuilt with the streamed world, " +
                "so it costs a pass over the document bounds on every rebuild.")));
    }

    static List<string> MultiplierLabels()
    {
        var labels = new List<string>(EditorSettings.RenderDistanceMultipliers.Count);
        foreach (float m in EditorSettings.RenderDistanceMultipliers) labels.Add(MultiplierLabel(m));
        return labels;
    }

    // The multiplier a menu label names, falling back to the base tier for a label from nowhere (the dropdown only
    // ever hands back one of its own options, so this is a guard, not a path).
    static float MultiplierFor(string label)
    {
        foreach (float m in EditorSettings.RenderDistanceMultipliers)
            if (string.Equals(MultiplierLabel(m), label, StringComparison.Ordinal)) return m;
        return EditorSettings.RenderDistanceMultipliers[0];
    }

    static List<string> EnumLabels<TEnum>() where TEnum : struct, Enum
    {
        TEnum[] values = Enum.GetValues<TEnum>();
        var labels = new List<string>(values.Length);
        foreach (TEnum value in values) labels.Add(value.ToString()!);
        return labels;
    }

    static TEnum ParseEnum<TEnum>(string label, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse(label, out TEnum parsed) && Enum.IsDefined(parsed) ? parsed : fallback;
}
