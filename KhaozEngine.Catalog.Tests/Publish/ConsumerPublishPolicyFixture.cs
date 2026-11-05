using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

internal sealed class ConsumerPublishPolicyFixture : IDisposable
{
    public const string Key = "artifact_value";
    public const string RefusalCode = "artifact-rebuild-required";
    readonly TemporaryCatalogDatabase _files = new();

    public ConsumerPublishPolicyFixture(bool sqlite)
    {
        Registry = PublishFixtures.Registry(PublishFixtures.Thing);
        Packs = _files.Pack();
        Store = sqlite
            ? new SqliteContentAuthoringStore(_files.ConnectionString, Registry, Packs)
            : new InMemoryContentAuthoringStore(Registry, Packs);
    }

    public ContentTypeRegistry Registry { get; }
    public FileSystemPackStore Packs { get; }
    public IContentAuthoringStore Store { get; }
    public ContentTypeId Type => new(PublishFixtures.ThingTypeId);
    public IContentPublishPolicy Policy { get; } = new GuardArtifactValue();

    public Task InitializeAsync()
        => Store is SqliteContentAuthoringStore sqlite
            ? sqlite.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate) : Task.CompletedTask;

    public async Task SeedAsync()
    {
        await InitializeAsync();

        await Store.ApplyEditsAsync([ContentEdit.Add(Type, new ContentKey(Key), PublishFixtures.Fields(11))],
            PublishFixtures.Actor, "operator", "seed");
        await Store.PublishAsync(PublishFixtures.Request(0));
    }

    public Task EditAsync(string name, ContentFieldValue value)
        => Store.ApplyEditsAsync([ContentEdit.Update(Type, 1, new ContentKey(Key), [new(name, value)])],
            PublishFixtures.Actor, "operator", "edit");

    public ContentPublishRequest Request() => PublishFixtures.Request(1) with { PublishPolicy = Policy };

    public async Task<ContentDraftCandidate> PreviewAsync()
        => ContentDraftCandidate.Build(await Store.ReadPublishBaselineAsync(),
            (await Store.GetOpenDraftAsync())!.Changes, Registry, Policy);

    public static void AssertGuard(ContentFinding finding)
    {
        Assert.Equal(RefusalCode, finding.Code);
        Assert.Equal(new ContentTypeId(PublishFixtures.ThingTypeId), finding.Type);
        Assert.Equal(1, finding.Id);
        Assert.Equal(Key, finding.Key);
    }

    public static async Task<string[]> HashesAsync(IPackStore packs, int version)
    {
        var hashes = new List<string>();
        await foreach (string hash in packs.ListAsync(version))
        {
            hashes.Add(hash);
        }

        return hashes.OrderBy(hash => hash, StringComparer.Ordinal).ToArray();
    }

    public void Dispose()
    {
        (Store as IDisposable)?.Dispose();
        _files.Dispose();
    }

    sealed class GuardArtifactValue : IContentPublishPolicy
    {
        public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
            ICollection<ContentFinding> findings)
        {
            foreach (ContentPublishPolicyRow row in candidate.Rows(new ContentTypeId(PublishFixtures.ThingTypeId)))
            {
                if (baseline.TryGetRow(row.Type, row.Key, out ContentPublishPolicyRow? old)
                    && old.GetValue(PublishFixtures.ValueField) != row.GetValue(PublishFixtures.ValueField))
                {
                    findings.Add(row.Finding(RefusalCode, "This value requires matching baked artifacts."));
                }
            }
        }
    }
}

// An edit lands after the legacy row-only pre-read and before its freeze marker is written.
internal sealed class EditBeforePolicyFreezeStore(IContentAuthoringStore inner, Func<Task> edit)
    : RowOnlyStoreView(inner), IContentAuthoringStore
{
    async Task IContentAuthoringStore.FreezeDraftAsync(int baseVersion, CancellationToken cancellationToken)
    {
        await edit();
        await base.FreezeDraftAsync(baseVersion, cancellationToken);
    }
}
