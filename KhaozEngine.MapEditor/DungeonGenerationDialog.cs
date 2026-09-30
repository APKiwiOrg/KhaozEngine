using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Dungeon;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.MapEditor;

/// <summary>Modal editor form for one deterministic dungeon bake. It edits a private copy of the game's preset
/// and returns validated inputs without changing the open map.</summary>
[LocalizationExempt]
internal sealed class DungeonGenerationDialog
{
    private const float CardWidth = 490f;
    private const float TitleHeight = 36f;
    private const float FooterHeight = 48f;
    private const float ErrorHeight = 54f;
    private const float Padding = 12f;
    private const float ButtonWidth = 128f;
    private const float ButtonHeight = 30f;
    private const float ButtonGap = 8f;
    private const float MaxHeightFraction = 0.92f;

    private static readonly Color ScrimColor = new(0f, 0f, 0f, 0.6f);
    private static readonly Color CardColor = new(0.075f, 0.082f, 0.12f, 0.98f);
    private static readonly Color TitleColor = new(0.115f, 0.12f, 0.165f, 1f);
    private static readonly Color TextColor = new(0.92f, 0.94f, 0.98f, 1f);
    private static readonly Color ErrorColor = new(1f, 0.42f, 0.38f, 1f);

    private readonly PropertyGrid _grid = new(default) { EditorStyle = GuiStyle.Modern, LabelFraction = 0.53f };
    private readonly Button _generate;
    private readonly Button _cancel;

    public DungeonGenerationDialog(DungeonConfig preset, Vector3 centre)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Draft = DungeonJson.LoadConfig(DungeonJson.SaveConfig(preset));
        OriginX = centre.X - Draft.PlotWidthTiles * Draft.CellSizeMeters * 0.5f;
        OriginZ = centre.Z - Draft.PlotDepthTiles * Draft.CellSizeMeters * 0.5f;
        BaseY = centre.Y;
        SeedText = "0";

