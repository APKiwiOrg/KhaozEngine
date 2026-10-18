using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

/// <summary>
/// The behaviour every <see cref="IReportMediaStore"/> owes the relay. Self-contained so a backend test project can link
/// this file and run the same facts over its own store.
/// </summary>
public abstract class ReportMediaStoreConformance
{
    const string Path = "g/2026-10/a/x.json";
    static readonly ReportBlobHeaders Json = new("application/json; charset=utf-8", "no-cache");

    /// <summary>Builds a fresh, empty store for one fact.</summary>
    protected abstract IReportMediaStore CreateStore();

    [Fact]
    public async Task Put_ThenGet_RoundTripsBytes()
    {
        IReportMediaStore store = CreateStore();

        Assert.True(await store.PutAsync(ReportArea.Public, Path, new byte[] { 1, 2, 3 }, Json, false, CancellationToken.None));

        ReadOnlyMemory<byte>? read = await store.GetAsync(ReportArea.Public, Path, 1024, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.Value.ToArray());
    }

    [Fact]
    public async Task Put_WithoutOverwrite_ExistingPath_ReturnsFalseAndKeepsFirst()
    {
        IReportMediaStore store = CreateStore();
        await store.PutAsync(ReportArea.Public, Path, new byte[] { 1, 2, 3 }, Json, false, CancellationToken.None);

        bool wrote = await store.PutAsync(ReportArea.Public, Path, new byte[] { 9 }, Json, false, CancellationToken.None);

        Assert.False(wrote);
        ReadOnlyMemory<byte>? read = await store.GetAsync(ReportArea.Public, Path, 1024, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, read!.Value.ToArray());
    }

    [Fact]
    public async Task Put_WithoutOverwrite_ConcurrentCallers_ExactlyOneWins()
    {
        const int callers = 8;
        IReportMediaStore store = CreateStore();
        using var start = new Barrier(callers);
        var writes = new Task<bool>[callers];
        for (int i = 0; i < callers; i++)
        {
            byte[] payload = { (byte)(i + 1), (byte)(i + 1) };
            // A dedicated thread per caller, released together by the barrier, so the calls overlap even when a store
            // completes synchronously and the pool has fewer free threads than callers.
            writes[i] = Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                return store.PutAsync(ReportArea.Public, Path, payload, Json, false, CancellationToken.None);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        bool[] results = await Task.WhenAll(writes);

        int winner = Assert.Single(Enumerable.Range(0, callers), i => results[i]);
        ReadOnlyMemory<byte>? read = await store.GetAsync(ReportArea.Public, Path, 1024, CancellationToken.None);
        Assert.Equal(new[] { (byte)(winner + 1), (byte)(winner + 1) }, read!.Value.ToArray());
    }

    [Fact]
    public async Task Put_WithOverwrite_ReplacesAndReturnsTrue()
    {
        IReportMediaStore store = CreateStore();
        await store.PutAsync(ReportArea.Public, Path, new byte[] { 1, 2, 3 }, Json, false, CancellationToken.None);

        bool wrote = await store.PutAsync(ReportArea.Public, Path, new byte[] { 9 }, Json, true, CancellationToken.None);

        Assert.True(wrote);
        ReadOnlyMemory<byte>? read = await store.GetAsync(ReportArea.Public, Path, 1024, CancellationToken.None);
        Assert.Equal(new byte[] { 9 }, read!.Value.ToArray());
    }

    [Fact]
    public async Task Get_Absent_ReturnsNull()
    {
        IReportMediaStore store = CreateStore();

        Assert.Null(await store.GetAsync(ReportArea.Pending, "accepted/none.json", 1024, CancellationToken.None));
    }

    [Fact]
    public async Task Get_OverMaxBytes_ThrowsInvalidDataException()
    {
        IReportMediaStore store = CreateStore();
        await store.PutAsync(ReportArea.Public, Path, new byte[10], Json, false, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(ReportArea.Public, Path, 9, CancellationToken.None));
    }

    [Fact]
    public async Task Areas_AreIndependent()
    {
        IReportMediaStore store = CreateStore();
        Assert.True(await store.PutAsync(ReportArea.Public, Path, new byte[] { 1 }, Json, false, CancellationToken.None));
        Assert.True(await store.PutAsync(ReportArea.Pending, Path, new byte[] { 2 }, Json, false, CancellationToken.None));

        ReadOnlyMemory<byte>? publicBytes = await store.GetAsync(ReportArea.Public, Path, 1024, CancellationToken.None);
        ReadOnlyMemory<byte>? pendingBytes = await store.GetAsync(ReportArea.Pending, Path, 1024, CancellationToken.None);

        Assert.Equal(new byte[] { 1 }, publicBytes!.Value.ToArray());
        Assert.Equal(new byte[] { 2 }, pendingBytes!.Value.ToArray());
    }

    [Fact]
    public async Task Exists_ReflectsPut()
    {
        IReportMediaStore store = CreateStore();
        Assert.False(await store.ExistsAsync(ReportArea.Public, Path, CancellationToken.None));

        await store.PutAsync(ReportArea.Public, Path, new byte[] { 1 }, Json, false, CancellationToken.None);

        Assert.True(await store.ExistsAsync(ReportArea.Public, Path, CancellationToken.None));
        Assert.False(await store.ExistsAsync(ReportArea.Pending, Path, CancellationToken.None));
    }
}
