using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class NameplateTemplateTests
{
    [Fact]
    public void AnEmptyComponentListIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new NameplateTemplate(Array.Empty<NameplateComponent>()));
    }

    [Fact]
    public void ANullComponentListIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new NameplateTemplate(null!));
    }

    [Fact]
    public void ANullComponentIsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            null!,
        }));

        Assert.Equal("components", error.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ANullOrBlankIdIsRefused(string? id)
    {
        Assert.Throws<ArgumentException>(() => new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent(id!, NameplateSlot.Name),
        }));
    }

    [Fact]
    public void ARepeatedIdIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBadgeComponent("name", NameplateSlot.Level),
        }));
    }

    [Fact]
    public void ARootWithANonNullTargetIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name)
            {
                Attach = new NameplateAttach(AttachPoint.Top, "name", AttachPoint.Bottom, Vector2.Zero),
            },
        }));
    }

    [Fact]
    public void ANonRootTargetingItselfIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBadgeComponent("level", NameplateSlot.Level)
            {
                Attach = new NameplateAttach(AttachPoint.Right, "level", AttachPoint.Left, Vector2.Zero),
            },
        }));
    }

    [Fact]
    public void ANonRootTargetingALaterComponentIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBadgeComponent("level", NameplateSlot.Level)
            {
                Attach = new NameplateAttach(AttachPoint.Right, "health", AttachPoint.Left, Vector2.Zero),
            },
            new NameplateBarComponent("health", NameplateSlot.Health),
        }));
    }

    [Fact]
    public void ANonRootTargetingAMissingComponentIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBadgeComponent("level", NameplateSlot.Level)
            {
                Attach = new NameplateAttach(AttachPoint.Right, "missing", AttachPoint.Left, Vector2.Zero),
            },
        }));
    }

    [Fact]
    public void ATemplateCopiesItsComponents()
    {
        var name = new NameplateTextComponent("name", NameplateSlot.Name);
        var level = new NameplateBadgeComponent("level", NameplateSlot.Level);
        var components = new List<NameplateComponent> { name, level };
        var template = new NameplateTemplate(components);

        components[0] = new NameplateBarComponent("replacement", NameplateSlot.Health);
        components.Add(new NameplateBarComponent("health", NameplateSlot.Health));
        components.Clear();

        Assert.Equal(2, template.Components.Count);
        Assert.Same(name, template.Components[0]);
        Assert.Same(level, template.Components[1]);
    }

    [Fact]
    public void NullTargetMeansTheRoot()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBadgeComponent("level", NameplateSlot.Level)
            {
                Attach = new NameplateAttach(AttachPoint.Right, null, AttachPoint.Left, new Vector2(-3f, 0f)),
            },
        });

        Assert.Equal(2, template.Components.Count);
        Assert.Null(template.Components[1].Attach.Target);
    }

    [Fact]
    public void ANonRootCanTargetAnyEarlierComponent()
    {
        var attach = new NameplateAttach(AttachPoint.Top, "level", AttachPoint.Bottom, new Vector2(0f, 2f));
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
            new NameplateBadgeComponent("level", NameplateSlot.Level),
            new NameplateBarComponent("health", NameplateSlot.Health) { Attach = attach },
        });

        Assert.Equal(3, template.Components.Count);
        Assert.Equal(attach, template.Components[2].Attach);
    }

    [Fact]
    public void ATextComponentHasTheSpecifiedDefaults()
    {
        var text = new NameplateTextComponent("name", NameplateSlot.Name);

        Assert.Equal(1f, text.FontScale);
        Assert.Null(text.OutlineColor);
        Assert.Equal(0f, text.OutlineThickness);
        Assert.Null(text.ShadowColor);
        Assert.Equal(new Vector2(1f, 1f), text.ShadowOffset);
        Assert.Equal(0f, text.MaxWidth);
    }

    [Fact]
    public void ABadgeComponentHasTheSpecifiedDefaults()
    {
        var badge = new NameplateBadgeComponent("level", NameplateSlot.Level);

        Assert.Equal(1f, badge.FontScale);
        Assert.Null(badge.OutlineColor);
        Assert.Equal(0f, badge.OutlineThickness);
        Assert.Equal(3f, badge.PadX);
        Assert.Equal(0f, badge.PadY);
        Assert.Equal(0f, badge.MinWidth);
        Assert.Equal(2f, badge.CornerRadius);
        Assert.Equal(new Color(0.07f, 0.07f, 0.07f, 1f), badge.BaseFill);
        Assert.Equal(0.32f, badge.TintAlpha);
        Assert.Equal(Color.Black, badge.BorderColor);
        Assert.Equal(1f, badge.BorderThickness);
    }

    [Fact]
    public void ABarComponentHasTheSpecifiedDefaults()
    {
        var bar = new NameplateBarComponent("health", NameplateSlot.Health);

        Assert.Equal(0f, bar.Width);
        Assert.Equal(7f, bar.Height);
        Assert.Equal(0f, bar.CornerRadius);
        Assert.Equal(Color.Black, bar.BorderColor);
        Assert.Equal(0f, bar.BorderThickness);
    }

    [Fact]
    public void ATemplateHasTheSpecifiedDefaults()
    {
        var template = new NameplateTemplate(new NameplateComponent[]
        {
            new NameplateTextComponent("name", NameplateSlot.Name),
        });

        Assert.Equal(Color.Transparent, template.PanelFill);
        Assert.Equal(Color.Transparent, template.PanelBorder);
        Assert.Equal(0f, template.PanelBorderThickness);
        Assert.Equal(0f, template.CornerRadius);
        Assert.Equal(0f, template.PadX);
        Assert.Equal(0f, template.PadY);
        Assert.Equal(0f, template.MinContentWidth);
        Assert.Equal(0f, template.MaxContentWidth);
        Assert.Equal(NameplateEdgeBehavior.None, template.EdgeBehavior);
        Assert.Equal(4f, template.EdgeMargin);
        Assert.Equal(0f, template.EdgeHysteresis);
    }

    [Fact]
    public void OnlyTheEngineCanConstructAComponentKind()
    {
        ConstructorInfo[] constructors = typeof(NameplateComponent).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        ConstructorInfo creating = Assert.Single(constructors,
            constructor => constructor.GetParameters() is not [{ ParameterType: var type }] || type != typeof(NameplateComponent));

        Assert.True(creating.IsFamilyAndAssembly);
        string[] properties = Array.ConvertAll(
            typeof(NameplateComponent).GetProperties(BindingFlags.Instance | BindingFlags.Public), property => property.Name);
        Array.Sort(properties, StringComparer.Ordinal);
        Assert.Equal(new[] { "Attach", "Id", "Slot" }, properties);
    }
}