        BuildRows();
        _generate = new Button(default, LocalizedText.Raw("Generate"), null!, () => GenerateRequested = true)
        {
            Style = GuiStyle.Modern,
        };
        _cancel = new Button(default, LocalizedText.Raw("Cancel"), null!, () => CloseRequested = true)
        {
            Style = GuiStyle.Modern,
        };
    }

    public DungeonConfig Draft { get; }
    public string SeedText { get; set; }
    public float OriginX { get; set; }
    public float OriginZ { get; set; }
    public float BaseY { get; set; }
    public float YawDegrees { get; set; }
    public string Error { get; private set; } = "";
    public bool CloseRequested { get; private set; }
    public bool GenerateRequested { get; private set; }
    public PropertyGrid Grid => _grid;
    public Button GenerateButton => _generate;
    public Button CancelButton => _cancel;

    /// <summary>Returns one Generate click and clears it, so a failed submit stays open without retrying each frame.</summary>
    public bool ConsumeGenerateRequest()
    {
        bool requested = GenerateRequested;
        GenerateRequested = false;
        return requested;
    }

    public void ShowError(string message) => Error = message ?? "";

    /// <summary>Builds independent, validated command inputs from the current form values.</summary>
    public bool TryBuild(out DungeonConfig config, out ulong seed, out DungeonPlotTransform plot)
    {
        config = null!;
        plot = default;
        if (!ulong.TryParse(SeedText, NumberStyles.None, CultureInfo.InvariantCulture, out seed))
        {
            Error = "Seed must be an unsigned 64-bit integer.";
            return false;
        }

        try
        {
            config = DungeonJson.LoadConfig(DungeonJson.SaveConfig(Draft));
        }
        catch (DungeonJsonException ex)
        {
            Error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            Error = "Generator settings invalid. " + ex.Message;
            return false;
        }

        plot = new DungeonPlotTransform(OriginX, OriginZ, BaseY, YawDegrees * MathF.PI / 180f);
        if (!float.IsFinite(plot.OriginX) || !float.IsFinite(plot.OriginZ) ||
            !float.IsFinite(plot.BaseY) || !float.IsFinite(plot.YawRadians))
        {
            Error = "Plot coordinates and yaw must be finite.";
            return false;
        }

        Error = "";
        return true;
    }

    public void HandleKeys(InputState input)
    {
        if (input.WasPressed(Key.Escape) && !_grid.HasActiveEditor) CloseRequested = true;
    }

    public void Update(InputManager input, Vector2 viewport, float dt)
    {
        ArgumentNullException.ThrowIfNull(input);
        Rect card = CardRect(viewport);
        input.BlockInputRegion(new Rect(0f, 0f, viewport.X, viewport.Y));
        _grid.Bounds = GridRect(card);
        _grid.Update(input, dt);

        (Rect generate, Rect cancel) = FooterRects(card);
        _generate.Bounds = generate;
        _generate.Update(input.Pointer);
        _cancel.Bounds = cancel;
        _cancel.Update(input.Pointer);
    }

    public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Vector2 viewport)
    {
        if (batch is null || white is null || font is null) return;
        Rect card = CardRect(viewport);
        batch.Draw(white, new Vector4(0f, 0f, viewport.X, viewport.Y), ScrimColor);
        batch.DrawRounded(white, new Vector4(card.X, card.Y, card.Width, card.Height), CardColor,
            GuiStyle.Modern.CornerRadius);
        batch.DrawRounded(white, new Vector4(card.X, card.Y, card.Width, TitleHeight), TitleColor,
            GuiStyle.Modern.CornerRadius);
        TextLayout.DrawAligned(batch, font, "Generate dungeon", card.X, card.Width,
            card.Y + (TitleHeight - font.LineHeight) * 0.5f, TextAlign.Center, TextColor);

        _grid.Bounds = GridRect(card);
        _grid.Draw(batch, white, font);
        if (Error.Length > 0)
            TextLayout.DrawWrapped(batch, font, Error,
                new Vector2(card.X + Padding, card.Bottom - FooterHeight - ErrorHeight + Padding * 0.5f),
                card.Width - Padding * 2f, TextAlign.Left, ErrorColor);

        (Rect generate, Rect cancel) = FooterRects(card);
        _generate.Bounds = generate;
        _generate.Font = font;
        _generate.Draw(batch, white);
        _cancel.Bounds = cancel;
        _cancel.Font = font;
        _cancel.Draw(batch, white);
    }

    private Rect CardRect(Vector2 viewport)
    {
        float width = MathF.Min(CardWidth, MathF.Max(viewport.X - Padding * 2f, 0f));
        float wanted = TitleHeight + _grid.ContentHeight + Padding * 2f + ErrorHeight + FooterHeight;
        float height = MathF.Min(wanted, MathF.Max(viewport.Y * MaxHeightFraction, 0f));
        return new Rect(MathF.Floor((viewport.X - width) * 0.5f), MathF.Floor((viewport.Y - height) * 0.5f),
            width, height);
    }

    private static Rect GridRect(Rect card) => new(
        card.X + Padding,
        card.Y + TitleHeight + Padding,
        MathF.Max(card.Width - Padding * 2f, 0f),
        MathF.Max(card.Height - TitleHeight - FooterHeight - ErrorHeight - Padding * 2f, 0f));

    private static (Rect Generate, Rect Cancel) FooterRects(Rect card)
    {
        float y = card.Bottom - FooterHeight + (FooterHeight - ButtonHeight) * 0.5f;
        float cancelX = card.Right - Padding - ButtonWidth;
        return (new Rect(cancelX - ButtonGap - ButtonWidth, y, ButtonWidth, ButtonHeight),
                new Rect(cancelX, y, ButtonWidth, ButtonHeight));
    }

    private void BuildRows()
    {
        _grid.Rows.Add(new HeaderRow(LocalizedText.Raw("Reproduction")));
        _grid.Rows.Add(new TextRow(LocalizedText.Raw("Seed"), () => SeedText, v => SeedText = v,
            maxLength: 20));

        _grid.Rows.Add(new HeaderRow(LocalizedText.Raw("Placement")));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Origin X"), () => OriginX, v => OriginX = v));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Origin Z"), () => OriginZ, v => OriginZ = v));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Base Y"), () => BaseY, v => BaseY = v));
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw("Yaw degrees"), () => YawDegrees, v => YawDegrees = v,
            dragScale: 0.5f, decimals: 1));

        _grid.Rows.Add(new HeaderRow(LocalizedText.Raw("Main layout")));
        AddIntRow("Plot width tiles", () => Draft.PlotWidthTiles, v => Draft.PlotWidthTiles = v);
        AddIntRow("Plot depth tiles", () => Draft.PlotDepthTiles, v => Draft.PlotDepthTiles = v);
        AddIntRow("Room count target", () => Draft.RoomCountTarget, v => Draft.RoomCountTarget = v);
        AddIntRow("Max floors", () => Draft.MaxFloors, v => Draft.MaxFloors = v);
        AddIntRow("Corridor min width", () => Draft.CorridorMinWidth, v => Draft.CorridorMinWidth = v);
        AddIntRow("Corridor max width", () => Draft.CorridorMaxWidth, v => Draft.CorridorMaxWidth = v);
        _grid.Rows.Add(new ChoiceRow(LocalizedText.Raw("Ceiling mode"), new[] { "Open", "Roofed" },
            () => Draft.CeilingMode.ToString(),
            v => Draft.CeilingMode = Enum.Parse<DungeonCeilingMode>(v)));
    }

    private void AddIntRow(string label, Func<int> get, Action<int> set) =>
        _grid.Rows.Add(new FloatRow(LocalizedText.Raw(label), () => get(),
            v => set((int)MathF.Round(v)), min: 0f, max: 4096f, dragScale: 1f, decimals: 0));
}
