using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The eight all-or-nothing facts of spec 10.1 and the four powers spec 10.5 says a game operation must
/// not have. They are the whole SHAPE of the working copy rather than a behaviour of any one primitive, so
/// they come first and everything else is built inside them.
/// <para>
/// Every registry and every snapshot a fact builds is its OWN, so nothing here writes process-global state
/// and no <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public sealed class CraftWorkingCopyTests
{
    /// <summary>A kind no v1 registry holds, which is what an unknown field and an unregistered write use.</summary>
    const ushort UnknownKind = 500;

    [Fact]
    public void A_craft_decodes_into_a_BUILDER_and_never_patches_a_stored_byte()
    {
        byte[] stored = Payload(builder => builder
            .AddScalar(InstancePropertyKind.ItemLevel, 60)
            .AddByte(InstancePropertyKind.Rarity, 2));
        byte[] before = [.. stored];

        CraftWorkingCopy copy = Open(stored);
        Assert.True(copy.SetScalar(InstancePropertyKind.Quality, 20));
        Assert.True(copy.TryEncode(out byte[] crafted));

        Assert.Equal(before, stored);
        Assert.NotEqual(before, crafted);
        Assert.Equal(crafted, Payload(builder => builder
            .AddScalar(InstancePropertyKind.ItemLevel, 60)
            .AddScalar(InstancePropertyKind.Quality, 20)
            .AddByte(InstancePropertyKind.Rarity, 2)));
    }

    [Fact]
    public void A_refusal_at_any_step_discards_the_builder_and_the_durable_bytes_are_untouched()
    {
        byte[] stored = Payload(builder => builder.AddScalar(InstancePropertyKind.ItemLevel, 60));
        byte[] before = [.. stored];

        CraftWorkingCopy copy = Open(stored);
        Assert.True(copy.SetScalar(InstancePropertyKind.Quality, 20));
        _ = copy.Refuse(new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.Durability));

        Assert.True(copy.IsRefused);
        Assert.False(copy.TryEncode(out byte[] crafted));
        Assert.Empty(crafted);
        Assert.Equal(before, stored);
    }

    [Fact]
    public void There_is_no_partial_craft_and_therefore_no_rollback_path()
    {
        byte[] stored = Payload(builder => builder.AddScalar(InstancePropertyKind.ItemLevel, 60));

        CraftWorkingCopy copy = Open(stored);
        CraftRefusal first = copy.Refuse(new CraftRefusal(CraftRefusalKind.SocketEmpty, 1));

        // Every later write is a no-op and the FIRST refusal is the one that survives, so there is no
        // half applied craft anywhere for a rollback to undo.
        Assert.False(copy.SetScalar(InstancePropertyKind.Quality, 20));
        Assert.False(copy.SetByte(InstancePropertyKind.Rarity, 2));
        Assert.Equal(first, copy.Refuse(new CraftRefusal(CraftRefusalKind.PayloadTooLong, 0)));
        Assert.False(copy.TryEncode(out _));

        foreach (string name in new[] { "Rollback", "Undo", "Revert", "Commit", "Restore" })
        {
            Assert.Empty(typeof(CraftWorkingCopy).GetMember(name, BindingFlags.Public | BindingFlags.Instance));
        }
    }

    [Fact]
    public void The_re_encoded_payload_is_canonical_because_the_builder_made_it_so()
    {
        byte[] stored = Payload(builder => builder.AddScalar(InstancePropertyKind.ItemLevel, 60));

        CraftWorkingCopy copy = Open(stored);

        // Written HIGH kind first, so the ascending order in the answer can only be the builder's.
        Assert.True(copy.SetByte(InstancePropertyKind.Rarity, 2));
        Assert.True(copy.SetScalar(InstancePropertyKind.Quality, 20));
        Assert.True(copy.TryEncode(out byte[] crafted));

        Assert.Null(ItemInstancePayload.Validate(InstancePropertyRegistry.CreateV1(), crafted));
        Assert.Equal(
            Payload(builder => builder
                .AddScalar(InstancePropertyKind.ItemLevel, 60)
                .AddScalar(InstancePropertyKind.Quality, 20)
                .AddByte(InstancePropertyKind.Rarity, 2)),
            crafted);
    }

    [Fact]
    public void A_working_copy_cannot_write_an_UNREGISTERED_property_kind()
    {
        byte[] stored = Payload(builder => builder.AddScalar(InstancePropertyKind.ItemLevel, 60));

        CraftWorkingCopy copy = Open(stored);

        Assert.False(copy.SetScalar(UnknownKind, 1));
        Assert.Equal(new CraftRefusal(CraftRefusalKind.PropertyKindUnregistered, UnknownKind), copy.Refusal);
        Assert.False(copy.TryEncode(out _));
    }

    [Fact]
    public void A_working_copy_cannot_exceed_MaxInstancePayloadBytes()
    {
        // Kind 7's material list is the one v1 field an author can size, so it is what fills the budget.
        var materials = new List<InstanceMaterial>();
        while (Payload(builder => builder.AddMaterials([.. materials])).Length
            < ItemInstancePayload.MaxInstancePayloadBytes - 8)
        {
            materials.Add(new InstanceMaterial(materials.Count + 1, 1));
        }

        byte[] stored = Payload(builder => builder.AddMaterials([.. materials]));
        byte[] before = [.. stored];

        CraftWorkingCopy copy = Open(stored);
        Assert.False(copy.SetPair(InstancePropertyKind.Charges, uint.MaxValue, uint.MaxValue));
        Assert.Equal(CraftRefusalKind.PayloadTooLong, copy.Refusal.Kind);
        Assert.False(copy.TryEncode(out _));
        Assert.Equal(before, stored);
    }

    [Fact]
    public void A_working_copy_cannot_allocate_an_instance_id()
    {
        // By reflection over the type's own fields, because "it cannot" is a property of the SHAPE rather
        // than of any code path a fact could drive. A working copy that held an allocator could mint one
        // whatever its methods happened to do today.
        foreach (Type held in Reachable(typeof(CraftWorkingCopy)))
        {
            Assert.NotEqual(typeof(InstanceIdAllocator), held);
            Assert.NotEqual(typeof(IInstanceIdStore), held);
        }

        foreach (MemberInfo member in typeof(CraftWorkingCopy).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            Assert.DoesNotContain("Allocat", member.Name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unknown_kind_the_decode_preserved_survives_a_craft_verbatim()
    {
        byte[] body = [7, 7, 7, 9];
        byte[] stored = Payload(builder => builder
            .AddScalar(InstancePropertyKind.ItemLevel, 60)
            .Add(UnknownKind, body)
            .AddByte(InstancePropertyKind.Rarity, 2));

        CraftWorkingCopy copy = Open(stored);
        Assert.True(copy.SetScalar(InstancePropertyKind.Quality, 20));
        Assert.True(copy.TryEncode(out byte[] crafted));

        // Contracts 9.4 keeps an unknown kind verbatim INCLUDING its position in the ordering, so a client
        // built against build N crafting an item does not strip a field only build N plus one knows.
        List<PayloadField> fields = Fields(crafted);
        Assert.Equal(
            [InstancePropertyKind.ItemLevel, InstancePropertyKind.Quality, InstancePropertyKind.Rarity, UnknownKind],
            fields.Select(static field => field.Kind));
        PayloadField unknown = fields[3];
        Assert.Equal(body, crafted[unknown.BodyStart..(unknown.BodyStart + unknown.BodyLength)]);
    }

    /// <summary>A copy over the authored generation world, which is the only content these facts need.</summary>
    static CraftWorkingCopy Open(ReadOnlySpan<byte> payload)
    {
        ContentTypeRegistry registry = World();
        return CraftWorkingCopy.Open(
            InstancePropertyRegistry.CreateV1(),
            Candidate(registry),
            Greatsword,
            payload);
    }

    /// <summary>One payload, built through the one encoder the format has.</summary>
    static byte[] Payload(Action<ItemInstancePayloadBuilder> author)
    {
        var builder = new ItemInstancePayloadBuilder();
        author(builder);
        return builder.ToArray();
    }

    /// <summary>Every type reachable from one type's instance fields, one level of composition down.</summary>
    static IEnumerable<Type> Reachable(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            yield return field.FieldType;
            foreach (FieldInfo inner in field.FieldType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                yield return inner.FieldType;
            }
        }
    }
}
