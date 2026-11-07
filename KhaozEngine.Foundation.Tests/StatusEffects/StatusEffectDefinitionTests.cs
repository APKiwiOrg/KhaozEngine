using System;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectDefinitionTests
{
    [Fact]
    public void Definition_RejectsInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => new StatusEffectDefinition(new(0), 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatusEffectDefinition(new(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatusEffectDefinition(new(1), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatusEffectDefinition(new(1), 30, maxStacks: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatusEffectDefinition(new(1), 30, maxStacks: 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StatusEffectDefinition(new(1), 30, periodTicks: -1));
        Assert.Throws<ArgumentNullException>(() => new StatusEffectDefinition(new(1), 30, iconKey: null!));
    }

    [Fact]
    public void Definition_SnapshotsModifiers()
    {
        var modifiers = new[] { new StatusEffectModifier(2, 500) };
        var definition = new StatusEffectDefinition(new(1), 30, maxStacks: 255, modifiers: modifiers);
        modifiers[0] = new(2, 999);
        var book = new StatusEffectBook(new StatusEffectCatalog(new[] { definition }));

        book.Apply(7, new(1), 100);

        Assert.Equal(new StatusEffectModifier(2, 500), Assert.Single(definition.Modifiers));
        Assert.Equal(500, book.ModifierTotal(7, 2));
        Assert.Empty(new StatusEffectDefinition(new(2), 30).Modifiers);
    }

    [Fact]
    public void Catalog_RejectsDuplicateIds()
    {
        Assert.Throws<ArgumentException>(() => new StatusEffectCatalog(new[]
        {
            new StatusEffectDefinition(new(1), 30),
            new StatusEffectDefinition(new(1), 60)
        }));
    }

    [Fact]
    public void Catalog_ResolvesKnownIdsAndRefusesUnknownIds()
    {
        var definition = new StatusEffectDefinition(new(-2), 30);
        var catalog = new StatusEffectCatalog(new[] { definition });

        Assert.True(definition.Id.IsValid);
        Assert.False(default(StatusEffectId).IsValid);
        Assert.Equal(1, catalog.Count);
        Assert.True(catalog.TryGet(new(-2), out var found));
        Assert.Same(definition, found);
        Assert.False(catalog.TryGet(new(9), out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Catalog_RejectsNullInputAndNullDefinitions()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusEffectCatalog(null!));
        Assert.Throws<ArgumentException>(() => new StatusEffectCatalog(new StatusEffectDefinition[] { null! }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(256)]
    public void Book_RejectsInvalidActorCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StatusEffectBook(new StatusEffectCatalog(Array.Empty<StatusEffectDefinition>()), capacity));
    }

    [Fact]
    public void Book_RejectsNullCatalog()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusEffectBook(null!));
    }
}
