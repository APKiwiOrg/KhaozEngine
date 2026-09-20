using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The upgrade definitions one build SHIPS, ordered ascending and validated whole at construction. A game
/// holds one of these in its server assembly and hands it to the runner.
/// <para>
/// <b>Duplicate ids and duplicate orders are refused here rather than at the first run.</b> Two definitions
/// under one id would have the ledger record only one of them and silently skip the other forever, and two
/// under one order would run in an order that depends on the list the caller happened to build, which is the
/// kind of defect that only shows up on the one catalog that is old enough to need both.
/// </para>
/// <para>
/// <b>An order is only a running order, and the IDENTITY is the id.</b> So two shapes a stricter check would
/// refuse are allowed, and each one is reported as an informational diagnostic rather than a refusal:
/// </para>
/// <list type="bullet">
/// <item>
/// A definition whose <see cref="ContentUpgradeDefinition.Order"/> differs from the order the ledger
/// recorded it under (<see cref="ContentUpgradeCodes.UpgradeOrderMoved"/>). The ledger holds the id, so the
/// catalog already carries the upgrade and it never runs again.
/// </item>
/// <item>
/// A PENDING definition ordered below one the catalog already holds
/// (<see cref="ContentUpgradeCodes.PendingBelowApplied"/>), which is what two feature branches merging
/// produces. It has not run, so it runs now, in its own order, against the catalog as it stands.
/// </item>
/// </list>
/// </summary>
public sealed class ContentUpgradeSet
{
    readonly ContentUpgradeDefinition[] _definitions;
    readonly Dictionary<string, ContentUpgradeDefinition> _byId;

    /// <summary>Builds and validates the set. The order the caller passes does not matter.</summary>
    /// <param name="definitions">Every definition this build ships.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definitions"/> is null, or an entry is.</exception>
    /// <exception cref="ArgumentException">Two definitions share an id or an order.</exception>
    public ContentUpgradeSet(params ContentUpgradeDefinition[] definitions)
        : this((IReadOnlyList<ContentUpgradeDefinition>)(definitions
            ?? throw new ArgumentNullException(nameof(definitions))))
    {
    }

    /// <summary>Builds and validates the set from any list.</summary>
    /// <param name="definitions">Every definition this build ships.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definitions"/> is null, or an entry is.</exception>
    /// <exception cref="ArgumentException">Two definitions share an id or an order.</exception>
    public ContentUpgradeSet(IReadOnlyList<ContentUpgradeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var ordered = new ContentUpgradeDefinition[definitions.Count];
        for (int i = 0; i < definitions.Count; i++)
        {
            ordered[i] = definitions[i] ?? throw new ArgumentNullException(
                nameof(definitions), FormattableString.Invariant($"Definition {i} is null."));
        }

        Array.Sort(ordered, static (left, right) => left.Order.CompareTo(right.Order));

        _byId = new Dictionary<string, ContentUpgradeDefinition>(ordered.Length, StringComparer.Ordinal);
        for (int i = 0; i < ordered.Length; i++)
        {
            ContentUpgradeDefinition definition = ordered[i];
            if (definition.Id.Length == 0)
            {
                throw new ArgumentException(
                    "An upgrade definition carries a non-empty id, which is the ledger's primary key.",
                    nameof(definitions));
            }

            if (!_byId.TryAdd(definition.Id, definition))
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Two upgrade definitions share the id '{definition.Id}'. An id is the ledger's primary key and is never reused."),
                    nameof(definitions));
            }

            if (i > 0 && ordered[i - 1].Order == definition.Order)
            {
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"Upgrades '{ordered[i - 1].Id}' and '{definition.Id}' share order {definition.Order}. An order decides which runs first and has to be unique."),
                    nameof(definitions));
            }
        }

        _definitions = ordered;
    }

    /// <summary>An empty set, which is what a build that ships no upgrade yet hands the runner.</summary>
    public static ContentUpgradeSet Empty { get; } = new([]);

    /// <summary>Every definition, ASCENDING by order, which is the order the runner applies them in.</summary>
    public IReadOnlyList<ContentUpgradeDefinition> Definitions => _definitions;

    /// <summary>How many definitions this build ships.</summary>
    public int Count => _definitions.Length;

    /// <summary>One definition by its stable id, ordinally.</summary>
    /// <param name="id">The id, compared ordinally.</param>
    /// <param name="definition">The definition, when the set ships one under that id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    public bool TryGet(string id, [MaybeNullWhen(false)] out ContentUpgradeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.TryGetValue(id, out definition);
    }

    /// <summary>Whether this build ships an upgrade under the id, which is the catalog-ahead check.</summary>
    /// <param name="id">The id, compared ordinally.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    public bool Contains(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _byId.ContainsKey(id);
    }
}
