using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.WorldStore.Journal;
using Xunit;
using static KhaozEngine.Tests.Server.ItemInstances.ContainerCommitFixtures;

namespace KhaozEngine.Tests.Server.ItemInstances;

public sealed class CraftIntentCompatibilityTests
{
    static readonly byte[] LegacyCanonical =
        [6, 4, (byte)'b', (byte)'a', (byte)'n', (byte)'k', 4,
            4, (byte)'b', (byte)'a', (byte)'n', (byte)'k', 0, 0, 0, 0xD9, 0x36];

    [Fact]
    public void A_new_craft_intent_names_the_plan_before_the_unchanged_canonical_parameters()
    {
        PagedItemContainer bank = Container(1);
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(bank);
        Assert.True(batch.Apply(Craft().FromClient(Guid.NewGuid())));

        byte[] expected = [0, 1, 7, .. LegacyCanonical];
        Assert.Equal(expected, batch.BuildIntent());
    }

    [Fact]
    public void A_client_can_build_the_craft_intent_before_resolving_an_outcome()
    {
        ContainerOperation request = Craft() with { Payload = default, EventPayload = default };
        byte[] expected = [0, 1, 7, .. LegacyCanonical];

        Assert.Equal(expected, ContainerOperationIntent.ForCraft(request, craftPlanId: 7));
    }

    [Fact]
    public void Historical_craft_parameters_and_schema_two_envelopes_keep_their_exact_bytes()
    {
        ContainerOperation craft = Craft();
        Assert.Equal(LegacyCanonical, craft.ToCanonicalArray());
        Assert.True(ContainerOperation.TryReadCanonical(LegacyCanonical,
            out ContainerOperation canonical, out string? reason), reason);
        Assert.Equal(ContainerOperationKind.Craft, canonical.Kind);
        Assert.Equal(Instance, canonical.InstanceId);

        byte[] audit = [1, 7, 0xD9, 0x36, 7, 3, 2, 1, 42, 3, 2, 1, 43];
        byte[] envelope = [17, .. LegacyCanonical, 13, .. audit];
        (int schema, byte[] body) = ContainerOperationEventCodec.Write(craft);
        Assert.Equal(2, schema);
        Assert.Equal(envelope, body);
        Assert.True(ContainerOperationEventCodec.TryRead(ItemInstanceEvents.Crafted, 2, envelope,
            out ContainerOperation read, out reason), reason);
        Assert.Equal(audit, read.EventPayload.ToArray());
        Assert.Equal(Payload(43), read.Payload.ToArray());
    }

    [Fact]
    public void A_server_batch_names_every_craft_plan_in_order()
    {
        PagedItemContainer bank = Container(1);
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(bank);
        Assert.True(batch.Apply(Craft()));
        byte[] after = Payload(44);
        Assert.True(batch.Apply(ContainerOperation.Craft(Bank, 4, Instance, after,
            new ItemCraftedEvent(8, Instance, 7, Payload(43), after).ToArray())));

        byte[] expected = [2, 0, 1, 7, .. LegacyCanonical, 0, 1, 8, .. LegacyCanonical];
        Assert.Equal(expected, batch.BuildIntent());
    }

    [Fact]
    public void Admission_budgets_the_plan_identity_before_mutating_a_container()
    {
        PagedItemContainer bank = Container(1);
        SeatItem(bank, 4, Sword, Instance);
        ContainerCommitBuilder batch = OpenBank(bank, options: new ContainerCommitOptions
        {
            Limits = new JournalLimits(normalizedIntentBytes: LegacyCanonical.Length),
        });

        Assert.False(batch.Apply(Craft().FromClient(Guid.NewGuid())));
        Assert.Equal(Payload(), bank.SlotAt(4).Payload.ToArray());
        Assert.Equal(0, bank.DirtyPageCount);
        Assert.Empty(batch.Operations);
    }

    static ContainerOperation Craft()
        => ContainerOperation.Craft(Bank, 4, Instance, Payload(43),
            new ItemCraftedEvent(7, Instance, 7, Payload(), Payload(43)).ToArray());
}
