using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>An ordered, validated component layout and its panel and viewport styling.</summary>
public sealed record NameplateTemplate
{
    private readonly NameplateComponent[] _components;

    /// <summary>The components in draw order. The first is the root every other one attaches to by default.</summary>
    public IReadOnlyList<NameplateComponent> Components { get; }
    /// <summary>Panel background fill. Alpha 0 skips the panel draw.</summary>
    public Color PanelFill { get; init; } = Color.Transparent;
    /// <summary>Panel border colour, drawn when <see cref="PanelBorderThickness"/> and its alpha are positive.</summary>
    public Color PanelBorder { get; init; } = Color.Transparent;
    /// <summary>Panel border width in pixels. Zero draws no border.</summary>
    public float PanelBorderThickness { get; init; } = 0f;
    /// <summary>Panel corner radius in pixels.</summary>
    public float CornerRadius { get; init; } = 0f;
    /// <summary>Horizontal padding between the panel edge and the component bounds.</summary>
    public float PadX { get; init; } = 0f;
    /// <summary>Vertical padding between the panel edge and the component bounds.</summary>
    public float PadY { get; init; } = 0f;
    /// <summary>The content is never narrower than this width, which also sizes bars with no width of their own.</summary>
    public float MinContentWidth { get; init; } = 0f;
    /// <summary>Caps the content width and ellipsizes text to fit. Zero leaves the content width unbounded.</summary>
    public float MaxContentWidth { get; init; } = 0f;
    /// <summary>How the plate is kept inside the viewport.</summary>
    public NameplateEdgeBehavior EdgeBehavior { get; init; } = NameplateEdgeBehavior.None;
    /// <summary>Inset in pixels from every viewport edge when <see cref="EdgeBehavior"/> is not
    /// <see cref="NameplateEdgeBehavior.None"/>.</summary>
    public float EdgeMargin { get; init; } = 4f;
    /// <summary><see cref="NameplateEdgeBehavior.Deflect"/> only: headroom in pixels a deflected plate must regain
    /// before it returns above the anchor. Values of zero or less mean half the plate height.</summary>
    public float EdgeHysteresis { get; init; } = 0f;

    /// <summary>Copies the components and validates ids and backward-only attachment targets.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> is null.</exception>
    /// <exception cref="ArgumentException">The list is empty, holds a null component, repeats or blanks an id, or
    /// attaches to a component that is not earlier.</exception>
    public NameplateTemplate(IReadOnlyList<NameplateComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0)
            throw new ArgumentException("A template requires at least one component.", nameof(components));

        _components = new NameplateComponent[components.Count];
        for (int i = 0; i < _components.Length; i++)
            _components[i] = components[i];

        for (int i = 0; i < _components.Length; i++)
        {
            var component = _components[i];
            if (component is null)
                throw new ArgumentException("Components must not be null.", nameof(components));
            if (string.IsNullOrWhiteSpace(component.Id))
                throw new ArgumentException("Component ids must not be null or blank.", nameof(components));

            for (int j = 0; j < i; j++)
                if (_components[j].Id == component.Id)
                    throw new ArgumentException("Component ids must be unique.", nameof(components));

            string? target = component.Attach.Target;
            if (i == 0)
            {
                if (target is not null)
                    throw new ArgumentException("The root component must have a null target.", nameof(components));
            }
            else if (target is not null)
            {
                bool found = false;
                for (int j = 0; j < i; j++)
                {
                    if (_components[j].Id == target)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    throw new ArgumentException("Attachment targets must name an earlier component.", nameof(components));
            }
        }

        Components = Array.AsReadOnly(_components);
    }
}
