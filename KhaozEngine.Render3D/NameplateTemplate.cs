using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

/// <summary>An ordered, validated component layout and its panel and viewport styling.</summary>
public sealed record NameplateTemplate
{
    private readonly NameplateComponent[] _components;

    public IReadOnlyList<NameplateComponent> Components { get; }
    public Color PanelFill { get; init; } = Color.Transparent;
    public Color PanelBorder { get; init; } = Color.Transparent;
    public float PanelBorderThickness { get; init; } = 0f;
    public float CornerRadius { get; init; } = 0f;
    public float PadX { get; init; } = 0f;
    public float PadY { get; init; } = 0f;
    public float MinContentWidth { get; init; } = 0f;
    /// <summary>Zero leaves the content width unbounded.</summary>
    public float MaxContentWidth { get; init; } = 0f;
    public NameplateEdgeBehavior EdgeBehavior { get; init; } = NameplateEdgeBehavior.None;
    public float EdgeMargin { get; init; } = 4f;
    public float EdgeHysteresis { get; init; } = 0f;

    /// <summary>Copies the components and validates ids and backward-only attachment targets.</summary>
    public NameplateTemplate(IReadOnlyList<NameplateComponent> components)
    {
        if (components.Count == 0)
            throw new ArgumentException("A template requires at least one component.", nameof(components));

        _components = new NameplateComponent[components.Count];
        for (int i = 0; i < _components.Length; i++)
            _components[i] = components[i];

        for (int i = 0; i < _components.Length; i++)
        {
            var component = _components[i];
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
