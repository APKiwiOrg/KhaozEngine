using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The DOOR every socket write goes through, which spec 10.5 says is where a game operation's four
/// refusals live and which the socket primitives are only one caller of.
/// <para>
/// A game operation builds its own <c>InstanceSocket</c> list and calls <c>SetSockets</c> directly, so a
/// rule that lives in <c>CraftPrimitives.Socket</c> alone is a rule an operation walks straight past.
/// Contracts 9.5's one level limit, the socket type's own <c>max_nested_bytes</c> and standing rule 2 over
/// the nested affix lists are all door rules for that reason.
/// </para>
/// <para>
/// Every registry and snapshot a fact builds is its OWN, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public sealed class CraftSocketDoorTests
{
    /// <summary>A socket type whose <c>max_nested_bytes</c> is too small for a two field payload.</summary>
    const int TinySocket = 4;

    /// <summary>That type's authored budget, in bytes.</summary>
    const int TinyBudget = 3;

    /// <summary>A prefix mod row carrying <c>legacy</c>, which is what standing rule 2 reads.</summary>
    const int AncientMod = 20;

    [Fact]
    public void SetSockets_refuses_a_nested_payload_that_itself_NESTS_and_one_past_the_type_budget()
    {
        CraftWorld world = SocketWorld();

        // Contracts 9.5's one level limit. The door used to ask the STRUCTURAL IsCanonical, which treats
        // every kind as unknown and never recurses, so a nested payload carrying kind 132 sailed through it
        // and the limit was enforced by CraftPrimitives.Socket alone.
        byte[] nests = Nested(builder => builder.AddSockets([new InstanceSocket(GemSocket, 0, 0, default)]));
        CraftWorkingCopy nesting = world.Open(Target());
        Assert.False(nesting.SetSockets([new InstanceSocket(GemSocket, Wand, 5, nests)]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.NestedPayloadNests, InstancePropertyKind.Sockets),
            nesting.Refusal);
        Assert.False(nesting.TryEncode(out _));

        // And the socket type's own max_nested_bytes, which lived in the same one caller.
        byte[] big = Nested(builder => builder
            .AddScalar(InstancePropertyKind.ItemLevel, 60)
            .AddScalar(InstancePropertyKind.Quality, 20));
        Assert.True(big.Length > TinyBudget);
        CraftWorkingCopy oversized = world.Open(Target());
        Assert.False(oversized.SetSockets([new InstanceSocket(TinySocket, Wand, 5, big)]));
        Assert.Equal(new CraftRefusal(CraftRefusalKind.NestedPayloadTooLong, TinyBudget), oversized.Refusal);

        // A budget of 0 in content MEANS the whole payload cap, and a socket type id of 0 is the one and
        // only "no restriction", so the same bytes go in through either.
        CraftWorkingCopy whole = world.Open(Target());
        Assert.True(whole.SetSockets([new InstanceSocket(GemSocket, Wand, 5, big)]));
        CraftWorkingCopy free = world.Open(Target());
        Assert.True(free.SetSockets([new InstanceSocket(0, Wand, 5, big)]));
    }

    [Fact]
    public void A_game_operation_cannot_move_a_LEGACY_entry_inside_a_socketed_item()
    {
        CraftWorld world = SocketWorld();
        byte[] held = Nested(builder => builder.AddAffixes(
            InstancePropertyKind.Affixes,
            [new InstanceAffix(AncientMod, 1, 100)]));
        byte[] moved = Nested(builder => builder.AddAffixes(
            InstancePropertyKind.Affixes,
            [new InstanceAffix(AncientMod, 1, 65_535)]));
        byte[] stored = Target(new InstanceSocket(GemSocket, Wand, 5, held));

        // Standing rule 2 reaches INSIDE the socket. Without it an operation rebuilds the nested payload
        // with the legacy entry's roll position moved and the door takes it, which is the farm for maximum
        // rolls the frozen reading exists to close.
        CraftWorkingCopy copy = world.Open(stored);
        Assert.False(copy.SetSockets([new InstanceSocket(GemSocket, Wand, 5, moved)]));
        Assert.Equal(new CraftRefusal(CraftRefusalKind.LegacyEntryFrozen, AncientMod), copy.Refusal);

        // Writing the SAME entry back is not a move, and neither is dropping it, which is a loss rather
        // than a gain and which rule 2 permits.
        CraftWorkingCopy same = world.Open(stored);
        Assert.True(same.SetSockets([new InstanceSocket(GemSocket, Wand, 5, held)]));
        CraftWorkingCopy dropped = world.Open(stored);
        byte[] bare = Nested(builder => builder.AddScalar(InstancePropertyKind.ItemLevel, 12));
        Assert.True(dropped.SetSockets([new InstanceSocket(GemSocket, Wand, 5, bare)]));

        // And an entry naming a legacy row the nested payload does NOT carry is an ADDITION, which is rule
        // 3 rather than rule 2 and answers the guard it names.
        CraftWorkingCopy added = world.Open(Target(new InstanceSocket(GemSocket, Wand, 5, bare)));
        Assert.False(added.SetSockets([new InstanceSocket(GemSocket, Wand, 5, held)]));
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.GuardFailed, (int)CraftGuardKind.NotLegacy),
            added.Refusal);

        // An ORDINARY entry moves freely, so what refused is the legacy row rather than the nesting.
        byte[] ordinary = Nested(builder => builder.AddAffixes(
            InstancePropertyKind.Affixes,
            [new InstanceAffix(1, 1, 100)]));
        byte[] shifted = Nested(builder => builder.AddAffixes(
            InstancePropertyKind.Affixes,
            [new InstanceAffix(1, 1, 65_535)]));
        CraftWorkingCopy plain = world.Open(Target(new InstanceSocket(GemSocket, Wand, 5, ordinary)));
        Assert.True(plain.SetSockets([new InstanceSocket(GemSocket, Wand, 5, shifted)]));
    }

    /// <summary>The generation world plus a tiny budget socket type and one legacy mod.</summary>
    static CraftWorld SocketWorld()
    {
        ContentTypeRegistry registry = World();
        List<ContentRow> rows = Rows(registry);
        rows.AddRange(
        [
            SocketType(registry, TinySocket, "tiny_socket", maxNestedBytes: TinyBudget),
            Mod(registry, AncientMod, "ancient", kind: ModContentType.PrefixKind, legacy: true),
            ModTier(registry, 20, "ancient_t1", modId: AncientMod, ordinal: 1),
        ]);

        return new CraftWorld(registry, Snapshot(registry, [.. rows]), Greatsword);
    }

    /// <summary>One target carrying the sockets a fact hands in, and nothing else a door rule reads.</summary>
    static byte[] Target(params InstanceSocket[] sockets)
    {
        var builder = new ItemInstancePayloadBuilder();
        _ = builder.AddScalar(InstancePropertyKind.ItemLevel, 60);
        if (sockets.Length > 0)
        {
            _ = builder.AddSockets(sockets);
        }

        return builder.ToArray();
    }

    /// <summary>One nested payload, built through the one encoder the format has.</summary>
    static byte[] Nested(Action<ItemInstancePayloadBuilder> author)
    {
        var builder = new ItemInstancePayloadBuilder();
        author(builder);
        return builder.ToArray();
    }
}
