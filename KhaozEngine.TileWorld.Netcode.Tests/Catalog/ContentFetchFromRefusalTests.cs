using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The seam between the door and the fetch loop, spec 8.5: refused with the server's version and hash, the
/// client fetches THAT version. It is tested here because it is the one place both halves are visible.
/// <c>KhaozEngine.Catalog</c> cannot see the refusal token at all, which is the point: the loop takes a
/// parsed <see cref="ContentVersionIdentity"/> and never a wire string, so no path exists in which a token
/// an unauthenticated party wrote becomes an address the client fetches from.
/// </summary>
public class ContentFetchFromRefusalTests
{
    [Fact]
    public async Task A_mismatch_refusal_drives_one_fetch_of_the_version_it_named()
    {
        var registry = new ContentTypeRegistry();
        var remote = new MemoryPackStore();
        var local = new MemoryPackStore();
        (ContentManifest manifest, string manifestHash, string ruleHash) = Publish(remote);

        string refusal = ContentRefusal.Mismatch(
            new ContentVersionIdentity((int)manifest.VersionNumber, manifestHash),
            new ContentVersionIdentity(3, new string('c', 64)));

        Assert.True(ContentRefusal.TryParseMismatch(refusal, out ContentVersionIdentity server, out _));

        var loop = new ContentFetchLoop(local, remote, registry, new ContentFetchOptions
        {
            ClientBuild = 1,
            Attempts = 1,
            BackoffStep = TimeSpan.Zero,
        });

        ContentFetchResult result = await loop.FetchAsync(server);

        Assert.True(result.Success);
        Assert.Equal(manifestHash, result.Version.ManifestHash);
        Assert.Equal(manifest.VersionNumber, result.Manifest?.VersionNumber);
        Assert.Equal([manifestHash, ruleHash], remote.Fetched);
        Assert.True(await local.ExistsAsync(ruleHash));
    }

    /// <summary>
    /// The smallest publishable version: no types yet, one empty remap rule chunk, no languages. It is
    /// enough for this seam, because the fact under test is WHICH version a refusal sends the client after.
    /// </summary>
    static (ContentManifest Manifest, string ManifestHash, string RuleHash) Publish(MemoryPackStore store)
    {
        byte[] ruleFile = ContentRuleChunkCodec.Encode([]);
        string ruleHash = ContentRuleChunkCodec.Hash([]);
        var manifest = new ContentManifest
        {
            Side = ContentManifestSide.Client,
            VersionNumber = 47,
            FormatGeneration = ContentPackFormat.Generation,
            MinimumServerBuild = 1,
            MinimumClientBuild = 1,
            RemapRuleChunkHash = ruleHash,
            Types = [],
            Languages = [],
        };

        string manifestHash = ContentManifestText.Hash(manifest);
        store.Plant(ruleHash, ruleFile);
        store.Plant(manifestHash, ContentManifestCodec.Encode(manifest));
        return (manifest, manifestHash, ruleHash);
    }

    /// <summary>An in-memory store that records what was asked of it, in call order.</summary>
    sealed class MemoryPackStore : IPackStore
    {
        readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        readonly List<string> _fetched = [];

        /// <summary>Every hash this store answered bytes for.</summary>
        public IReadOnlyList<string> Fetched => _fetched;

        /// <summary>Files bytes under a name without verifying they digest to it.</summary>
        public void Plant(string hash, byte[] bytes) => _objects[hash] = bytes;

        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(_objects.ContainsKey(hash));

        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(hash, out byte[]? bytes))
            {
                return Task.FromResult<ReadOnlyMemory<byte>?>(null);
            }

            _fetched.Add(hash);
            return Task.FromResult<ReadOnlyMemory<byte>?>(bytes);
        }

        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            _objects[hash] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListAsync(
            int versionNumber,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
