using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;

namespace KhaozEngine.Tests.Catalog;

internal sealed class CatalogPinPolicyFixture : IDisposable
{
    public const string Key = "artifact_value";
    public const string Code = "artifact-rebuild-required";
    readonly CatalogActionHarness _files = new();

    public CatalogPinPolicyFixture(bool sqlite)
    {
        Store = sqlite
            ? new SqliteContentAuthoringStore("Data Source=:memory:", _files.Registry, _files.Pack)
            : _files.Store;
    }

    public IContentAuthoringStore Store { get; }
    public ContentTypeRegistry Registry => _files.Registry;
    public int Id { get; private set; }

    public async Task HistoryAsync(int first = 4200, int second = 4500)
    {
        await Store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await Store.ApplyEditsAsync([ContentEdit.Add(CatalogActionHarness.Thing, new ContentKey(Key), CatalogActionHarness.Fields(first))],
            "trusted-seed", "server", "matching artifacts");
        await Store.PublishAsync(Request(0));
        Id = (await Store.ListRowsAsync(CatalogActionHarness.Thing, 1, Key, true, 0, 10)).Rows[0].Id;
        await PublishAsync(second);
    }

    public async Task PublishAsync(int value, bool stackable = false)
    {
        int active = await Store.GetActiveVersionAsync();
        await Store.ApplyEditsAsync([ContentEdit.Update(CatalogActionHarness.Thing, Id, new ContentKey(Key),
            CatalogActionHarness.Fields(value, stackable))], "trusted-upgrade", "server", "matching artifacts");
        await Store.PublishAsync(Request(active));
    }

    public ServerAdmin Register(IContentPublishPolicy? policy, IContentAuthoringStore? store = null)
    {
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, store ?? Store, Registry, new CatalogAdminActionOptions { PublishPolicy = policy });
        return admin;
    }

    public void Dispose()
    {
        (Store as IDisposable)?.Dispose();
        _files.Dispose();
    }

    static ContentPublishRequest Request(int version) => new("trusted-server", "server", "matching artifacts", version);
}

internal sealed class GuardPinValue(bool fail = false) : IContentPublishPolicy
{
    public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
        ICollection<ContentFinding> findings)
    {
        if (fail)
        {
            throw new InvalidOperationException("Policy failure");
        }

        foreach (ContentPublishPolicyRow row in baseline.Rows(CatalogActionHarness.Thing))
        {
            if (!candidate.TryGetRow(row.Type, row.Key, out ContentPublishPolicyRow? next)
                || row.IsRetired != next.IsRetired
                || row.GetValue(CatalogActionHarness.ValueField) != next.GetValue(CatalogActionHarness.ValueField))
            {
                findings.Add(row.Finding(CatalogPinPolicyFixture.Code, "Matching baked artifacts are required."));
            }
        }
    }
}

internal class ForwardingPinSelectionStore(IContentAuthoringStore inner)
    : ForwardingContentAuthoringStore(inner), IContentVersionSelectionStore
{
    public virtual Task<bool> TrySetPinnedVersionAsync(int? version, ContentVersionSelection expected, string actor,
        string operatorId, CancellationToken cancellationToken = default)
        => ((IContentVersionSelectionStore)Inner).TrySetPinnedVersionAsync(version, expected, actor, operatorId, cancellationToken);
}

internal sealed class UnreadablePinRowsStore(IContentAuthoringStore inner, int unreadableVersion)
    : ForwardingPinSelectionStore(inner)
{
    public override Task<ContentSnapshot> LoadSnapshotAsync(int versionNumber, ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
        => versionNumber == unreadableVersion
            ? throw new InvalidOperationException("Published rows are unavailable")
            : base.LoadSnapshotAsync(versionNumber, registry, cancellationToken);
}

internal sealed class MismatchedPinSnapshotStore(IContentAuthoringStore inner) : ForwardingPinSelectionStore(inner)
{
    public override Task<ContentSnapshot> LoadSnapshotAsync(int versionNumber, ContentTypeRegistry registry,
        CancellationToken cancellationToken = default)
        => base.LoadSnapshotAsync(versionNumber == 1 ? 2 : versionNumber, registry, cancellationToken);
}

internal sealed class ChangedPinSelectionStore(IContentAuthoringStore inner, Func<Task> change)
    : ForwardingPinSelectionStore(inner)
{
    public override async Task<bool> TrySetPinnedVersionAsync(int? version, ContentVersionSelection expected, string actor,
        string operatorId, CancellationToken cancellationToken = default)
    {
        await change();
        return await base.TrySetPinnedVersionAsync(version, expected, actor, operatorId, cancellationToken);
    }
}

internal sealed class UnsupportedPinSelectionStore(IContentAuthoringStore inner) : ForwardingContentAuthoringStore(inner);

internal sealed class FailedPinAcknowledgementStore(IContentAuthoringStore inner) : ForwardingPinSelectionStore(inner)
{
    public override async Task<bool> TrySetPinnedVersionAsync(int? version, ContentVersionSelection expected, string actor,
        string operatorId, CancellationToken cancellationToken = default)
    {
        bool written = await base.TrySetPinnedVersionAsync(version, expected, actor, operatorId, cancellationToken);
        if (written)
        {
            throw new IOException("The committed write could not be acknowledged");
        }

        return false;
    }
}
