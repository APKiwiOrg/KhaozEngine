using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The boot-side half of <see cref="ModCandidateTables"/>: the <see cref="IContentLoadIndex"/> a host
/// registers against the <c>mod</c> type, so the candidate tables are built once at boot step 7b with no
/// second pass wired anywhere.
/// <para>
/// It reads the other types' ROWS through the snapshot it is handed, which the interface explicitly
/// permits, and reads NO other index. It THROWS to fail the boot closed rather than handing back a partial
/// table set, which the runtime turns into a <see cref="ContentLoadIndexException"/> naming the type.
/// </para>
/// <para>
/// <b>It owns the WHOLE of what a roll reads</b>, not just the candidate tables: the content fold and the
/// run ceiling are functions of the same snapshot and are built here, once, so a replay harness's second
/// generator costs one object rather than a second fold.
/// </para>
/// <para>
/// <b>A BOOT builds the tables, not a publish.</b> They are immutable for the life of the process, so there
/// is exactly ONE table set and no roll can see two. A new content version becomes active at server
/// RESTART, so this builds no swap and a second <see cref="Build"/> on the same instance is refused.
/// </para>
/// <para>
/// A host registers it by handing it to <see cref="InstanceContentTypes.Register"/>, which attaches it to
/// the <c>mod</c> registration, and reads it back through <c>ContentRuntime.TryGetLoadIndex</c> once boot
/// step 7b has finished.
/// </para>
/// </summary>
public sealed class ModCandidateTablesIndex : IContentLoadIndex
{
    GenerationTables? _tables;

    /// <inheritdoc />
    public ContentTypeId Type => new(InstanceContentTypeIds.ModTypeId);

    /// <summary>The built candidate tables, immutable for the process.</summary>
    /// <exception cref="InvalidOperationException">The boot has not built them, or its build failed.</exception>
    public ModCandidateTables Tables => Generation.Candidates;

    /// <summary>
    /// The candidate tables plus the content fold and the run ceiling beside them, built ONCE here so every
    /// <see cref="ItemGenerator"/> over this version costs one object and no table build (spec 9.1).
    /// </summary>
    /// <exception cref="InvalidOperationException">The boot has not built them, or its build failed.</exception>
    public GenerationTables Generation => _tables ?? throw new InvalidOperationException(
        "The mod candidate tables were asked for before boot step 7b built them, or after a build that failed. They are built ONCE, at boot, and a failed build leaves nothing behind on purpose.");

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// This index is built already, or the version breaks one of the ceilings
    /// <see cref="ModCandidateTables.Build"/> refuses.
    /// </exception>
    public void Build(IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_tables is not null)
        {
            throw new InvalidOperationException(
                "The mod candidate tables are built already. A process holds exactly one table set, built at boot, because a new content version becomes active at server restart rather than through a swap.");
        }

        _tables = GenerationTables.Build(ModCandidateTables.Build(snapshot), snapshot);
    }
}
