using System;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalCanonicalizerTests
{
    [Fact]
    public void Intent_version_one_matches_golden_bytes_and_digest()
    {
        var identity = new JournalOperationIdentity(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            "w/a",
            "x",
            new byte[] { 1, 2 });

        JournalFingerprint fingerprint = JournalCanonicalizer.CreateIntentFingerprint(identity, 1);

        Assert.Equal("4B4A4946000100010000001000112233445566778899AABBCCDDEEFF000200000003772F6100030000000178000400000006000000020102", Convert.ToHexString(fingerprint.CanonicalBytes.Span));
        Assert.Equal("22A6F54290004BA04E17359C90408B6DD09D563BEA78B6F64C143490BB7F5D87", Convert.ToHexString(fingerprint.Digest.Span));
    }

    [Fact]
    public void Commit_version_one_matches_golden_bytes_and_digest()
    {
        var identity = new JournalOperationIdentity(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "w/a", "x", new byte[] { 1, 2 });
        var commit = new JournalCommit(
            identity,
            new[] { new JournalStreamMutation("s", 2, new[] { new JournalEvent("e", 1, new byte[] { 0xAA }) }) },
            Array.Empty<JournalProjectionWrite>(),
            "r",
            1,
            new byte[] { 0xBB });

        JournalFingerprint fingerprint = JournalCanonicalizer.CreateCommitFingerprint(commit, 1);

        Assert.Equal("4B4A4546000100010000002022A6F54290004BA04E17359C90408B6DD09D563BEA78B6F64C143490BB7F5D8700020000004D0000000100000045000100000001730002000000080000000000000002000300000004000000010004000000200000001C000100000001650002000000040000000100030000000500000001AA00030000000400000000000400000001720005000000040000000100060000000500000001BB", Convert.ToHexString(fingerprint.CanonicalBytes.Span));
        Assert.Equal("699EAF85420B9744C46EAB18AA266AF302D5365BA802F173C76B89138C25E276", Convert.ToHexString(fingerprint.Digest.Span));
    }

    [Fact]
    public void Initialization_version_one_matches_golden_bytes_and_digest()
    {
        var identity = new JournalOperationIdentity(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "w/a", "x", new byte[] { 1, 2 });
        var initialization = new JournalInitialization(identity, "s", "snap", 1, new byte[] { 0xCC }, Array.Empty<JournalProjectionWrite>(), "r", 1, new byte[] { 0xDD });

        JournalFingerprint fingerprint = JournalCanonicalizer.CreateInitializationFingerprint(initialization, 1);

        Assert.Equal("4B4A4E46000100010000002022A6F54290004BA04E17359C90408B6DD09D563BEA78B6F64C143490BB7F5D8700020000000173000300000004736E61700004000000040000000100050000000500000001CC00060000000400000000000700000001720008000000040000000100090000000500000001DD", Convert.ToHexString(fingerprint.CanonicalBytes.Span));
        Assert.Equal("4C1B9D0B20DD5471ECE74BE99BA8F4AC6EC3F41AE6BBB52A4C2E0B919E553C11", Convert.ToHexString(fingerprint.Digest.Span));
    }

    [Fact]
    public void Caller_order_does_not_change_commit_fingerprint()
    {
        JournalCommit first = JournalTestData.Commit(
            streams: new[] { JournalTestData.Mutation("z"), JournalTestData.Mutation("a") },
            projections: new[] { JournalTestData.Projection("z", "z"), JournalTestData.Projection("a", "a") });
        JournalCommit second = JournalTestData.Commit(
            streams: new[] { JournalTestData.Mutation("a"), JournalTestData.Mutation("z") },
            projections: new[] { JournalTestData.Projection("a", "a"), JournalTestData.Projection("z", "z") });

        Assert.Equal(JournalCanonicalizer.CreateCommitFingerprint(first).Digest.ToArray(), JournalCanonicalizer.CreateCommitFingerprint(second).Digest.ToArray());
    }

    [Fact]
    public void Event_order_changes_commit_fingerprint()
    {
        JournalEvent a = JournalTestData.Event("a");
        JournalEvent b = JournalTestData.Event("b");
        JournalCommit first = JournalTestData.Commit(streams: new[] { JournalTestData.Mutation(events: new[] { a, b }) });
        JournalCommit second = JournalTestData.Commit(streams: new[] { JournalTestData.Mutation(events: new[] { b, a }) });

        Assert.NotEqual(JournalCanonicalizer.CreateCommitFingerprint(first).Digest.ToArray(), JournalCanonicalizer.CreateCommitFingerprint(second).Digest.ToArray());
    }

    [Fact]
    public void Version_one_resolver_replays_the_retained_golden_format()
    {
        JournalFingerprint resolved = JournalCanonicalizer.CreateIntentFingerprint(
            new JournalOperationIdentity(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "w/a", "x", new byte[] { 1, 2 }),
            formatVersion: 1);

        Assert.Equal((ushort)1, resolved.FormatVersion);
        Assert.Equal("22A6F54290004BA04E17359C90408B6DD09D563BEA78B6F64C143490BB7F5D87", Convert.ToHexString(resolved.Digest.Span));
        Assert.Throws<NotSupportedException>(() => JournalCanonicalizer.CreateIntentFingerprint(JournalTestData.Identity(), formatVersion: 2));
    }
}
