using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Upgrade;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

// Regression #1700. The operator import must carry the host policy to its actual seed publication.
public sealed class ConsumerPolicyImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperatorImportRefusalResetsStagingAndTheSamePolicyAllowsItsControl(bool sqlite)
    {
        using var fixture = new ConsumerPublishPolicyFixture(sqlite);
        await fixture.InitializeAsync();
        var request = new ContentPolicyImportRequest(new RefuseIncompatibleValue(), "admin", "operator", "seed");
        var importer = Assert.IsAssignableFrom<IContentPolicyImportStore>(fixture.Store);
        ContentBundle guarded = UpgradeFixtures.Target(fixture.Registry,
            UpgradeFixtures.Row(fixture.Type, 1, ConsumerPublishPolicyFixture.Key, 12));

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => importer.ImportWithPolicyAsync(guarded, request));

        Assert.Equal(ContentAuthoringException.CandidateInvalidReason, refused.Reason);
        ConsumerPublishPolicyFixture.AssertGuard(Assert.Single(refused.Findings,
            finding => finding.Code == ConsumerPublishPolicyFixture.RefusalCode));
        Assert.Equal(0, await fixture.Store.GetActiveVersionAsync());
        Assert.Empty(await fixture.Store.ListVersionsAsync());
        Assert.Null(await fixture.Store.GetOpenDraftAsync());
        ContentBundle control = UpgradeFixtures.Target(fixture.Registry,
            UpgradeFixtures.Row(fixture.Type, 1, ConsumerPublishPolicyFixture.Key, 11));
        Assert.Equal(1, (await importer.ImportWithPolicyAsync(control, request)).VersionNumber);
    }

    sealed class RefuseIncompatibleValue : IContentPublishPolicy
    {
        public void Validate(ContentPublishPolicySnapshot baseline, ContentPublishPolicySnapshot candidate,
            ICollection<ContentFinding> findings)
        {
            foreach (ContentPublishPolicyRow row in candidate.Rows(new ContentTypeId(PublishFixtures.ThingTypeId)))
            {
                if (row.GetValue(PublishFixtures.ValueField).Number == 12)
                {
                    findings.Add(row.Finding(ConsumerPublishPolicyFixture.RefusalCode, "Matching baked artifacts are required."));
                }
            }
        }
    }
}
