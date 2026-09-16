using System;
using System.IO;
using System.Reflection;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Visibility;

/// <summary>
/// Spec 12.5's rule in order, spec 17 row 9, and the two projections spec 7.4 and 7.6 ask for. There is
/// exactly ONE function answering "may this viewer see this field", so the table-driven fact below drives
/// that same function from BOTH call shapes and from the projection the replication filter runs, and a
/// tooltip that computed its own answer would go red here.
/// <para>
/// Every fact builds its own registry through <see cref="VisibilityFixtures"/>, so nothing here writes
/// process-global state and no collection attribute is needed.
/// </para>
/// </summary>
public class ItemInstanceVisibilityTests
{
    [Fact]
    public void ServerOnly_is_never_visible_to_anyone_including_the_owner()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        foreach (PropertyVisibility viewer in Levels)
        {
            Assert.False(ItemInstanceVisibility.CanSee(
                registry,
                VisibilityFixtures.ServerSecret,
                viewer,
                identified: true,
                revealedMask: ulong.MaxValue));
        }
    }

    [Fact]
    public void OwnerOnly_is_visible_only_when_the_viewer_level_is_OwnerOnly()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        Assert.True(Sees(registry, InstancePropertyKind.Durability, PropertyVisibility.OwnerOnly));
        Assert.False(Sees(registry, InstancePropertyKind.Durability, PropertyVisibility.Everyone));
        Assert.False(Sees(registry, InstancePropertyKind.Durability, PropertyVisibility.ServerOnly));
    }

    [Fact]
    public void Everyone_is_always_visible()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        Assert.True(Sees(registry, InstancePropertyKind.ItemLevel, PropertyVisibility.Everyone));
        Assert.True(Sees(registry, InstancePropertyKind.ItemLevel, PropertyVisibility.OwnerOnly));
    }

    [Fact]
    public void A_viewer_level_of_ServerOnly_sees_nothing_because_it_is_not_a_viewer_level()
    {
        // Contracts 11.2 gives a viewer two levels, Everyone and OwnerOnly. ServerOnly is a level a KIND
        // carries, so a caller passing it here has a bug, and the answer it gets is the EMPTY view rather
        // than a privileged one: the one privileged reader in this design is the server reading its own
        // stored bytes, which never goes through this function at all.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        Assert.False(Sees(registry, InstancePropertyKind.ItemLevel, PropertyVisibility.ServerOnly));
        Assert.False(Sees(registry, InstancePropertyKind.Durability, PropertyVisibility.ServerOnly));
        Assert.False(Sees(registry, VisibilityFixtures.ServerSecret, PropertyVisibility.ServerOnly));

        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: uint.MaxValue);
        var view = new byte[payload.Length];
        Assert.Equal(0, ItemInstanceVisibility.PublicView(
            registry,
            payload,
            PropertyVisibility.ServerOnly,
            identified: true,
            revealedMask: ulong.MaxValue,
            view));
    }

    [Fact]
    public void A_gated_kind_is_hidden_from_the_OWNER_too_while_unidentified()
    {
        // Gate 0 decision 8, and it is why unidentified is a mechanic rather than a fourth visibility
        // level: the gate runs AFTER the three levels have already said yes.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        foreach (ushort kind in new ushort[]
        {
            InstancePropertyKind.UniqueTemplate,
            InstancePropertyKind.Affixes,
            InstancePropertyKind.Enchantments,
            InstancePropertyKind.RareName,
        })
        {
            Assert.False(ItemInstanceVisibility.CanSee(
                registry, kind, PropertyVisibility.OwnerOnly, identified: false, revealedMask: 0));
            Assert.True(ItemInstanceVisibility.CanSee(
                registry, kind, PropertyVisibility.OwnerOnly, identified: true, revealedMask: 0));
        }

        // The ungated public kinds beside them are untouched by the gate.
        Assert.True(ItemInstanceVisibility.CanSee(
            registry, InstancePropertyKind.Rarity, PropertyVisibility.Everyone, identified: false, revealedMask: 0));

        // A kind that is owner-only AND gated needs both answers, and the gate wins while it is shut.
        Assert.False(ItemInstanceVisibility.CanSee(
            registry, VisibilityFixtures.OwnerSecret, PropertyVisibility.OwnerOnly, identified: false, revealedMask: 0));
    }

    [Fact]
    public void A_set_RevealedMask_bit_reveals_exactly_its_own_registered_kind()
    {
        // Spec 12.7: bit N is the bit the kind was REGISTERED with, never its position in the ascending
        // list of gated kinds. A partial reveal of bit 1 uncovers the affixes and nothing else.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        Assert.True(registry.TryGetByIdentificationMaskBit(1, out InstancePropertyRegistration? affixes));
        Assert.Equal(InstancePropertyKind.Affixes, affixes.Kind);

        ulong mask = 1UL << affixes.IdentificationMaskBit;

        Assert.True(ItemInstanceVisibility.CanSee(
            registry, InstancePropertyKind.Affixes, PropertyVisibility.Everyone, identified: false, mask));
        Assert.False(ItemInstanceVisibility.CanSee(
            registry, InstancePropertyKind.UniqueTemplate, PropertyVisibility.Everyone, identified: false, mask));
        Assert.False(ItemInstanceVisibility.CanSee(
            registry, InstancePropertyKind.Enchantments, PropertyVisibility.Everyone, identified: false, mask));
        Assert.False(ItemInstanceVisibility.CanSee(
            registry, InstancePropertyKind.RareName, PropertyVisibility.Everyone, identified: false, mask));
    }

    [Fact]
    public void The_revealed_mask_is_a_ulong_so_a_decoded_sixty_four_bit_mask_never_narrows()
    {
        // Kind 128's mask is a varint and the shape walk reads a varint at the full 64 bits (#917), so the
        // value reaching this function can carry bits above 31. The registry caps a REGISTERED bit at
        // MaxIdentificationMaskBit, so the high bits gate nothing, and taking a ulong here is what keeps
        // that a documented no-op rather than a silent narrowing at the call site.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        const ulong highBitsOnly = 0xFFFF_FFFF_0000_0000UL;

        Assert.False(ItemInstanceVisibility.CanSee(
            registry, VisibilityFixtures.HighBitGated, PropertyVisibility.Everyone, identified: false, highBitsOnly));
        Assert.True(ItemInstanceVisibility.CanSee(
            registry,
            VisibilityFixtures.HighBitGated,
            PropertyVisibility.Everyone,
            identified: false,
            highBitsOnly | (1UL << VisibilityFixtures.HighBit)));
        Assert.True(ItemInstanceVisibility.CanSee(
            registry, VisibilityFixtures.HighBitGated, PropertyVisibility.Everyone, identified: false, ulong.MaxValue));
    }

    [Fact]
    public void An_unregistered_kind_is_visible_to_nobody()
    {
        // Contracts 11.2 is an IF AND ONLY IF over the kind's registered visibility, and an unregistered
        // kind has none, so there is nothing to compare and the answer is no. Failing closed is the only
        // safe direction: a kind this process cannot classify might be ServerOnly in the build that wrote
        // it, and the cost of the other choice is leaking it to every viewer.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        foreach (PropertyVisibility viewer in Levels)
        {
            Assert.False(ItemInstanceVisibility.CanSee(
                registry, VisibilityFixtures.UnregisteredKind, viewer, identified: true, revealedMask: ulong.MaxValue));
        }

        // And the projection drops it, so an unknown kind is kept verbatim in STORAGE (contracts 9.4) and
        // still never reaches a viewer.
        byte[] payload = new ItemInstancePayloadBuilder()
            .AddScalar(InstancePropertyKind.ItemLevel, 68)
            .AddScalar(VisibilityFixtures.UnregisteredKind, 9)
            .ToArray();

        var view = new byte[payload.Length];
        int written = ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.OwnerOnly, identified: true, revealedMask: ulong.MaxValue, view);

        Assert.False(VisibilityFixtures.Carries(registry, view.AsSpan(0, written), VisibilityFixtures.UnregisteredKind));
        Assert.True(VisibilityFixtures.Carries(registry, view.AsSpan(0, written), InstancePropertyKind.ItemLevel));
    }

    /// <summary>
    /// Spec 17 row 9: every registered kind, times the three levels, times identified and not. Each case
    /// asserts the two CALL SHAPES agree with each other and that the PROJECTION the replication filter
    /// runs agrees with both, which is the whole content of "there is exactly one function".
    /// </summary>
    public static TheoryData<ushort, PropertyVisibility, bool> EveryKindAtEveryLevel()
    {
        var data = new TheoryData<ushort, PropertyVisibility, bool>();
        foreach (ushort kind in VisibilityFixtures.RegisteredKinds())
        {
            foreach (PropertyVisibility viewer in Levels)
            {
                data.Add(kind, viewer, true);
                data.Add(kind, viewer, false);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryKindAtEveryLevel))]
    public void The_replication_filter_and_the_tooltip_builder_agree_on_every_kind_at_every_level(
        ushort kind,
        PropertyVisibility viewer,
        bool identified)
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        Assert.True(registry.TryGet(kind, out InstancePropertyRegistration? registration));

        // The tooltip builder holds a kind id and goes through the registry. The replication filter has
        // already walked the registry and holds the registration. Same function, two doors.
        bool byRegistration = ItemInstanceVisibility.CanSee(registration, viewer, identified, revealedMask: 0);
        bool byKind = ItemInstanceVisibility.CanSee(registry, kind, viewer, identified, revealedMask: 0);
        Assert.Equal(byRegistration, byKind);

        // And the projection is that same answer applied to bytes.
        byte[] payload = VisibilityFixtures.EveryKind(identified, revealedMask: 0);
        var view = new byte[payload.Length];
        int written = ItemInstanceVisibility.PublicView(
            registry, payload, viewer, identified, revealedMask: 0, view);
        Assert.True(written >= 0);

        Assert.Equal(byKind, VisibilityFixtures.Carries(registry, view.AsSpan(0, written), kind));
    }

    [Fact]
    public void PublicView_of_a_rare_drops_kinds_4_5_and_6_and_keeps_the_rest()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        var view = new byte[payload.Length];
        int written = ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);

        ReadOnlySpan<byte> kept = view.AsSpan(0, written);
        foreach (ushort dropped in new ushort[]
        {
            InstancePropertyKind.Charges,
            InstancePropertyKind.Durability,
            InstancePropertyKind.BoundTo,
            VisibilityFixtures.OwnerSecret,
            VisibilityFixtures.ServerSecret,
        })
        {
            Assert.False(VisibilityFixtures.Carries(registry, kept, dropped));
        }

        foreach (ushort held in new ushort[]
        {
            InstancePropertyKind.Flags,
            InstancePropertyKind.ItemLevel,
            InstancePropertyKind.Quality,
            InstancePropertyKind.Materials,
            InstancePropertyKind.Tier,
            InstancePropertyKind.Identification,
            InstancePropertyKind.UniqueTemplate,
            InstancePropertyKind.Rarity,
            InstancePropertyKind.Affixes,
            InstancePropertyKind.Sockets,
            InstancePropertyKind.Enchantments,
            InstancePropertyKind.RareName,
        })
        {
            Assert.True(VisibilityFixtures.Carries(registry, kept, held));
        }
    }

    [Fact]
    public void A_socketed_gems_owner_only_fields_do_not_reach_a_public_viewer()
    {
        // Kind 132 is Everyone and its entry ends in a nested payload, so a projection that kept or
        // dropped whole top-level fields kept the socket VERBATIM and shipped the gem's kind 5 and kind 6
        // inside it, to every viewer including a passer-by reading a ground stack. Contracts 11.2 is an if
        // and only if over the kind's own visibility and it does not stop at the first level.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        var view = new byte[payload.Length];
        int written = ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);

        ReadOnlySpan<byte> kept = view.AsSpan(0, written);
        Assert.True(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Sockets));

        byte[] nested = VisibilityFixtures.SocketNested(registry, kept);
        Assert.True(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.ItemLevel));
        Assert.False(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.Durability));
        Assert.False(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.BoundTo));

        // The same claim over the bytes, at any depth, which is what the leak looked like: the gem's whole
        // durability field and its whole bound-to field, verbatim, inside a field the viewer may see.
        Assert.Equal(-1, kept.IndexOf(new byte[] { 0x05, 0x02, 0x5A, 0x64 }));
        Assert.Equal(-1, kept.IndexOf(new byte[] { 0x06, 0x03, 0xB1, 0xB6, 0x3C }));

        // A rebuilt field is still a field, so the view still decodes and is still canonical.
        Assert.Null(ItemInstancePayload.Validate(registry, kept));
    }

    [Fact]
    public void OwnerRemainder_carries_a_socketed_gems_owner_only_fields()
    {
        // The mirror of the fact above, and the reason the two are fixed together: while the public view
        // leaked the gem's owner-only bytes, a remainder that dropped kind 132 whole was self-consistent
        // with it. The owner sees the gem's durability through the remainder or not at all.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        var remainder = new byte[payload.Length];
        int written = ItemInstanceVisibility.OwnerRemainder(
            registry, payload, identified: true, revealedMask: 0, remainder);

        ReadOnlySpan<byte> kept = remainder.AsSpan(0, written);
        Assert.Null(ItemInstancePayload.Validate(registry, kept));

        byte[] nested = VisibilityFixtures.SocketNested(registry, kept);
        Assert.True(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.Durability));
        Assert.True(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.BoundTo));

        // And nothing the public view already carried, one level down as at the top.
        Assert.False(VisibilityFixtures.Carries(registry, nested, InstancePropertyKind.ItemLevel));
    }

    [Fact]
    public void A_socket_holding_nothing_owner_only_is_absent_from_the_remainder_entirely()
    {
        // The remainder carries a socket's FRAME only to position the owner-only bytes inside it, so a
        // socket with none leaves no frame behind. That is what keeps a ground stack's remainder empty.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        byte[] golden = Golden("spec-3-8-poe-greatsword.bin");

        var remainder = new byte[golden.Length];
        int written = ItemInstanceVisibility.OwnerRemainder(
            registry, golden, identified: true, revealedMask: 0, remainder);

        ReadOnlySpan<byte> kept = remainder.AsSpan(0, written);
        Assert.True(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Durability));
        Assert.False(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Sockets));
    }

    [Fact]
    public void PublicView_output_is_still_canonical_and_still_decodes()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        foreach (PropertyVisibility viewer in Levels)
        {
            foreach (bool identified in new[] { true, false })
            {
                byte[] payload = VisibilityFixtures.EveryKind(identified, revealedMask: 0);
                var view = new byte[payload.Length];
                int written = ItemInstanceVisibility.PublicView(
                    registry, payload, viewer, identified, revealedMask: 0, view);

                // Dropping whole fields cannot break the three canonical rules, because what is left is
                // still a subsequence of an ascending, duplicate-free, minimally encoded list.
                Assert.Null(ItemInstancePayload.Validate(registry, view.AsSpan(0, written)));
            }
        }
    }

    [Fact]
    public void PublicView_allocates_nothing_beyond_its_destination_span()
    {
        // The absence of allocation is a property of the SIGNATURE: the view is written into a caller's
        // span and the length comes back as an int, so there is nothing for the method to hand out.
        MethodInfo method = typeof(ItemInstanceVisibility).GetMethod(nameof(ItemInstanceVisibility.PublicView))!;
        Assert.Equal(typeof(int), method.ReturnType);
        Assert.Equal(typeof(Span<byte>), method.GetParameters()[^1].ParameterType);

        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);
        var view = new byte[payload.Length];

        // Warm the path first, so a tier 0 compile is not measured as an allocation.
        for (int index = 0; index < 64; index++)
        {
            ItemInstanceVisibility.PublicView(
                registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 256; index++)
        {
            ItemInstanceVisibility.PublicView(
                registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void A_destination_too_short_answers_minus_one_and_writes_nothing()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        var full = new byte[payload.Length];
        int written = ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, full);

        var tooShort = new byte[written - 1];
        Assert.Equal(-1, ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, tooShort));

        // Nothing was written, so a caller that ignores the answer cannot ship a truncated view.
        Assert.All(tooShort, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_rares_fifty_eight_byte_golden_replicates_as_fifty_four_bytes_on_the_ground()
    {
        // Spec 7.4's ground rule and budget 11's input. The rare holds one of the three OwnerOnly kinds,
        // kind 5 durability, whose whole field is four bytes: one kind varint, one length varint and a two
        // byte body of 90 and 100. 58 minus 4 is 54. Kinds 4 and 6 are the other two OwnerOnly kinds and
        // this item carries neither, so nothing else is stripped.
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        byte[] golden = Golden("spec-3-8-poe-greatsword.bin");
        Assert.Equal(58, golden.Length);

        var view = new byte[golden.Length];
        int written = ItemInstanceVisibility.PublicView(
            registry, golden, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);

        Assert.Equal(54, written);
        Assert.False(VisibilityFixtures.Carries(registry, view.AsSpan(0, written), InstancePropertyKind.Durability));
        Assert.True(VisibilityFixtures.Carries(registry, view.AsSpan(0, written), InstancePropertyKind.Affixes));

        // A drop has no owner, so there is no owner remainder message for it: the component carries the
        // public view and that is the whole of what a passer-by ever receives. Asking the ground bytes for
        // a remainder answers zero, because the owner-only kinds are already gone.
        var remainder = new byte[golden.Length];
        Assert.Equal(0, ItemInstanceVisibility.OwnerRemainder(
            registry, view.AsSpan(0, written), identified: true, revealedMask: 0, remainder));
    }

    [Fact]
    public void An_unidentified_rares_public_view_drops_the_gated_affixes_and_rare_name()
    {
        InstancePropertyRegistry registry = InstancePropertyRegistry.CreateV1();
        byte[] golden = Golden("spec-3-8-poe-greatsword.bin");

        var view = new byte[golden.Length];
        int written = ItemInstanceVisibility.PublicView(
            registry, golden, PropertyVisibility.OwnerOnly, identified: false, revealedMask: 0, view);

        ReadOnlySpan<byte> kept = view.AsSpan(0, written);
        Assert.False(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Affixes));
        Assert.False(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.RareName));

        // Kind 128 itself is not gated, so the viewer still learns the item is unidentified, which is what
        // puts khaoz.item.unidentified in the tooltip instead of a blank line.
        Assert.True(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Identification));
        Assert.True(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Rarity));
    }

    [Fact]
    public void PublicView_and_OwnerRemainder_reconstruct_the_payload_for_an_identified_owner()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        var view = new byte[payload.Length];
        int publicBytes = ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);

        var remainder = new byte[payload.Length];
        int ownerBytes = ItemInstanceVisibility.OwnerRemainder(
            registry, payload, identified: true, revealedMask: 0, remainder);

        ReadOnlySpan<byte> publicView = view.AsSpan(0, publicBytes);
        ReadOnlySpan<byte> ownerView = remainder.AsSpan(0, ownerBytes);

        // The two are disjoint and their union is what the owner sees, which is what makes them a
        // complement rather than two overlapping views that happen to add up.
        foreach (ushort kind in VisibilityFixtures.RegisteredKinds())
        {
            bool inPublic = VisibilityFixtures.Carries(registry, publicView, kind);
            bool inRemainder = VisibilityFixtures.Carries(registry, ownerView, kind);

            // Kind 132 is the ONE kind both carry, and it is not an overlap: a socket's FRAME is public
            // and the gem sitting in it holds owner-only fields, so the split runs one level down. The
            // remainder's copy of the frame is what positions those fields, and it is the only byte cost
            // of fixing the leak.
            if (kind == InstancePropertyKind.Sockets)
            {
                Assert.True(inPublic && inRemainder);
                continue;
            }

            Assert.False(inPublic && inRemainder);
            Assert.Equal(
                ItemInstanceVisibility.CanSee(registry, kind, PropertyVisibility.OwnerOnly, identified: true, 0),
                inPublic || inRemainder);
        }

        // The same rule over the socketed gem's own fields, which is where the leak and its mirror both
        // lived. A gem field reaches the owner exactly when the gem carries it and the owner may see it,
        // and it arrives through exactly one of the two projections.
        byte[] publicNested = VisibilityFixtures.SocketNested(registry, publicView);
        byte[] ownerNested = VisibilityFixtures.SocketNested(registry, ownerView);
        foreach (ushort kind in VisibilityFixtures.RegisteredKinds())
        {
            bool inPublic = VisibilityFixtures.Carries(registry, publicNested, kind);
            bool inRemainder = VisibilityFixtures.Carries(registry, ownerNested, kind);
            Assert.False(inPublic && inRemainder);
            Assert.Equal(
                VisibilityFixtures.Carries(registry, VisibilityFixtures.SocketedGem, kind)
                && ItemInstanceVisibility.CanSee(registry, kind, PropertyVisibility.OwnerOnly, identified: true, 0),
                inPublic || inRemainder);
        }
    }

    [Fact]
    public void OwnerRemainder_carries_the_OwnerOnly_kinds_and_nothing_else()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        var remainder = new byte[payload.Length];
        int written = ItemInstanceVisibility.OwnerRemainder(
            registry, payload, identified: true, revealedMask: 0, remainder);

        ReadOnlySpan<byte> kept = remainder.AsSpan(0, written);
        Assert.Null(ItemInstancePayload.Validate(registry, kept));

        Assert.True(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Charges));
        Assert.True(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.Durability));
        Assert.True(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.BoundTo));
        Assert.True(VisibilityFixtures.Carries(registry, kept, VisibilityFixtures.OwnerSecret));
        Assert.False(VisibilityFixtures.Carries(registry, kept, InstancePropertyKind.ItemLevel));
        Assert.False(VisibilityFixtures.Carries(registry, kept, VisibilityFixtures.ServerSecret));
    }

    [Fact]
    public void An_owner_only_kind_behind_a_shut_gate_is_in_neither_projection()
    {
        // The remainder is the complement of the public view at Everyone, so a gate that hides a field
        // from the owner has to hide it from the remainder too, or the targeted message would hand back
        // exactly what the gate withheld.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: false, revealedMask: 0);

        var view = new byte[payload.Length];
        int publicBytes = ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: false, revealedMask: 0, view);

        var remainder = new byte[payload.Length];
        int ownerBytes = ItemInstanceVisibility.OwnerRemainder(
            registry, payload, identified: false, revealedMask: 0, remainder);

        Assert.False(VisibilityFixtures.Carries(registry, view.AsSpan(0, publicBytes), VisibilityFixtures.OwnerSecret));
        Assert.False(VisibilityFixtures.Carries(registry, remainder.AsSpan(0, ownerBytes), VisibilityFixtures.OwnerSecret));

        // Revealing its registered bit puts it back in the remainder and nowhere else.
        ownerBytes = ItemInstanceVisibility.OwnerRemainder(
            registry, payload, identified: false, 1UL << VisibilityFixtures.OwnerSecretBit, remainder);
        Assert.True(VisibilityFixtures.Carries(registry, remainder.AsSpan(0, ownerBytes), VisibilityFixtures.OwnerSecret));
    }

    [Fact]
    public void The_payload_member_is_the_same_rule_rather_than_a_second_copy_of_it()
    {
        // ItemInstancePayload.PublicView shipped as a stub in phase 1 and delegates here now, so the
        // codec and the container keep the member they were written against and there is still ONE rule.
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        var throughPayload = new byte[payload.Length];
        int viaPayload = ItemInstancePayload.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, throughPayload);

        var throughVisibility = new byte[payload.Length];
        int viaVisibility = ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, throughVisibility);

        Assert.Equal(viaVisibility, viaPayload);
        Assert.Equal(throughVisibility, throughPayload);
    }

    [Fact]
    public void A_payload_that_does_not_decode_is_refused_rather_than_projected()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        // Two fields out of order, which rule 9.3.1 refuses. Projecting bytes nobody has checked would
        // put a malformed field on the wire under the projection's name.
        byte[] payload = { 0x05, 0x02, 0x5A, 0x64, 0x02, 0x01, 0x44 };
        Assert.NotNull(ItemInstancePayload.Validate(registry, payload));

        var view = new byte[payload.Length];
        Assert.Equal(-1, ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view));
        Assert.Equal(-1, ItemInstanceVisibility.OwnerRemainder(
            registry, payload, identified: true, revealedMask: 0, view));
    }

    [Fact]
    public void An_empty_payload_projects_to_an_empty_view()
    {
        InstancePropertyRegistry registry = VisibilityFixtures.Registry();

        Assert.Equal(0, ItemInstanceVisibility.PublicView(
            registry, default, PropertyVisibility.Everyone, identified: true, revealedMask: 0, default));
        Assert.Equal(0, ItemInstanceVisibility.OwnerRemainder(
            registry, default, identified: true, revealedMask: 0, default));
    }

    [Fact]
    public void A_null_registry_is_a_caller_error_rather_than_an_empty_view()
    {
        byte[] payload = VisibilityFixtures.EveryKind(identified: true, revealedMask: 0);

        Assert.Throws<ArgumentNullException>(() => ItemInstanceVisibility.CanSee(
            null!, InstancePropertyKind.ItemLevel, PropertyVisibility.Everyone, identified: true, revealedMask: 0));
        Assert.Throws<ArgumentNullException>(
            () => ItemInstanceVisibility.CanSee(null!, PropertyVisibility.Everyone, identified: true, revealedMask: 0));
        Assert.Throws<ArgumentNullException>(() => Project(null!, payload));
    }

    /// <summary>The three levels, which is what every fact here sweeps.</summary>
    static readonly PropertyVisibility[] Levels =
    {
        PropertyVisibility.ServerOnly, PropertyVisibility.OwnerOnly, PropertyVisibility.Everyone,
    };

    static bool Sees(InstancePropertyRegistry registry, ushort kind, PropertyVisibility viewer)
        => ItemInstanceVisibility.CanSee(registry, kind, viewer, identified: true, revealedMask: 0);

    static int Project(InstancePropertyRegistry registry, ReadOnlySpan<byte> payload)
    {
        Span<byte> view = stackalloc byte[payload.Length];
        return ItemInstanceVisibility.PublicView(
            registry, payload, PropertyVisibility.Everyone, identified: true, revealedMask: 0, view);
    }

    static byte[] Golden(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Payload", "Goldens", name));
}
