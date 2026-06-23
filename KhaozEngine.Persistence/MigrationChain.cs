using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Persistence;

/// <summary>
/// Static entry points for building a <see cref="MigrationChain{T}"/> of ordered, versioned save/settings
/// migration steps.
/// </summary>
public static class MigrationChain
{
    /// <summary>
    /// Begins a chain for any type, with caller-supplied accessors for the schema-version field. Use this
    /// for POCOs that do not implement <see cref="ISchemaVersioned"/>.
    /// </summary>
    public static MigrationChainBuilder<T> For<T>(Func<T, int> getVersion, Action<T, int> setVersion)
        where T : new()
        => new(getVersion, setVersion);

    /// <summary>
    /// Begins a chain for a type that implements <see cref="ISchemaVersioned"/>; the version field is read
    /// and written through <see cref="ISchemaVersioned.SchemaVersion"/>.
    /// </summary>
    public static MigrationChainBuilder<T> For<T>()
        where T : ISchemaVersioned, new()
        => new(v => v.SchemaVersion, (v, n) => v.SchemaVersion = n);
}

/// <summary>
/// Fluent builder for a <see cref="MigrationChain{T}"/>. Register one <see cref="Step"/> per schema
/// version, then <see cref="Build"/> to validate and freeze the chain.
/// </summary>
/// <typeparam name="T">The migrated value type.</typeparam>
public sealed class MigrationChainBuilder<T> where T : new()
{
    private readonly Func<T, int> getVersion;
    private readonly Action<T, int> setVersion;
    private readonly Dictionary<int, Func<T, T>> steps = new();

    internal MigrationChainBuilder(Func<T, int> getVersion, Action<T, int> setVersion)
    {
        this.getVersion = getVersion ?? throw new ArgumentNullException(nameof(getVersion));
        this.setVersion = setVersion ?? throw new ArgumentNullException(nameof(setVersion));
    }

    /// <summary>
    /// Registers the transform that takes a value from <paramref name="fromVersion"/> to
    /// <paramref name="fromVersion"/> + 1. The transform does ONLY the data change (mutate in place or
    /// return a replacement); the chain stamps the version field afterwards. Returning null keeps the input.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="migrate"/> is null.</exception>
    /// <exception cref="ArgumentException">A step from <paramref name="fromVersion"/> is already registered.</exception>
    public MigrationChainBuilder<T> Step(int fromVersion, Func<T, T> migrate)
    {
        ArgumentNullException.ThrowIfNull(migrate);
        if (steps.ContainsKey(fromVersion))
            throw new ArgumentException($"A migration step from version {fromVersion} is already registered.", nameof(fromVersion));
        steps.Add(fromVersion, migrate);
        return this;
    }

    /// <summary>
    /// Validates and freezes the chain. The registered step keys must form the contiguous run
    /// <c>{ start .. currentVersion-1 }</c> with no gaps; no step may target at or beyond
    /// <paramref name="currentVersion"/>. An empty chain (no steps) is allowed and acts as a no-op.
    /// </summary>
    /// <exception cref="ArgumentException">A step targets >= <paramref name="currentVersion"/>, or there is a gap.</exception>
    public MigrationChain<T> Build(int currentVersion)
    {
        foreach (int from in steps.Keys)
        {
            if (from >= currentVersion)
                throw new ArgumentException(
                    $"Migration step from version {from} targets version {from + 1}, at or beyond the current version {currentVersion}.");
        }

        if (steps.Count > 0)
        {
            int start = int.MaxValue;
            foreach (int k in steps.Keys)
                if (k < start) start = k;

            for (int v = start; v < currentVersion; v++)
            {
                if (!steps.ContainsKey(v))
                    throw new ArgumentException(
                        $"Migration chain has a gap: no step registered from version {v} (steps must be contiguous from {start} to {currentVersion - 1}).");
            }
        }

        return new MigrationChain<T>(getVersion, setVersion, new Dictionary<int, Func<T, T>>(steps), currentVersion);
    }
}

/// <summary>
/// An immutable, validated chain of versioned migration steps. Reusable and stateless across loads:
/// <see cref="Migrate"/> steps a value from its stored version up to <see cref="CurrentVersion"/>.
/// Never throws on the value it is handed (corrupt/odd data is logged and the best-effort value returned),
/// consistent with the engine's "a bad save never crashes the game" stance.
/// </summary>
/// <typeparam name="T">The migrated value type.</typeparam>
public sealed class MigrationChain<T> where T : new()
{
    private readonly Func<T, int> getVersion;
    private readonly Action<T, int> setVersion;
    private readonly Dictionary<int, Func<T, T>> steps;
    private readonly int startVersion;

    /// <summary>The version a fully-migrated value ends at.</summary>
    public int CurrentVersion { get; }

    internal MigrationChain(Func<T, int> getVersion, Action<T, int> setVersion, Dictionary<int, Func<T, T>> steps, int currentVersion)
    {
        this.getVersion = getVersion;
        this.setVersion = setVersion;
        this.steps = steps;
        CurrentVersion = currentVersion;

        int start = currentVersion;
        foreach (int k in steps.Keys)
            if (k < start) start = k;
        startVersion = start;
    }

    /// <summary>
    /// Runs the chain on <paramref name="value"/> from its stored version up to <see cref="CurrentVersion"/>.
    /// A value already at/above current is returned untouched. A value older than the oldest step is logged
    /// (Warn) and returned unchanged. A step that throws is logged (Error) and halts the chain, returning the
    /// partially-migrated value (its version reflects only the completed steps).
    /// </summary>
    /// <param name="value">The value to migrate. A null value is returned as-is.</param>
    /// <param name="logger">Optional logger; defaults to the "MigrationChain" category.</param>
    public T Migrate(T value, ILogger? logger = null)
    {
        if (value is null) return value;
        logger ??= Log.Get("MigrationChain");

        try
        {
            int v = getVersion(value);

            if (v >= CurrentVersion)
                return value;   // already current, or a save from a newer build

            if (v < startVersion)
            {
                logger.Warn($"Schema version {v} predates the oldest migration step ({startVersion}); leaving value as-is.");
                return value;
            }

            while (v < CurrentVersion)
            {
                if (!steps.TryGetValue(v, out Func<T, T>? migrate))
                {
                    // Unreachable for a Build-validated chain; guard anyway.
                    logger.Warn($"No migration step from version {v}; halting.");
                    break;
                }

                value = migrate(value) ?? value;
                v++;
                setVersion(value, v);
            }

            return value;
        }
        catch (Exception ex)
        {
            logger.Error("Migration chain failed; returning value as-is.", ex);
            return value;
        }
    }
}
