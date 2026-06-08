using Microsoft.Xna.Framework;

namespace KhaozEngine.UI;

/// <summary>
/// Visual style definition for a <see cref="Button"/>. Create themed presets
/// and reuse them across the UI for consistent styling.
/// </summary>
public sealed class ButtonStyle
{
    /// <summary>Background color in normal state.</summary>
    public Color BackgroundNormal { get; init; } = new(35, 55, 90);

    /// <summary>Background color when pointer is hovering.</summary>
    public Color BackgroundHover { get; init; } = new(45, 70, 115);

    /// <summary>Background color when pressed.</summary>
    public Color BackgroundPressed { get; init; } = new(25, 40, 70);

    /// <summary>Background color when disabled.</summary>
    public Color BackgroundDisabled { get; init; } = new(30, 30, 35);

    /// <summary>Border color in normal state.</summary>
    public Color BorderNormal { get; init; } = new(60, 90, 150);

    /// <summary>Border color when hovering.</summary>
    public Color BorderHover { get; init; } = new(80, 120, 190);

    /// <summary>Border color when pressed.</summary>
    public Color BorderPressed { get; init; } = new(50, 75, 130);

    /// <summary>Border color when disabled.</summary>
    public Color BorderDisabled { get; init; } = new(40, 40, 50);

    /// <summary>Text color in normal state.</summary>
    public Color TextNormal { get; init; } = Color.White;

    /// <summary>Text color when hovering.</summary>
    public Color TextHover { get; init; } = new(220, 235, 255);

    /// <summary>Text color when pressed.</summary>
    public Color TextPressed { get; init; } = new(180, 200, 230);

    /// <summary>Text color when disabled.</summary>
    public Color TextDisabled { get; init; } = new(70, 70, 80);

    /// <summary>Border thickness in pixels.</summary>
    public int BorderThickness { get; init; } = 1;

    /// <summary>Standard blue button  -- for primary actions like "Continue", "Buy", "Confirm".</summary>
    public static readonly ButtonStyle Primary = new();

    /// <summary>Subtle dark button  -- for secondary actions, toggles, quantity selectors.</summary>
    public static readonly ButtonStyle Secondary = new()
    {
        BackgroundNormal = new Color(25, 25, 35),
        BackgroundHover = new Color(35, 35, 50),
        BackgroundPressed = new Color(18, 18, 25),
        BorderNormal = new Color(45, 45, 55),
        BorderHover = new Color(60, 65, 80),
        BorderPressed = new Color(35, 35, 45),
        TextNormal = new Color(160, 160, 170),
        TextHover = new Color(200, 200, 210),
        TextPressed = new Color(130, 130, 140)
    };

    /// <summary>Red danger button  -- for destructive actions like "Reset Save".</summary>
    public static readonly ButtonStyle Danger = new()
    {
        BackgroundNormal = new Color(60, 20, 20),
        BackgroundHover = new Color(80, 25, 25),
        BackgroundPressed = new Color(45, 15, 15),
        BackgroundDisabled = new Color(30, 30, 35),
        BorderNormal = new Color(180, 50, 50),
        BorderHover = new Color(220, 60, 60),
        BorderPressed = new Color(140, 40, 40),
        BorderDisabled = new Color(40, 40, 50),
        TextNormal = new Color(255, 100, 100),
        TextHover = new Color(255, 130, 130),
        TextPressed = new Color(200, 80, 80),
        TextDisabled = new Color(70, 70, 80)
    };

    /// <summary>Highlighted active state  -- for selected toggles, active tabs.</summary>
    public static readonly ButtonStyle Active = new()
    {
        BackgroundNormal = new Color(40, 60, 90),
        BackgroundHover = new Color(50, 75, 110),
        BackgroundPressed = new Color(30, 50, 75),
        BorderNormal = new Color(80, 140, 220),
        BorderHover = new Color(100, 160, 240),
        BorderPressed = new Color(60, 110, 180),
        TextNormal = new Color(140, 200, 255),
        TextHover = new Color(180, 220, 255),
        TextPressed = new Color(110, 170, 220)
    };
}
