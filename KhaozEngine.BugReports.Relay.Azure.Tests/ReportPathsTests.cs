using System;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>The public path rule is usable by backends and enforced at every media operation.</summary>
public sealed class ReportPathsTests
{
    [Theory]
    [InlineData("g/2026-10/id-token/viewer.html", true)]
    [InlineData("claims/createissue-id-1.done", true)]
    [InlineData("a_b/0.-", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("a/../b", false)]
    [InlineData("a/", false)]
    [InlineData("/a", false)]
    [InlineData("a//b", false)]
    [InlineData("A.json", false)]
    [InlineData("a%20b", false)]
    [InlineData("a\\b", false)]
    public async Task PublicRule_AndAzureStore_EnforcePortablePaths(string path, bool valid)
    {
        Assert.True(typeof(ReportPaths).IsVisible);
        Assert.Equal(valid, ReportPaths.IsValid(path));
        var store = new BlobReportMediaStore(new InMemoryReportBlobContainer(), new InMemoryReportBlobContainer());
        if (valid)
        {
            Assert.True(await store.PutAsync(ReportArea.Pending, path, new byte[] { 1 }, new("text/plain", "no-cache"), false, default));
            Assert.True(await store.ExistsAsync(ReportArea.Pending, path, default));
            Assert.Equal(new byte[] { 1 }, (await store.GetAsync(ReportArea.Pending, path, 1, default))!.Value.ToArray());
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(ReportArea.Pending, path, new byte[] { 1 }, new("text/plain", "no-cache"), false, default));
            await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(ReportArea.Pending, path, 1, default));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ExistsAsync(ReportArea.Pending, path, default));
        }
    }
}
