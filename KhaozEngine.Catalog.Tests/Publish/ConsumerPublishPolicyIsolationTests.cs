using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

public sealed class ConsumerPublishPolicyIsolationTests
{
    [Fact]
    public async Task PolicyByteAccessCannotChangeTheBaselineCandidateOrItsOwnView()
    {
        using var files = new TemporaryCatalogDatabase();
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        var schema = new ContentFieldSchema([new("bytes", ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, true)]);
        var registry = new ContentTypeRegistry();
        registry.RegisterContentType(ContentRegistrationBand.Game, type.Value, "thing", new PublishCodec(type, schema),
            null, schema, ContentVisibility.Client, PublishFixtures.ChunkSlots);
        var store = new InMemoryContentAuthoringStore(registry, files.Pack());
        await store.ApplyEditsAsync([ContentEdit.Add(type, new ContentKey("payload"), [Bytes(1)])], "test", "test", "seed");
        await store.PublishAsync(PublishFixtures.Request(0));
        await store.ApplyEditsAsync([ContentEdit.Update(type, 1, new ContentKey("payload"), [Bytes(2)])], "test", "test", "edit");
        var policy = new MutatesReturnedBytes(type);

        ContentDraftCandidate preview = ContentDraftCandidate.Build(await store.ReadPublishBaselineAsync(),
            (await store.GetOpenDraftAsync())!.Changes, registry, policy);
        Assert.True(preview.Report.IsValid);
        Assert.Equal(new byte[] { 2, 3 }, Assert.Single(preview.Snapshot.Rows(type)).Fields[0].Bytes.ToArray());
        await store.PublishAsync(PublishFixtures.Request(1) with { PublishPolicy = policy });

        ContentRow before = Assert.Single((await store.ListRowsAsync(type, 1, null, true, 0, 10)).Rows);
        ContentRow after = Assert.Single((await store.ListRowsAsync(type, 2, null, true, 0, 10)).Rows);
        Assert.Equal(new byte[] { 1, 3 }, before.Fields[0].Bytes.ToArray());
        Assert.Equal(new byte[] { 2, 3 }, after.Fields[0].Bytes.ToArray());
    }

    [Fact]
    public async Task APolicyExceptionBecomesAValidationRefusalAndReleasesTheDraft()
    {
        using var fixture = new ConsumerPublishPolicyFixture(false);
        await fixture.SeedAsync();
        await fixture.EditAsync(PublishFixtures.LegacyField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1));
        var policy = new ThrowsDuringValidation();
        ContentDraftCandidate preview = ContentDraftCandidate.Build(await fixture.Store.ReadPublishBaselineAsync(),
            (await fixture.Store.GetOpenDraftAsync())!.Changes, fixture.Registry, policy);
        Assert.False(preview.Report.IsValid);
        Assert.Equal(IContentPublishPolicy.FailureCode, Assert.Single(preview.Report.Findings).Code);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => fixture.Store.PublishAsync(PublishFixtures.Request(1) with { PublishPolicy = policy }));
        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        Assert.Equal(IContentPublishPolicy.FailureCode, Assert.Single(refused.Findings).Code);
        Assert.Equal(1, await fixture.Store.GetActiveVersionAsync());
        Assert.False((await fixture.Store.GetOpenDraftAsync())!.IsFrozen);
    }

    static ContentFieldEdit Bytes(byte first)
        => new("bytes", ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, new byte[] { first, 3 }));

    sealed class MutatesReturnedBytes(ContentTypeId type) : IContentPublishPolicy
    {
        public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
            ICollection<ContentFinding> findings)
        {
            Mutate(baseline, findings);
            Mutate(candidate, findings);
        }

        void Mutate(ContentPublishPolicySnapshot snapshot, ICollection<ContentFinding> findings)
        {
            ContentPublishPolicyRow row = Assert.Single(snapshot.Rows(type));
            ContentFieldValue value = row.GetValue("bytes");
            byte original = value.Bytes.Span[0];
            if (MemoryMarshal.TryGetArray(value.Bytes, out ArraySegment<byte> segment))
            {
                segment.Array![segment.Offset] = 99;
            }

            if (row.GetValue("bytes").Bytes.Span[0] != original)
            {
                findings.Add(row.Finding("mutable-policy-row", "A policy changed its own row view."));
            }
        }
    }

    sealed class ThrowsDuringValidation : IContentPublishPolicy
    {
        public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
            ICollection<ContentFinding> findings) => throw new InvalidOperationException("Policy defect");
    }
}
