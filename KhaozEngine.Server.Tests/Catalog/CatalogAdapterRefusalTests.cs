using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

public sealed class CatalogAdapterRefusalTests
{
    [Theory]
    [InlineData("publish-in-progress", null, AdminActionStatus.Conflict)]
    [InlineData("publish-in-progress", ContentAuthoringRefusal.PublishInProgress, AdminActionStatus.Conflict)]
    [InlineData("edit-target-collision", null, AdminActionStatus.Conflict)]
    [InlineData("edit-target-collision", ContentAuthoringRefusal.EditTargetOperationCollision, AdminActionStatus.Conflict)]
    [InlineData("text-state-mismatch", null, AdminActionStatus.Conflict)]
    [InlineData("text-state-mismatch", ContentAuthoringRefusal.TextPlanDraftMismatch, AdminActionStatus.Conflict)]
    [InlineData("draft-open", null, AdminActionStatus.Conflict)]
    [InlineData("draft-open", ContentAuthoringRefusal.DraftOpen, AdminActionStatus.Conflict)]
    [InlineData("candidate-invalid", null, AdminActionStatus.BadRequest)]
    [InlineData("candidate-invalid", ContentAuthoringRefusal.CandidateRejected, AdminActionStatus.BadRequest)]
    [InlineData("catalog-not-empty", ContentAuthoringRefusal.CatalogNotEmpty, AdminActionStatus.BadRequest)]
    [InlineData("consumer-token", null, AdminActionStatus.BadRequest)]
    [InlineData(null, null, AdminActionStatus.BadRequest)]
    // Consumer-built exceptions may pair an additive enum with a different released token.
    [InlineData("publish-in-progress", ContentAuthoringRefusal.CandidateRejected, AdminActionStatus.Conflict)]
    [InlineData("consumer-token", ContentAuthoringRefusal.PublishInProgress, AdminActionStatus.BadRequest)]
    public void LegacyTokensKeepTheirStatusPayloadAndCallerSuppliedDetail(
        string? token, ContentAuthoringRefusal? refusal, AdminActionStatus status)
    {
        using var harness = new CatalogActionHarness();
        const string detail = "caller-supplied refusal detail";
        ContentFinding[] findings =
        [
            new(CatalogActionHarness.Thing, 7, "policy-first", "first detail") { Key = "first" },
            new(CatalogActionHarness.Tag, 8, "policy-second", "second detail"),
        ];
        var failure = refusal is { } typed
            ? new ContentAuthoringException(typed, detail, default, 0, token, findings)
            : new ContentAuthoringException(detail, default, 0, token, findings);

        AdminActionResult result = CatalogRefusal.From(failure, harness.Registry);
        JsonElement body = CatalogActionHarness.Wire(result);

        Assert.Equal(status, result.Status);
        Assert.Equal(token ?? "malformed-request", body.GetProperty("reason").GetString());
        Assert.Equal(detail, body.GetProperty("error").GetString());
        string[] names = body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (status == AdminActionStatus.Conflict)
        {
            Assert.Equal(new[] { "error", "reason", "remedy" }, names);
            Assert.False(string.IsNullOrEmpty(body.GetProperty("remedy").GetString()));
        }
        else
        {
            Assert.Equal(new[] { "error", "findingCount", "findings", "reason" }, names);
            Assert.Equal(2, body.GetProperty("findingCount").GetInt32());
            JsonElement[] rendered = body.GetProperty("findings").EnumerateArray().ToArray();
            Assert.Equal(2, rendered.Length);
            Assert.Equal(new[] { "policy-first", "policy-second" }, rendered.Select(finding => finding.GetProperty("code").GetString()));
            Assert.Equal(new[] { "thing", "tag" }, rendered.Select(finding => finding.GetProperty("type").GetString()));
            Assert.Equal(new[] { 7, 8 }, rendered.Select(finding => finding.GetProperty("id").GetInt32()));
            Assert.Equal(new[] { "first detail", "second detail" }, rendered.Select(finding => finding.GetProperty("message").GetString()));
            Assert.Equal("first", rendered[0].GetProperty("key").GetString());
            Assert.False(rendered[1].TryGetProperty("key", out _));
        }
    }

    [Fact]
    public async Task OperatorImportKeepsTheOriginalLowerRefusalAndItsContext()
    {
        using var source = new CatalogActionHarness();
        await source.PublishThingsAsync("artifact_value");
        ContentBundle bundle = await source.Store.ExportBundleAsync(1);
        using var target = new CatalogActionHarness();
        var cause = new InvalidOperationException("retained provider fault");
        var failure = new ContentAuthoringException(ContentAuthoringRefusal.PackUnreadable,
            "provider detail", CatalogActionHarness.Thing, 7, "pack-unreadable", cause)
        { VersionNumber = 1, ObjectHash = new string('a', 64), PackReaderReason = "manifest-absent" };
        var store = new PolicyImportStore(target.Store) { Failure = failure };
        var policy = new AcceptPolicy();
        string before = await target.SnapshotAsync();
        string epochBefore = await target.Store.GetStoreEpochAsync();

        var actual = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            CatalogOperatorImport.ImportAsync(store, bundle, CatalogActionHarness.Operator, "seed", policy, default));

