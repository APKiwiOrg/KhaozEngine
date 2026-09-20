using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// The four numbers every "nothing was written" assertion reads at once: how many versions the catalog has
/// published, how many audit rows it holds, how many ledger rows, and how many edits the open draft carries.
/// <para>
/// They are taken together rather than one at a time because the claim under test is about the whole store:
/// an up-to-date run that quietly opened and discarded a draft would leave the version count alone and move
/// the audit count, and a test that only read the version count would pass.
/// </para>
/// </summary>
/// <param name="Versions">Published versions.</param>
/// <param name="Audit">Audit rows.</param>
/// <param name="Ledger">Upgrade ledger rows.</param>
/// <param name="DraftEdits">Edits on the open draft, and 0 when none is open.</param>
internal readonly record struct CatalogFootprint(int Versions, int Audit, int Ledger, int DraftEdits);

/// <summary>
/// The registries, target bundles, definitions and options the upgrade suites share. Everything here is
/// GENERIC over the two fixture content types, because the engine's orchestration carries no game noun and a
/// fixture that invented one would be testing a game.
/// </summary>
internal static class UpgradeFixtures
{
    /// <summary>The actor every run here carries, which is also what proves a draft is the runner's own.</summary>
    public const string Actor = "content-upgrade-tests";

    /// <summary>The operator identity every run here forwards.</summary>
    public const string Operator = "oid:upgrade-tests";

    /// <summary>The type the older catalog already carries rows of.</summary>
    public static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    /// <summary>The type an additive upgrade brings, which an older catalog holds no rows of.</summary>
    public static ContentTypeId Other => new(PublishFixtures.OtherTypeId);

    /// <summary>An apply run.</summary>
    /// <param name="expectedVersion">The version the caller expects, or null for the local arm.</param>
    /// <param name="serverBuild">This build's server ordinal.</param>
    /// <param name="clientBuild">This build's client ordinal.</param>
    public static ContentUpgradeOptions Apply(int? expectedVersion = null, int serverBuild = 0, int clientBuild = 0)
        => new(ContentUpgradeMode.Apply, Actor, Operator, serverBuild, clientBuild)
        {
            ExpectedVersion = expectedVersion,
        };

    /// <summary>A preview run, which writes nothing.</summary>
    /// <param name="expectedVersion">The version the caller expects, or null.</param>
    public static ContentUpgradeOptions Preview(int? expectedVersion = null)
        => new(ContentUpgradeMode.Preview, Actor, Operator, 0, 0) { ExpectedVersion = expectedVersion };

    /// <summary>
    /// A committed target bundle over a registry, built from the registry's own declarations so it agrees
    /// with it by construction. That is what a game's committed bundle IS: an export of the catalog the
    /// current build ships.
    /// </summary>
    /// <param name="registry">The registry the bundle declares its types from.</param>
    /// <param name="rows">Every row the shipped catalog carries, each naming its stable id.</param>
    public static ContentBundle Target(ContentTypeRegistry registry, params ContentBundleRow[] rows)
    {
        ArgumentNullException.ThrowIfNull(registry);

        IReadOnlyList<ContentTypeRegistration> registered = registry.ByTypeId;
        var types = new List<ContentBundleType>(registered.Count);
        for (int i = 0; i < registered.Count; i++)
        {
            ContentTypeRegistration registration = registered[i];
            types.Add(new ContentBundleType(
                registration.Type,
                registration.TypeKey,
                registration.DefaultVisibility,
                registration.ChunkSlots,
                registration.MaxDefinitionId,
                registration.Schema));
        }

        return new ContentBundle(ContentBundle.CurrentFormatVersion, "target-bundle", 0, types, rows, [], []);
    }

    /// <summary>One committed row carrying its stable id, which is what makes an adoption a no-op.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="id">The stable definition id.</param>
    /// <param name="key">The row's key.</param>
    /// <param name="value">The required int field's value.</param>
    public static ContentBundleRow Row(ContentTypeId type, int id, string key, int value)
        => new(type, id, new ContentKey(key), false, null, PublishFixtures.Fields(value));

    /// <summary>One committed identity, which is how a definition names a row to add.</summary>
    /// <param name="type">The content type.</param>
    /// <param name="key">The row's key.</param>
    public static ContentUpgradeIdentity Identity(ContentTypeId type, string key)
        => new(type, new ContentKey(key));

    /// <summary>A definition whose planner adds the named identities out of the committed target bundle.</summary>
    /// <param name="id">The definition's stable id.</param>
    /// <param name="order">The definition's order.</param>
    /// <param name="target">The committed target bundle.</param>
    /// <param name="identities">The identities to add.</param>
    public static ContentUpgradeDefinition Adds(
        string id,
        int order,
        ContentBundle target,
        params ContentUpgradeIdentity[] identities)
        => new(
            id,
            order,
            "adds " + string.Join(", ", KeysOf(identities)),
            context => new ContentUpgradePlanBuilder(context, target).AddRows(identities).Build());

    /// <summary>A definition whose planner always refuses, for the refusal path.</summary>
    /// <param name="id">The definition's stable id.</param>
    /// <param name="order">The definition's order.</param>
    /// <param name="reason">The refusal reason.</param>
    public static ContentUpgradeDefinition Refuses(string id, int order, string reason)
        => new(id, order, "refuses", _ => ContentUpgradePlan.Refused(reason));

    /// <summary>The four numbers a "nothing was written" assertion compares.</summary>
    /// <param name="store">The store to read.</param>
    public static async Task<CatalogFootprint> FootprintAsync(IContentAuthoringStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        IReadOnlyList<ContentVersionRecord> versions = await store.ListVersionsAsync();
        IReadOnlyList<ContentAuditEntry> audit = await store.ListAuditAsync(default, 0, 0, 1000);
        IReadOnlyList<ContentUpgradeRecord> ledger = store is IContentUpgradeLedger held
            ? await held.ListUpgradesAsync()
            : [];
        ContentDraft? draft = await store.GetOpenDraftAsync();
        return new CatalogFootprint(versions.Count, audit.Count, ledger.Count, draft?.EditCount ?? 0);
    }

    static string[] KeysOf(IReadOnlyList<ContentUpgradeIdentity> identities)
    {
        var keys = new string[identities.Count];
        for (int i = 0; i < identities.Count; i++)
        {
            keys[i] = identities[i].Key.ToString();
        }

        return keys;
    }
}
