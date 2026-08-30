using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The publisher both servers end their tick on (#134): rebuild into a reused buffer, publish a fresh immutable
/// array only when the content changed. Enlisted in the non-parallel <c>AllocSensitive</c> collection for the
/// steady-state zero-allocation assertion, which is the whole point of the type.
/// </summary>
[Collection("AllocSensitive")]
public class OnlineSnapshotPublisherTests
{
    private static OnlinePlayer Player(int slot, float x) =>
        new(slot, "acct" + slot, "Name" + slot, new Vector3(x, 0f, 0f), true, 0f, 100 + slot);

    private static void Fill(OnlineSnapshotPublisher publisher, params OnlinePlayer[] players)
    {
        List<OnlinePlayer> buffer = publisher.BeginRebuild();
        foreach (OnlinePlayer p in players) buffer.Add(p);
    }

    [Fact]
    public void FirstDifferentRebuildPublishes()
    {
        var admin = new AdminCommandBuffer();
        var publisher = new OnlineSnapshotPublisher();

        Fill(publisher, Player(0, 1f));

        Assert.True(publisher.PublishIfChanged(admin));
        Assert.Single(admin.Online);
        Assert.Equal("acct0", admin.Online[0].AccountId);
    }

    [Fact]
    public void AnIdenticalRebuildDoesNotRepublish()
    {
        var admin = new AdminCommandBuffer();
        var publisher = new OnlineSnapshotPublisher();
        Fill(publisher, Player(0, 1f));
        publisher.PublishIfChanged(admin);
        IReadOnlyList<OnlinePlayer> first = admin.Online;

        Fill(publisher, Player(0, 1f));

        Assert.False(publisher.PublishIfChanged(admin));
        Assert.Same(first, admin.Online);
    }

    [Fact]
    public void AChangedFieldRepublishes()
    {
        var admin = new AdminCommandBuffer();
        var publisher = new OnlineSnapshotPublisher();
        Fill(publisher, Player(0, 1f));
        publisher.PublishIfChanged(admin);

        Fill(publisher, Player(0, 2f));   // same player, moved

        Assert.True(publisher.PublishIfChanged(admin));
        Assert.Equal(2f, admin.Online[0].Position.X);
    }

    [Fact]
    public void ACountChangeRepublishesEvenWhenThePrefixMatches()
    {
        var admin = new AdminCommandBuffer();
        var publisher = new OnlineSnapshotPublisher();
        Fill(publisher, Player(0, 1f));
        publisher.PublishIfChanged(admin);

        Fill(publisher, Player(0, 1f), Player(1, 5f));

        Assert.True(publisher.PublishIfChanged(admin));
        Assert.Equal(2, admin.Online.Count);
    }

    [Fact]
    public void EmptyingThePopulationRepublishes()
    {
        var admin = new AdminCommandBuffer();
        var publisher = new OnlineSnapshotPublisher();
        Fill(publisher, Player(0, 1f));
        publisher.PublishIfChanged(admin);

        publisher.BeginRebuild();   // everyone left

        Assert.True(publisher.PublishIfChanged(admin));
        Assert.Empty(admin.Online);
    }

    [Fact]
    public void ThePublishedArrayIsACopy_NotTheReusedBuffer()
    {
        // The published list is read lock-free from another thread, so it must never alias the buffer the next
        // tick is about to clear and refill.
        var admin = new AdminCommandBuffer();
        var publisher = new OnlineSnapshotPublisher();
        Fill(publisher, Player(0, 1f));
        publisher.PublishIfChanged(admin);
        IReadOnlyList<OnlinePlayer> published = admin.Online;

        Fill(publisher, Player(7, 99f));   // the next tick's rebuild overwrites the buffer

        Assert.Single(published);
        Assert.Equal("acct0", published[0].AccountId);
    }

    [Fact]
    public void AnUnchangedRebuildAllocatesNothing()
    {
        var admin = new AdminCommandBuffer();
        var publisher = new OnlineSnapshotPublisher();
        var population = new OnlinePlayer[64];
        for (int i = 0; i < population.Length; i++) population[i] = Player(i, i);

        // Warm up: the first publish allocates the array and grows the buffer to its steady-state capacity.
        for (int warm = 0; warm < 3; warm++)
        {
            Fill(publisher, population);
            publisher.PublishIfChanged(admin);
        }

        AllocAssert.NoPerCallAllocation("a steady-state online-snapshot rebuild", () =>
        {
            for (int tick = 0; tick < 100; tick++)
            {
                Fill(publisher, population);
                Assert.False(publisher.PublishIfChanged(admin));
            }
        });
    }
}
