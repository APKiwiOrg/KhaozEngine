using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Concurrent misses on one material image identity decode it once (issue #1776). A miss registers its
/// identity as in flight before it decodes, so a second load of the same image joins that decode and shares its
/// source, and a failed decode reaches the load that joined it without being remembered, so the next load decodes
/// again. The gates hold the first decode open until the second caller has joined it, so no step depends on timing,
/// and a cache that let the second caller decode on its own fails the join check instead of hanging.</summary>
public sealed class MaterialImageCacheTests
{
    const string Identity = "sha256:material-image-cache-tests";

    [Fact]
    public async Task Concurrent_misses_on_one_identity_decode_once_and_share_the_source()
    {
        using var decode = new GatedDecode(failFirst: false);
        var cache = new MaterialImageCache(decode.Decode, _ => decode.Joined.Set());

        (MaterialImageSource? first, MaterialImageSource? second) = await decode.TwoMisses(cache);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, decode.Calls);
    }

    [Fact]
    public async Task A_failed_decode_reaches_its_waiter_and_the_next_miss_decodes_again()
    {
        using var decode = new GatedDecode(failFirst: true);
        var cache = new MaterialImageCache(decode.Decode, _ => decode.Joined.Set());

        (MaterialImageSource? first, MaterialImageSource? second) = await decode.TwoMisses(cache);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, decode.Calls);

        MaterialImageSource? retried = cache.Resolve(Identity, decode.Png);
        Assert.NotNull(retried);
        Assert.Equal(2, decode.Calls);
        // The source that did decode is kept while it is referenced, so a later load decodes nothing.
        Assert.Same(retried, cache.Resolve(Identity, decode.Png));
        Assert.Equal(2, decode.Calls);
    }

    /// <summary>A decode seam whose first call parks until released, and which fails that call on request. Later
    /// calls decode at once, which is what a cache that does not coordinate would make the second caller do.</summary>
    sealed class GatedDecode(bool failFirst) : IDisposable
    {
        readonly ManualResetEventSlim _entered = new();
        readonly ManualResetEventSlim _release = new();
        int _calls;

        internal ManualResetEventSlim Joined { get; } = new();
        internal byte[] Png { get; } = MaterialGlbFixture.UniquePng();
        internal int Calls => Volatile.Read(ref _calls);

        internal MaterialImageSource? Decode(string identity, ReadOnlySpan<byte> encoded)
        {
            if (Interlocked.Increment(ref _calls) > 1) return MaterialImageSource.TryCreate(identity, encoded);
            _entered.Set();
            _release.Wait();
            return failFirst ? null : MaterialImageSource.TryCreate(identity, encoded);
        }

        /// <summary>Two loads of one identity: the first parks inside its decode, the second starts only then, and
        /// the first is released only once the second has joined it or has finished without joining.</summary>
        internal async Task<(MaterialImageSource? First, MaterialImageSource? Second)> TwoMisses(MaterialImageCache cache)
        {
            Task<MaterialImageSource?> first = Task.Run(() => cache.Resolve(Identity, Png));
            Task<MaterialImageSource?>? second = null;
            try
            {
                WaitHandle.WaitAny([_entered.WaitHandle, ((IAsyncResult)first).AsyncWaitHandle]);
                Assert.True(_entered.IsSet, "the first miss never reached its decode");
                second = Task.Run(() => cache.Resolve(Identity, Png));
                WaitHandle.WaitAny([Joined.WaitHandle, ((IAsyncResult)second).AsyncWaitHandle]);
                Assert.True(Joined.IsSet, "the second miss decoded on its own instead of joining the decode in flight");
            }
            finally
            {
                _release.Set();
            }
            return (await first, await second!);
        }

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
            Joined.Dispose();
        }
    }
}
