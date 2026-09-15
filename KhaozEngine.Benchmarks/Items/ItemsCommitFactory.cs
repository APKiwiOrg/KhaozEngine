using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The commit shapes spec 6.4 emits: ONE operation identity, ONE journal event per logical operation,
/// ONE projection write per touched page. The intent is the CLIENT operation's own canonical intent of
/// 10.6, which is 6.5's rule for a client-headed batch.
/// </summary>
internal static class ItemsCommitFactory
{
    internal const string Scope = "items-benchmark/world";
    internal const string CraftAction = "item-craft";
    internal const string CraftEventType = "item-crafted";
    internal const string PageSchema = "container.page.v2";
    internal const string ResultSchema = "item.craft.result.v1";

    internal static byte[] CraftIntent(int currencyId, int containerId, int slot, int sourceSlot, ulong instanceId)
    {
        Span<byte> buffer = stackalloc byte[48];
        int written = 0;
        buffer[written++] = 1;
        written += Varint.Write(buffer[written..], (ulong)(uint)currencyId);
        written += Varint.Write(buffer[written..], (ulong)(uint)containerId);
        written += Varint.Write(buffer[written..], (ulong)(uint)slot);
        written += Varint.Write(buffer[written..], (ulong)(uint)containerId);
        written += Varint.Write(buffer[written..], (ulong)(uint)sourceSlot);
        written += Varint.Write(buffer[written..], instanceId);
        written += Varint.Write(buffer[written..], 0);
        return buffer[..written].ToArray();
    }

    internal static byte[] CraftEvent(
        int currencyId,
        ulong instanceId,
        int contentVersion,
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after)
    {
        Span<byte> buffer = stackalloc byte[1_280];
        int written = 0;
        buffer[written++] = 1;
        written += Varint.Write(buffer[written..], (ulong)(uint)currencyId);
        written += Varint.Write(buffer[written..], instanceId);
        written += Varint.Write(buffer[written..], (ulong)(uint)contentVersion);
        written += Varint.Write(buffer[written..], (ulong)(uint)before.Length);
        before.CopyTo(buffer[written..]);
        written += before.Length;
        written += Varint.Write(buffer[written..], (ulong)(uint)after.Length);
        after.CopyTo(buffer[written..]);
        written += after.Length;
        return buffer[..written].ToArray();
    }

    internal static JournalCommit Build(
        Guid operationId,
        string streamKey,
        long expectedVersion,
        byte[] intent,
        IReadOnlyList<JournalEvent> events,
        IReadOnlyList<JournalProjectionWrite> projections,
        byte[] result)
        => new(
            new JournalOperationIdentity(operationId, Scope, CraftAction, intent),
            new[] { new JournalStreamMutation(streamKey, expectedVersion, events) },
            projections,
            ResultSchema,
            1,
            result);

    internal static Guid OperationId(int seed, string domain, long ordinal)
    {
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{seed}:{domain}:{ordinal}")));
        return new Guid(digest.AsSpan(0, 16));
    }

    internal static async System.Threading.Tasks.Task InitializeAsync(IMutationJournalStore store, string streamKey, int seed, int pages)
    {
        var projections = new JournalProjectionWrite[pages];
        for (int page = 0; page < pages; page++)
            projections[page] = new JournalProjectionWrite(
                streamKey,
                ContainerPageCodec.SectionName("bank", page),
                PageSchema,
                1,
                new byte[] { 2, 0 });
        var initialization = new JournalInitialization(
            new JournalOperationIdentity(
                OperationId(seed, $"initialize:{streamKey}", 0),
                Scope,
                "items.initialize",
                Encoding.ASCII.GetBytes(streamKey)),
            streamKey,
            "player.snapshot.v1",
            1,
            new byte[] { 1 },
            projections,
            "initialize.result.v1",
            1,
            new byte[] { 1 });
        JournalInitializeResult result = await store.InitializeAsync(initialization).ConfigureAwait(false);
        if (result.Status is not (JournalInitializeStatus.Initialized or JournalInitializeStatus.Replayed or JournalInitializeStatus.ExistingStream))
            throw new InvalidOperationException($"Stream initialization failed with {result.Status}.");
    }
}
