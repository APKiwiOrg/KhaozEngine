using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// One in-memory catalog with a real pack root, plus the committed target bundle this build ships and the
/// two definitions that bring an older catalog up to it.
/// <para>
/// The pack root is a real directory because a publish writes FILES before it writes rows, so a store with
/// nowhere to write them cannot publish at all, and an upgrade is a publish.
/// </para>
/// <para>
/// The registry carries BOTH fixture types from the start, which is the current build's view. An older
/// catalog is one that holds no rows of the second type, and that is what
/// <see cref="SeedOlderCatalogAsync"/> produces.
/// </para>
/// </summary>
internal sealed class UpgradeHarness : IDisposable
{
    /// <summary>The first definition's stable id.</summary>
    public const string FirstId = "add-other-first";

    /// <summary>The second definition's stable id, which plans against the first one's published result.</summary>
    public const string SecondId = "add-other-second";

    readonly TemporaryCatalogDatabase _files = new();

    public UpgradeHarness()
    {
        Registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        Packs = _files.Pack();
        Store = new InMemoryContentAuthoringStore(Registry, Packs);
        Target = UpgradeFixtures.Target(
            Registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
            UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));
        First = UpgradeFixtures.Adds(
            FirstId, 1, Target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "new_row"));
        Second = UpgradeFixtures.Adds(
            SecondId, 2, Target, UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row"));
        Set = new ContentUpgradeSet(First, Second);
    }

    /// <summary>The current build's registry, carrying both fixture types.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The pack root a publish writes to.</summary>
    public FileSystemPackStore Packs { get; }

    /// <summary>The catalog under test.</summary>
    public InMemoryContentAuthoringStore Store { get; }

    /// <summary>The committed bundle this build ships, which a planner reads the new rows out of.</summary>
    public ContentBundle Target { get; }

    /// <summary>The first definition, which adds the second type's first row.</summary>
    public ContentUpgradeDefinition First { get; }

    /// <summary>The second definition, which adds the second type's second row.</summary>
    public ContentUpgradeDefinition Second { get; }

    /// <summary>Both definitions as the set this build ships.</summary>
    public ContentUpgradeSet Set { get; }

    /// <summary>
    /// An OLDER populated catalog: version 1 carrying the first type's row and no row of the second type at
    /// all, which is exactly the catalog a strict boot of the new build refuses.
    /// </summary>
    /// <param name="minimumServerBuild">The version's minimum server build, or null for none.</param>
    /// <param name="minimumClientBuild">The version's minimum client build, or null for none.</param>
    public async Task SeedOlderCatalogAsync(int? minimumServerBuild = null, int? minimumClientBuild = null)
    {
        await Store.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("old_row"), PublishFixtures.Fields(11))],
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator,
            "seed the older catalog");
        await Store.PublishAsync(PublishFixtures.Request(0, minimumServerBuild, minimumClientBuild));
    }

    /// <summary>A FRESH install: the committed bundle imported whole, which is version 1 of a new catalog.</summary>
    public Task SeedCurrentCatalogAsync()
        => Store.ImportBundleAsync(Target, UpgradeFixtures.Actor, UpgradeFixtures.Operator, "fresh install");

    /// <summary>The four numbers a "nothing was written" assertion compares.</summary>
    public Task<CatalogFootprint> FootprintAsync() => UpgradeFixtures.FootprintAsync(Store);

    /// <inheritdoc />
    public void Dispose() => _files.Dispose();
}
