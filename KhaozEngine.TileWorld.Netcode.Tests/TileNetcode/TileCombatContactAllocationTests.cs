using System;
using System.Reflection;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

[Collection("AllocSensitive")]
public class TileCombatContactAllocationTests
{
    [Fact]
    public void Disabled_reads_allocate_nothing_and_keep_contact_runtime_null()
    {
        using var scenario = new ContactContractScenario();
        scenario.Snapshot(10, (ContactContractScenario.RemoteId,
            TileMoveState.At(new TileCoord(22, 20, 0), TileDirection.S)));
        scenario.Client.AdvancePresentation(.1f);
        for (int i = 0; i < 128; i++) ReadContact(scenario.Client);

        const int iterations = 1024;
        int hits = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++) hits += ReadContact(scenario.Client);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(iterations * 2, hits);
        Assert.Equal(0L, allocated);
        AssertNoContactRuntime(scenario.Client);
    }

    [Fact]
    public void Disabled_snapshot_and_presentation_allocate_the_same_as_raw_reads()
    {
        using var scenario = new ContactContractScenario();
        byte[] payload = scenario.Payload((ContactContractScenario.RemoteId,
            TileMoveState.At(new TileCoord(22, 20, 0), TileDirection.S)));
        long tick = 10;
        for (int i = 0; i < 128; i++)
        {
            scenario.ApplySnapshot(tick++, payload);
            scenario.Client.AdvancePresentation(1f / 60f);
            ReadRaw(scenario.Client);
            ReadContact(scenario.Client);
        }

        long raw = MeasureFrames(scenario, payload, ref tick, contact: false);
        long contact = MeasureFrames(scenario, payload, ref tick, contact: true);

        Assert.True(raw > 0);
        Assert.Equal(raw, contact);
        AssertNoContactRuntime(scenario.Client);
    }

    static long MeasureFrames(ContactContractScenario scenario, byte[] payload, ref long tick, bool contact)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++)
        {
            scenario.ApplySnapshot(tick++, payload);
            scenario.Client.AdvancePresentation(1f / 60f);
            if (contact) ReadContact(scenario.Client);
            else ReadRaw(scenario.Client);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static int ReadContact(TileWorldClient client)
    {
        int hits = client.TryGetCombatBodyPresentation(client.LocalNetId, out _) ? 1 : 0;
        if (client.TryGetCombatBodyPresentation(ContactContractScenario.RemoteId, out _)) hits++;
        if (client.TryGetCombatBodyPresentation(9999, out _)) hits++;
        hits += client.CombatContactImpacts.Count;
        hits += (int)client.CombatContactMissCount;
        if (client.TryTransferCombatBodyPresentation(client.LocalNetId, ContactContractScenario.RemoteId)) hits++;
        return hits;
    }

    static void ReadRaw(TileWorldClient client)
    {
        _ = client.LocalPose;
        client.TryGetRemotePose(ContactContractScenario.RemoteId, out _);
        client.TryGetRemotePose(9999, out _);
    }

    static void AssertNoContactRuntime(TileWorldClient client)
    {
        FieldInfo? field = typeof(TileWorldClient).GetField("combatContactPresentation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        Assert.Null(field.GetValue(client));
    }
}