        Assert.Same(failure, actual);
        Assert.Same(cause, actual.InnerException);
        Assert.Equal(before, await target.SnapshotAsync());
        Assert.Equal(epochBefore, await target.Store.GetStoreEpochAsync());
        Assert.Null(await target.Store.GetOpenDraftAsync());
        store.Failure = null;
        Assert.Equal(1, (await CatalogOperatorImport.ImportAsync(
            store, bundle, CatalogActionHarness.Operator, "seed", policy, default)).VersionNumber);
        Assert.Equal("artifact_value", Assert.Single((await target.Store.ExportBundleAsync(1)).Rows).Key.ToString());
    }

    [Theory]
    [InlineData("text-provenance-unknown", null)]
    [InlineData("text-provenance-unknown", ContentAuthoringRefusal.TextProvenanceUnknown)]
    [InlineData("text-operation-unavailable", null)]
    [InlineData("text-operation-unavailable", ContentAuthoringRefusal.CandidateRejected)]
    public async Task TextReadsKeepUnknownTextUnknownForBothLegacyAndTypedFailures(
        string token, ContentAuthoringRefusal? refusal)
    {
        using var harness = new CatalogActionHarness();
        await harness.PublishThingsAsync("artifact_value");
        var failure = Failure(token, refusal);
        var store = new TextReadStore(harness.Store) { Failure = failure };

        Assert.Null(await CatalogTextReads.SnapshotAsync(store, 1, default));
        store.Failure = null;
        ContentVersionTextSnapshot known = Assert.IsType<ContentVersionTextSnapshot>(
            await CatalogTextReads.SnapshotAsync(store, 1, default));
        Assert.Equal(1, known.VersionNumber);
        Assert.Equal(await harness.Store.GetStoreEpochAsync(), known.StoreEpoch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ContentAuthoringRefusal.TextProvenanceUnknown)]
    public async Task TextReadsPropagateOtherTokensEvenWhenTheEnumNamesUnknownText(ContentAuthoringRefusal? refusal)
    {
        using var harness = new CatalogActionHarness();
        await harness.PublishThingsAsync("artifact_value");
        var failure = Failure("consumer-token", refusal);
        var store = new TextReadStore(harness.Store) { Failure = failure };

        Assert.Same(failure, await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            CatalogTextReads.SnapshotAsync(store, 1, default)));
        store.Failure = null;
        Assert.NotNull(await CatalogTextReads.SnapshotAsync(store, 1, default));
    }

    static ContentAuthoringException Failure(string token, ContentAuthoringRefusal? refusal)
        => refusal is { } typed
            ? new ContentAuthoringException(typed, "consumer detail", default, 0, token)
            : new ContentAuthoringException("consumer detail", default, 0, token);

    sealed class AcceptPolicy : IContentPublishPolicy
    {
        public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
            System.Collections.Generic.ICollection<ContentFinding> findings) { }
    }

    sealed class PolicyImportStore(InMemoryContentAuthoringStore inner)
        : ForwardingContentAuthoringStore(inner), IContentPolicyImportStore
    {
        readonly IContentPolicyImportStore _policy = inner;
        public ContentAuthoringException? Failure { get; set; }

        public Task<ContentPublishResult> ImportWithPolicyAsync(
            ContentBundle bundle, ContentPolicyImportRequest request, CancellationToken cancellationToken = default)
            => Failure is { } failure ? Task.FromException<ContentPublishResult>(failure)
                : _policy.ImportWithPolicyAsync(bundle, request, cancellationToken);
    }

    sealed class TextReadStore(InMemoryContentAuthoringStore inner)
        : ForwardingContentAuthoringStore(inner), IContentTextAuthoringStore
    {
        readonly IContentTextAuthoringStore _text = inner;
        public ContentAuthoringException? Failure { get; set; }

        public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(int versionNumber, CancellationToken cancellationToken = default)
            => Failure is { } failure ? Task.FromException<ContentVersionTextSnapshot>(failure)
                : _text.ReadTextSnapshotAsync(versionNumber, cancellationToken);

        public Task<ContentDraft> ApplyChangesAsync(ContentAuthoringChanges changes, string actor, string operatorId,
            string note, CancellationToken cancellationToken = default)
            => _text.ApplyChangesAsync(changes, actor, operatorId, note, cancellationToken);

        public Task<ContentTextPublishSnapshot> FreezeChangesAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
            => _text.FreezeChangesAsync(expectedBaseVersion, cancellationToken);

        public Task<ContentVersionRecord> CommitTextPublishAsync(ContentTextPublishPlan plan, ContentPublishRequest request,
            IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default)
            => _text.CommitTextPublishAsync(plan, request, pointers, cancellationToken);

        public Task<bool> TryDiscardChangesAsync(ContentDraft expected, string actor, string operatorId,
            CancellationToken cancellationToken = default)
            => _text.TryDiscardChangesAsync(expected, actor, operatorId, cancellationToken);

        public Task<ContentPublishResult> ImportTextBundleAsync(ContentBundle bundle, string actor, string operatorId,
            string note, CancellationToken cancellationToken = default)
            => _text.ImportTextBundleAsync(bundle, actor, operatorId, note, cancellationToken);

        public Task<ContentDraft> RollbackTextToAsync(int targetVersion, string actor, string operatorId,
            string note, CancellationToken cancellationToken = default)
            => _text.RollbackTextToAsync(targetVersion, actor, operatorId, note, cancellationToken);
    }
}
