using System;
using System.Collections.Generic;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.Tests.WorldStore.Journal;

internal static class JournalTestData
{
    internal static JournalOperationIdentity Identity(byte[]? intent = null)
        => new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), "world/account", "bank.deposit", intent ?? Array.Empty<byte>());

    internal static JournalEvent Event(string eventType = "item.added", byte[]? payload = null)
        => new(eventType, 1, payload ?? new byte[] { 1 });

    internal static JournalStreamMutation Mutation(
        string streamKey = "player/1",
        long expectedVersion = 0,
        IReadOnlyList<JournalEvent>? events = null)
        => new(streamKey, expectedVersion, events ?? new[] { Event() });

    internal static JournalProjectionWrite Projection(
        string streamKey = "player/1",
        string sectionName = "bag",
        byte[]? data = null)
        => new(streamKey, sectionName, "bag.v1", 1, data ?? new byte[] { 2 });

    internal static JournalCommit Commit(
        int streamCount = 1,
        IReadOnlyList<JournalStreamMutation>? streams = null,
        IReadOnlyList<JournalProjectionWrite>? projections = null,
        byte[]? result = null)
    {
        if (streams is null)
        {
            var generated = new JournalStreamMutation[streamCount];
            for (int i = 0; i < generated.Length; i++)
                generated[i] = Mutation(streamCount == 1 ? "player/1" : $"player/{i:D2}");
            streams = generated;
        }

        return new JournalCommit(Identity(), streams, projections ?? Array.Empty<JournalProjectionWrite>(), "result.v1", 1, result ?? Array.Empty<byte>());
    }

    internal static JournalInitialization Initialization(
        byte[]? snapshot = null,
        IReadOnlyList<JournalProjectionWrite>? projections = null,
        byte[]? result = null)
        => new(Identity(), "player/1", "player.v1", 1, snapshot ?? Array.Empty<byte>(), projections ?? Array.Empty<JournalProjectionWrite>(), "result.v1", 1, result ?? Array.Empty<byte>());
}
