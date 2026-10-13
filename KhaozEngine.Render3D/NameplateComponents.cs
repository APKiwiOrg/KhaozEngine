using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>Attaches a component point to a target point. A null target names the root component.</summary>
public readonly record struct NameplateAttach(AttachPoint Self, string? Target, AttachPoint TargetPoint, Vector2 Offset);

/// <summary>
/// A named piece of a nameplate, bound to a content slot. The engine owns the kinds, because the layout and
/// renderer only know <see cref="NameplateTextComponent"/>, <see cref="NameplateBadgeComponent"/> and
/// <see cref="NameplateBarComponent"/>.
/// </summary>
public abstract record NameplateComponent
{
    private protected NameplateComponent(string id, NameplateSlot slot)
    {
        Id = id;
        Slot = slot;
    }

    /// <summary>Seals the component kinds to the engine: code outside this assembly cannot override an internal abstract member, so it cannot subclass NameplateComponent.</summary>
    internal abstract void EngineOwned();

    /// <summary>The component's unique name within its template, used as an attachment target.</summary>
    public string Id { get; init; }

    /// <summary>The content slot this component reads its value from.</summary>
    public NameplateSlot Slot { get; init; }

    /// <summary>Where this component sits relative to an earlier one. The root component's target is null.</summary>
    public NameplateAttach Attach { get; init; }
}

/// <summary>Text with optional outline, shadow and an independent width cap.</summary>
public sealed record NameplateTextComponent(string Id, NameplateSlot Slot) : NameplateComponent(Id, Slot)
{
    internal override void EngineOwned() { }

    /// <summary>Constant on-screen text scale.</summary>
    public float FontScale { get; init; } = 1f;
    /// <summary>The outline colour. Null, or a thickness of zero or less, draws plain text.</summary>
    public Color? OutlineColor { get; init; }
    /// <summary>Outline width in pixels on every side. It adds to the measured size and counts against caps.</summary>
    public float OutlineThickness { get; init; } = 0f;
    /// <summary>When set, the text is drawn once offset in this colour before the main pass.</summary>
    public Color? ShadowColor { get; init; }
    /// <summary>Pixel offset of the <see cref="ShadowColor"/> pass.</summary>
    public Vector2 ShadowOffset { get; init; } = new(1f, 1f);
    /// <summary>Outer width cap in pixels, outline included. The text is ellipsized to fit. Zero means this
    /// component has no independent width cap.</summary>
    public float MaxWidth { get; init; } = 0f;
}

/// <summary>Text within a padded badge, with an optional content tint over its base fill.</summary>
public sealed record NameplateBadgeComponent(string Id, NameplateSlot Slot) : NameplateComponent(Id, Slot)
{
    internal override void EngineOwned() { }

    /// <summary>Constant on-screen text scale.</summary>
    public float FontScale { get; init; } = 1f;
    /// <summary>The outline colour. Null, or a thickness of zero or less, draws plain text.</summary>
    public Color? OutlineColor { get; init; }
    /// <summary>Outline width in pixels on every side of the text.</summary>
    public float OutlineThickness { get; init; } = 0f;
    /// <summary>Horizontal padding between the badge edge and its text.</summary>
    public float PadX { get; init; } = 3f;
    /// <summary>Vertical padding between the badge edge and its text.</summary>
    public float PadY { get; init; } = 0f;
    /// <summary>The badge is never narrower than this width in pixels.</summary>
    public float MinWidth { get; init; } = 0f;
    /// <summary>Badge corner radius in pixels.</summary>
    public float CornerRadius { get; init; } = 2f;
    /// <summary>The badge fill before any content tint.</summary>
    public Color BaseFill { get; init; } = new(0.07f, 0.07f, 0.07f, 1f);
    /// <summary>How far a content tint moves the fill from <see cref="BaseFill"/>, from 0 to 1.</summary>
    public float TintAlpha { get; init; } = 0.32f;
    /// <summary>Badge border colour.</summary>
    public Color BorderColor { get; init; } = Color.Black;
    /// <summary>Badge border width in pixels. Zero draws no border.</summary>
    public float BorderThickness { get; init; } = 1f;
}

/// <summary>A bar whose width and height describe the inner track. Its border sits outside the track.</summary>
public sealed record NameplateBarComponent(string Id, NameplateSlot Slot) : NameplateComponent(Id, Slot)
{
    internal override void EngineOwned() { }

    /// <summary>Legacy bars omit the fill call when their clamped fraction is not positive.</summary>
    internal bool SkipEmptyFill { get; init; }

    /// <summary>Track width in pixels. Zero uses the template's content width.</summary>
    public float Width { get; init; } = 0f;
    /// <summary>Track height in pixels.</summary>
    public float Height { get; init; } = 7f;
    /// <summary>Corner radius of the track and fill.</summary>
    public float CornerRadius { get; init; } = 0f;
    /// <summary>Border colour when the content supplies none.</summary>
    public Color BorderColor { get; init; } = Color.Black;
    /// <summary>Border width in pixels, outside the track. Zero draws no border.</summary>
    public float BorderThickness { get; init; } = 0f;
}
