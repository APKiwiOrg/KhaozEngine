using System.Collections.Generic;
using KhaozEngine.StatusEffects;
using Xunit;

namespace KhaozEngine.Tests.StatusEffects;

public sealed class StatusEffectBookCatalogTests
{
    [Fact]
    public void Reapply_WhenCatalogReturnsReplacement_UsesCapturedDefinitionThroughoutInstance()
    {
        var catalog = new ReplaceableCatalog(new(new(1), 10, maxStacks: 3, periodTicks: 2,
            modifiers: new[] { new StatusEffectModifier(4, 5) }));
        var book = new StatusEffectBook(catalog);
        book.Apply(7, new(1), 0, source: 11, durationTicks: 20);
        catalog.Definition = new(new(1), 100, scope: StatusEffectScope.PerSource, maxStacks: 1,
            periodTicks: 1, shown: false, modifiers: new[] { new StatusEffectModifier(4, 99) });

        Assert.Equal(StatusEffectApplyResult.Stacked, book.Apply(7, new(1), 1, source: 12));

        var entries = new List<StatusEffectEntry>();
        book.CopyEntries(7, entries, shownOnly: true);
        Assert.Equal(new StatusEffectEntry(new(1), 12, 2, 1, 20), Assert.Single(entries));
        Assert.Equal(StatusEffectApplyResult.Stacked, book.Apply(7, new(1), 15, source: 13, magnitude: 11));
        entries.Clear();
        book.CopyEntries(7, entries, shownOnly: true);
        Assert.Equal(new StatusEffectEntry(new(1), 13, 3, 15, 25), Assert.Single(entries));
        Assert.Equal(15, book.ModifierTotal(7, 4));
        var events = new List<StatusEffectEvent>();
        book.DrainEvents(events);

        book.Advance(16);

        events.Clear();
        book.DrainEvents(events);
        Assert.Equal(new StatusEffectEvent(StatusEffectEventKind.Periodic, 7, new(1), 13, 3, 11, 16,
            StatusEffectEndReason.None), Assert.Single(events));
    }

    private sealed class ReplaceableCatalog(StatusEffectDefinition definition) : IStatusEffectCatalog
    {
        public StatusEffectDefinition Definition { get; set; } = definition;

        public bool TryGet(StatusEffectId id, out StatusEffectDefinition definition)
        {
            definition = Definition;
            return definition.Id == id;
        }
    }
}
