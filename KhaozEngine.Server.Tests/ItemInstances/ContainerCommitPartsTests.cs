using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// A batch's parts, handed out for a commit the host composes across streams
/// (https://github.com/APKiwiOrg/KhaozEngine/issues/1044). <c>Close</c> is the convenience over them, so the
/// parts with nothing added have to compose the commit <c>Close</c> builds, and the limits have to be checked on
/// the composed total rather than on a part.
/// </summary>
public sealed class ContainerCommitPartsTests
{
    /// <summary>A second stream a host's own commit writes beside the container's, a loot source.</summary>
    const string LootStream = "loot:9";

    static readonly byte[] Result = [3, 1];

    static PagedItemContainer SeededBank()
    {
        PagedItemContainer bank = Container();
        SeatItem(bank, 4, Sword, Instance);
        SeatStack(bank, 5, Currency, 20);
        SeatStack(bank, 6, Potion, 9);
        return bank;
    }

    static void ApplyTheSameWork(ContainerCommitBuilder batch)
    {
        Assert.True(batch.Apply(ContainerOperation.Move(Bank, 4, Bank, 150, 1, Instance)));
        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 6, 2)));
        Assert.True(batch.Apply(ContainerOperation.Craft(
            Bank, 150, Instance, Payload(7), CraftEventBody(0), currencySlot: 5, currencyDefinitionId: Currency, currencyCount: 1)));
    }

    /// <summary>What a host writes when it composes a commit from the parts and adds nothing, which is
    /// exactly what <c>Close</c> writes.</summary>
    static JournalCommit Compose(
        ContainerCommitBuilder batch,
        Guid operationId,
        IReadOnlyList<JournalEvent> events,
        IReadOnlyList<JournalProjectionWrite> writes,
        params JournalStreamMutation[] others)
    {
        var streams = new List<JournalStreamMutation> { new(batch.StreamKey, batch.Options.ExpectedVersion, events) };
        streams.AddRange(others);
        return new JournalCommit(
            new JournalOperationIdentity(operationId, batch.Scope, batch.ActionKind, batch.BuildIntent()),
            streams,
            writes,
            batch.Options.ResultSchema,
            batch.Options.ResultSchemaVersion,
            Result,
            batch.PresentAtCommit,
            batch.Options.QueueBehindAdmitted);
    }

    static void AssertSameCommit(JournalCommit expected, JournalCommit actual)
    {
        Assert.Equal(
            JournalCanonicalizer.CreateCommitFingerprint(expected).CanonicalBytes.ToArray(),
            JournalCanonicalizer.CreateCommitFingerprint(actual).CanonicalBytes.ToArray());
        Assert.Equal(expected.PresentAtCommit, actual.PresentAtCommit);
        Assert.Equal(expected.QueueBehindAdmitted, actual.QueueBehindAdmitted);
    }

    [Fact]
    public void Parts_with_nothing_added_compose_the_commit_Close_builds()
    {
        ContainerCommitBuilder closing = OpenBank(SeededBank());
        ApplyTheSameWork(closing);
        JournalCommit closed = closing.Close(Mint(ServerId), Result);

        PagedItemContainer bank = SeededBank();
        ContainerCommitBuilder parting = OpenBank(bank);
        ApplyTheSameWork(parting);
        Assert.True(parting.TryBuildParts(
            out IReadOnlyList<JournalEvent> events, out IReadOnlyList<JournalProjectionWrite> writes));

        Assert.Equal(3, events.Count);
        Assert.Equal(2, writes.Count);
        JournalCommit composed = Compose(parting, ServerId, events, writes);
        composed.Validate(parting.Options.Limits);
        AssertSameCommit(closed, composed);

        // Taking the parts closes the batch exactly as Close does, so nothing joins behind them and the same
        // work cannot be taken twice.
        Assert.Equal(ContainerBatchCloseReason.Closed, parting.Window.CloseReason);
        Assert.Throws<InvalidOperationException>(() => parting.TryBuildParts(out _, out _));
        Assert.Throws<InvalidOperationException>(() => parting.Close(Mint(ServerId)));
        Assert.Throws<InvalidOperationException>(() => parting.Apply(ContainerOperation.Take(Bank, 6, 1)));

        // The pages stay dirty until the COMPOSED commit lands, exactly as after Close.
        Assert.Equal(2, bank.DirtyPageCount);
        parting.MarkCommitted();
        Assert.Equal(0, bank.DirtyPageCount);
    }

    [Fact]
    public void A_client_headed_batch_and_a_present_at_commit_batch_compose_what_Close_builds_too()
    {
        // The two rules Close applies beyond the parts, and both reachable from public members: a client headed
        // batch commits under the client's own id and intent, and value moving between accounts carries
        // PresentAtCommit into whatever it rides in.
        Guid clientId = Guid.NewGuid();
        ContainerOperation click = ContainerOperation.Take(Bank, 6, 2).FromClient(clientId);
        ContainerOperation trade = ContainerOperation.Take(Bank, 4, 1, Instance) with { PresentAtCommit = true };

        foreach (ContainerOperation head in (ContainerOperation[])[click, trade])
        {
            ContainerCommitBuilder closing = OpenBank(SeededBank());
            Assert.True(closing.Apply(head));
            JournalCommit closed = closing.Close(Mint(ServerId), Result);

            ContainerCommitBuilder parting = OpenBank(SeededBank());
            Assert.True(parting.Apply(head));
            Assert.True(parting.TryBuildParts(out IReadOnlyList<JournalEvent> events, out IReadOnlyList<JournalProjectionWrite> writes));

            Guid operationId = parting.Window.HoldsClientOperation ? parting.Operations[0].OperationId : ServerId;
            AssertSameCommit(closed, Compose(parting, operationId, events, writes));
            Assert.Equal(head.PresentAtCommit, parting.PresentAtCommit);
        }
    }

    [Fact]
    public void A_batch_holding_no_operation_has_no_parts_and_stays_open()
    {
        PagedItemContainer bank = SeededBank();
        ContainerCommitBuilder batch = OpenBank(bank);

        Assert.False(batch.TryBuildParts(out IReadOnlyList<JournalEvent> events, out IReadOnlyList<JournalProjectionWrite> writes));
        Assert.Empty(events);
        Assert.Empty(writes);
        Assert.True(batch.Window.IsOpen);

        Assert.True(batch.Apply(ContainerOperation.Take(Bank, 6, 1)));
        Assert.True(batch.TryBuildParts(out events, out writes));
        Assert.Single(events);
        Assert.Single(writes);
    }

    [Fact]
    public void A_composed_commit_is_checked_on_its_real_total_and_a_part_never_is()
    {
        // The batch is bounded to three events. The window lets exactly three join, because that is THIS
        // batch's share, and the parts are handed out whole. The host then adds its own stream, and only the
        // composed commit can see the fourth event: the journal's own Validate refuses it on the real total.
        var options = new ContainerCommitOptions { Limits = new JournalLimits(eventsPerOperation: 3) };
        ContainerCommitBuilder batch = OpenBank(SeededBank(), options: options);
        for (int take = 0; take < 3; take++) Assert.True(batch.Apply(ContainerOperation.Take(Bank, 6, 1)));
        Assert.False(batch.Apply(ContainerOperation.Take(Bank, 6, 1)));
        Assert.Equal(ContainerBatchCloseReason.LimitReached, batch.Window.CloseReason);

        Assert.True(batch.TryBuildParts(out IReadOnlyList<JournalEvent> events, out IReadOnlyList<JournalProjectionWrite> writes));

        JournalCommit alone = Compose(batch, ServerId, events, writes);
        alone.Validate(batch.Options.Limits);

        var loot = new JournalStreamMutation(LootStream, 0, [new JournalEvent("loot-claimed", 1, [1])]);
        JournalCommit composed = Compose(batch, ServerId, events, writes, loot);
        Assert.Equal(4, composed.StreamMutations[0].Events.Count + composed.StreamMutations[1].Events.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => composed.Validate(batch.Options.Limits));

        // The engine maxima are the constructor's own check, and a host that reserves room for what it adds
        // opens the batch with the lower limit and validates the composed commit against the full one.
        composed.Validate(JournalLimits.Maximum);
    }

    [Fact]
    public void Parts_that_throw_leave_the_batch_where_it_was()
    {
        // The same reachable throw Close has: a name four short of the identity cap opens at page 0 and cannot
        // name page 100. Nothing is recorded until the parts are built, so the batch is still open.
        string wide = new('b', JournalLimits.EngineMaximumIdentityCharacters - 4);
        PagedItemContainer bank = Container(pageCount: 101);
        ContainerCommitBuilder batch = ContainerCommitBuilder.Open(
            StreamKey, ItemInstanceEvents.CraftActionKind, Scope, Containers((wide, bank)), tick: 4);
        Assert.True(batch.Apply(ContainerOperation.Grant(wide, slot: 10_000, Sword, 1, Instance, Payload())));

        Assert.Throws<ArgumentException>(() => batch.TryBuildParts(out _, out _));

        Assert.True(batch.Window.IsOpen);
        Assert.Equal(1, batch.ProjectionWriteCount);
    }
}
