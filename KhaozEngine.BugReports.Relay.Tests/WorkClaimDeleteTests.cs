using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class WorkClaimDeleteTests
{
    const string Id = RelayHarness.Id;
    const string OtherId = "0193f3a4b5c67d8e9f0a1b2c3d4e5f60";
    static readonly CancellationToken Ct = CancellationToken.None;

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "issue", 3)]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment", 3)]
    [InlineData(ReportWorkKind.CreateIssue, "issue", 64)]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment", 64)]
    public async Task DeleteAll_DeletesEveryClaimEpochAndMarker(ReportWorkKind kind, string claimKind, int epochs)
    {
        DeletingMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new DeletingMediaStore(inner));
        await SeedAsync(harness, kind, claimKind, epochs);
        string unrelated = "claims/" + claimKind + "-" + OtherId + "-0.json";
        await harness.Media.PutAsync(ReportArea.Pending, unrelated, "{}"u8.ToArray(), ReportLayout.HeadersFor(unrelated), false, Ct);

        await new WorkClaim(store, harness.Clock).DeleteAllAsync(kind, Id, Ct);

        Assert.Equal(new[] { unrelated }, harness.Media.Paths(ReportArea.Pending)
            .Where(path => path.StartsWith("claims/", StringComparison.Ordinal)));
        string[] expected = Enumerable.Range(0, epochs).Reverse()
            .SelectMany(epoch => new[] { ".done", ".released", ".json" }
                .Select(extension => "claims/" + claimKind + "-" + Id + "-" + epoch + extension)).ToArray();
        Assert.Equal(expected, store.Deleted.Where(path => path.StartsWith("claims/" + claimKind + "-", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(ReportWorkKind.CreateIssue, "issue")]
    [InlineData(ReportWorkKind.AppendServerSnapshot, "comment")]
    public async Task DeleteAll_InterruptedDeletion_ResumesContiguousEpochs(ReportWorkKind kind, string claimKind)
    {
        DeletingMediaStore store = null!;
        var harness = new RelayHarness(wrapMedia: inner => store = new DeletingMediaStore(inner));
        await SeedAsync(harness, kind, claimKind, 3);
        string prefix = "claims/" + claimKind + "-" + Id + "-";
        var failure = new IOException("Delete interrupted");
        store.BeforeDelete = path => { if (path == prefix + "1.released") throw failure; };
        var claims = new WorkClaim(store, harness.Clock);

        Assert.Same(failure, await Record.ExceptionAsync(() => claims.DeleteAllAsync(kind, Id, Ct)));

        Assert.Equal(new[] { prefix + "0.done", prefix + "0.json", prefix + "0.released", prefix + "1.json", prefix + "1.released" },
            harness.Media.Paths(ReportArea.Pending).Where(path => path.StartsWith(prefix, StringComparison.Ordinal)));
        store.BeforeDelete = null;
        await claims.DeleteAllAsync(kind, Id, Ct);
        Assert.DoesNotContain(harness.Media.Paths(ReportArea.Pending), path => path.StartsWith(prefix, StringComparison.Ordinal));
    }

    static async Task SeedAsync(RelayHarness harness, ReportWorkKind kind, string claimKind, int epochs)
    {
        var claims = new WorkClaim(harness.Media, harness.Clock);
        for (int epoch = 0; epoch < epochs; epoch++)
        {
            await WorkerFixtures.SeedClaimAsync(harness, claimKind, epoch, harness.Clock.GetUtcNow());
            await claims.CompleteAsync(kind, Id, epoch, kind == ReportWorkKind.CreateIssue ? "9" : "commented");
            await claims.ReleaseAsync(kind, Id, epoch);
        }
    }

    sealed class DeletingMediaStore(InMemoryReportMediaStore inner) : IReportMediaStore
    {
        public List<string> Deleted { get; } = new();
        public Action<string>? BeforeDelete { get; set; }
        public Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content,
            ReportBlobHeaders headers, bool overwrite, CancellationToken cancellationToken)
            => inner.PutAsync(area, path, content, headers, overwrite, cancellationToken);
        public Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken)
            => inner.GetAsync(area, path, maxBytes, cancellationToken);
        public Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
            => inner.ExistsAsync(area, path, cancellationToken);
        public Task<IReadOnlyList<string>> ListAsync(ReportArea area, string prefix, CancellationToken cancellationToken)
            => area == ReportArea.Pending ? throw new InvalidOperationException("Claim deletion must not list Pending.")
                : inner.ListAsync(area, prefix, cancellationToken);
        public Task<bool> DeleteAsync(ReportArea area, string path, CancellationToken cancellationToken)
        {
            BeforeDelete?.Invoke(path);
            Deleted.Add(path);
            return inner.DeleteAsync(area, path, cancellationToken);
        }
    }
}
