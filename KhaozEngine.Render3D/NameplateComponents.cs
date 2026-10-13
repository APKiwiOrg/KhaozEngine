using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>Attaches a component point to a target point. A null target names the root component.</summary>
public readonly record struct NameplateAttach(AttachPoint Self, string? Target, AttachPoint TargetPoint, Vector2 Offset);

/// <summary>A named piece of a nameplate, bound to a content slot.</summary>
public abstract record NameplateComponent(string Id, NameplateSlot Slot)
{
    public NameplateAttach Attach { get; init; }
}

/// <summary>Text with optional outline, shadow and an independent width cap.</summary>
public sealed record NameplateTextComponent(string Id, NameplateSlot Slot) : NameplateComponent(Id, Slot)
{
    public float FontScale { get; init; } = 1f;
    public Color? OutlineColor { get; init; }
    public float OutlineThickness { get; init; } = 0f;
    public Color? ShadowColor { get; init; }
    public Vector2 ShadowOffset { get; init; } = new(1f, 1f);
    /// <summary>Zero means this component has no independent width cap.</summary>
    public float MaxWidth { get; init; } = 0f;
}

/// <summary>Text within a padded badge, with an optional content tint over its base fill.</summary>
public sealed record NameplateBadgeComponent(string Id, NameplateSlot Slot) : NameplateComponent(Id, Slot)
{
    public float FontScale { get; init; } = 1f;
    public Color? OutlineColor { get; init; }
    public float OutlineThickness { get; init; } = 0f;
    public float PadX { get; init; } = 3f;
    public float PadY { get; init; } = 0f;
    public float MinWidth { get; init; } = 0f;
    public float CornerRadius { get; init; } = 2f;
    public Color BaseFill { get; init; } = new(0.07f, 0.07f, 0.07f, 1f);
    public float TintAlpha { get; init; } = 0.32f;
    public Color BorderColor { get; init; } = Color.Black;
    public float BorderThickness { get; init; } = 1f;
}

/// <summary>A bar whose width and height describe the inner track. Its border sits outside the track.</summary>
public sealed record NameplateBarComponent(string Id, NameplateSlot Slot) : NameplateComponent(Id, Slot)
{
    /// <summary>Zero uses the template's content width.</summary>
    public float Width { get; init; } = 0f;
    public float Height { get; init; } = 7f;
    public float CornerRadius { get; init; } = 0f;
    public Color BorderColor { get; init; } = Color.Black;
    public float BorderThickness { get; init; } = 0f;
}
