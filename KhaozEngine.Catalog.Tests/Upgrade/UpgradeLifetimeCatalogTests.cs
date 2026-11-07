using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Failed real pack setup must close the held unpooled SQLite connection before returning.</summary>
public sealed class UpgradeLifetimeCatalogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedFilesystemSetupClosesTheNativeConnectionAndPreservesFailures(bool cleanupFails)
    {
        using var files = new TemporaryCatalogDatabase();
        var packs = new ObservedFileSystemPacks(files.Pack());
        // The real filesystem provider must fail its shard creation, rather than a double throwing.
        Directory.Delete(files.PackRoot);
        await File.WriteAllTextAsync(files.PackRoot, "a file cannot contain pack shards");
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        using var actual = new SqliteContentAuthoringStore(files.ConnectionString, registry, packs);
        // File unlink on Unix does not prove closure. Observe the actual held provider and native handle.
        var lifetime = actual.CaptureConnectionLifetimeForTests();
        Assert.Equal(ConnectionState.Open, lifetime().State);
        Assert.False(lifetime().NativeHandleClosed);
        var cleanupFailure = new InvalidOperationException("intentional setup cleanup fault after native closure");
        IContentAuthoringStore store = cleanupFails ? new FailsAfterDisposeStore(actual, cleanupFailure) : actual;

        Exception escaped = await Assert.ThrowsAnyAsync<Exception>(() => UpgradeLifetimeCatalog.CreateAsync(store));

        Assert.True(lifetime().NativeHandleClosed, "failed catalog setup left its native SQLite connection open");
        Assert.Equal(ConnectionState.Closed, lifetime().State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => actual.GetActiveVersionAsync());
        Exception primary;
        if (cleanupFails)
        {
            AggregateException aggregate = Assert.IsType<AggregateException>(escaped);
            Assert.Collection(aggregate.InnerExceptions,
                first => Assert.Same(packs.WriteFailure, first), second => Assert.Same(cleanupFailure, second));
            primary = aggregate.InnerExceptions[0];
        }
        else
        {
            primary = escaped;
            Assert.Same(packs.WriteFailure, primary);
        }

        Assert.IsAssignableFrom<IOException>(primary);
    }

    sealed class FailsAfterDisposeStore(IContentAuthoringStore inner, Exception failure)
        : ForwardingContentAuthoringStore(inner), IDisposable
    {
        public void Dispose()
        {
            ((IDisposable)Inner).Dispose();
            throw failure;
        }
    }
}
