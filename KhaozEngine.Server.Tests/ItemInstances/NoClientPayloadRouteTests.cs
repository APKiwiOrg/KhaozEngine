using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.ItemInstances;
using KhaozEngine.ItemInstances.Journal;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Server.ItemInstances;

/// <summary>
/// Spec 17 row 13 in its FULL form, which is spec 15.3's invariant: <b>the server never accepts instance
/// BYTES from a client, only OPERATIONS</b>. Every client-to-server message in this design names items by
/// slot and by id, and there is no message anywhere carrying a payload in that direction. A payload is
/// produced by the generator, by a craft primitive, or by a decode of the server's own durable bytes, and by
/// nothing else, which makes the whole class of "craft a payload, send it, get the item" unreachable rather
/// than mitigated.
/// <para>
/// <b>The enumeration rule, so a new message kind is caught rather than missed.</b> The set is every PUBLIC
/// concrete type in the three assemblies that carry this design's client-to-server wire
/// (<c>KhaozEngine.ItemInstances</c>, <c>KhaozEngine.ItemInstances.Journal</c> and
/// <c>KhaozEngine.TileWorld.Netcode</c>) whose simple name ends in <c>Request</c>, <c>Command</c> or
/// <c>Operation</c>, which are the three names this design gives a client-originated message, plus every
/// member of <see cref="TileCommandKind"/>, which is the enum the one command frame dispatches on, plus
/// every <c>ClientFrame</c> tag the protocol declares. The discovered set is held EQUAL to the expected list,
/// so a new message type under any of those names fails this test until someone judges it, and an existing
/// one cannot silently drop out of the sweep.
/// <para>
/// It is deliberately over-inclusive: <see cref="PendingTileCommand"/> is the server-side component that
/// carries an already decoded command rather than a wire message, and holding it to the same bar costs
/// nothing and closes the door on a command growing a byte field on the way in.
/// </para>
/// </para>
/// <para>
/// <b>The LIMIT of the sweep, so nobody reads it as more than it is.</b> The rule is NAME shaped and
/// ASSEMBLY shaped, so a new client to server type is caught here only when it is named
/// <c>*Request</c>, <c>*Command</c> or <c>*Operation</c> AND it lives in one of those three assemblies.
/// A public type under any other suffix, or one in a fourth assembly, carries a payload past this fact
/// without failing it. The equality assertion below is what makes the gap visible rather than silent: the
/// expected list has to be edited by hand when the set moves, and the person editing it is the one who has
/// to judge whether the new name belongs in <c>MessageSuffixes</c> too.
/// </para>
/// <para>
/// <b>Phase 3's narrow version stays where it is and stays green.</b>
/// <c>PageSyncFrameBoundTests.No_client_to_server_message_carries_an_instance_payload</c> in
/// <c>KhaozEngine.TileWorld.Netcode.Tests</c> covers the resync request and the take request from the
/// netcode side, which is the only project that sees the protocol's own internals. This class SUPERSEDES it
/// in coverage: it repeats both of those facts and adds the operation vocabulary a client heads, every
/// command kind, and the frame tags.
/// </para>
/// <para>
/// Nothing here writes process-global state, so no collection attribute is needed.
/// </para>
/// </summary>
public class NoClientPayloadRouteTests
{
    /// <summary>The three suffixes this design gives a client-originated message.</summary>
    static readonly string[] MessageSuffixes = ["Request", "Command", "Operation"];

    /// <summary>The value types that ARE a window over bytes, which is how a payload reaches a message
    /// without being a reference type.</summary>
    static readonly Type[] MemoryShapes =
    [
        typeof(ReadOnlyMemory<>), typeof(Memory<>), typeof(ReadOnlySpan<>), typeof(Span<>), typeof(ArraySegment<>),
    ];

    /// <summary>A recognizable payload, which no canonical encoding may contain.</summary>
    static readonly byte[] Marker = [0xDE, 0xAD, 0xBE, 0xEF, 0x11, 0x22, 0x33, 0x44];

    /// <summary>The container a page resync request names, by number.</summary>
    const byte ContainerId = 3;

    /// <summary>The page it asks for.</summary>
    const byte PageIndex = 9;

    [Fact]
    public void No_client_to_server_message_type_in_this_design_carries_a_payload_shaped_field()
    {
        string[] discovered = ClientMessageTypes().Select(type => type.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            new[] { "ContainerOperation", "ContainerPageSyncRequest", "PendingTileCommand", "TileCommand" },
            discovered);

        // Every member of every one of them is a name or a number, with ONE type held to a different bar:
        // ContainerOperation carries the OUTCOME of the operation beside the operation, and what a client
        // sends and resubmits is its canonical encoding rather than the object.
        foreach (Type message in ClientMessageTypes().Where(type => type != typeof(ContainerOperation)))
        {
            foreach (MemberInfo member in PayloadShapedMembers(message))
            {
                Assert.Fail($"{message.Name}.{member.Name} is payload shaped, which spec 15.3 refuses.");
            }
        }

        // The detector carries its own positive control, so this fact can never pass because the walk broke:
        // the two durable bodies and a bare array ARE payload shaped, and it says so.
        Assert.False(IsNameOrNumber(typeof(byte[])));
        Assert.False(IsNameOrNumber(typeof(ReadOnlyMemory<byte>)));
        Assert.False(IsNameOrNumber(typeof(ItemCraftedEvent)));
        Assert.False(IsNameOrNumber(typeof(ItemGeneratedEvent)));
    }

    [Fact]
    public void The_operation_a_client_HEADS_carries_its_payload_outside_the_intent_for_every_kind()
    {
        // ContainerOperation's payload shaped members are exactly two, and both are OUTCOMES: the payload a
        // grant seats or a craft leaves, and the durable event body. Its only reference typed members are
        // container NAMES, which is what a container's pages are filed under (OWNER DECISION 2, #942).
        Assert.Equal(
            new[] { "EventPayload", "Payload" },
            PayloadShapedMembers(typeof(ContainerOperation))
                .Where(member => !IsName(MemberType(member)))
                .Select(member => member.Name)
                .OrderBy(name => name, StringComparer.Ordinal));
        // Its only reference typed members beyond those two are STRINGS, and a string here is an identity:
        // two container names, the same name defaulted to the operation's own, and the durable event type.
        Assert.Equal(
            new[] { "Container", "DestinationContainer", "DestinationContainerOrOwn", "EventType" },
            PayloadShapedMembers(typeof(ContainerOperation))
                .Where(member => IsName(MemberType(member)))
                .Select(member => member.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        // What a client sends is the canonical encoding, and changing the payload changes NOTHING in it. A
        // craft is the one kind that requires an event body, so its body is varied instead of emptied.
        foreach (ContainerOperation operation in EveryKind())
        {
            byte[] canonical = operation.ToCanonicalArray();
            ContainerOperation loaded = operation.Kind == ContainerOperationKind.Craft
                ? operation with { Payload = Marker, EventPayload = Marker }
                : operation with { Payload = Marker };

            Assert.Equal(canonical, loaded.ToCanonicalArray());
            Assert.Equal(-1, canonical.AsSpan().IndexOf(Marker.AsSpan()));
        }
    }

    [Fact]
    public void Every_client_to_server_message_names_the_item_it_acts_on_by_ID()
    {
        // The operation vocabulary: every kind carries the instance id it expects at the slot, so two
        // otherwise identical operations against two different items encode to different bytes. That is
        // spec 15.1's load bearing field, checked here for all six kinds rather than for a craft alone.
        foreach (ContainerOperation operation in EveryKind())
        {
            byte[] mine = operation.ToCanonicalArray();
            byte[] theirs = (operation with { InstanceId = operation.InstanceId + 1 }).ToCanonicalArray();

            Assert.NotEqual(mine, theirs);
        }

        // The tile command: the target is a net id, an authored object id or a held direction, and the frame is a
        // fixed 24 bytes on every kind, so no kind can carry bytes even if a later encoder wanted one to.
        foreach (TileCommandKind kind in Enum.GetValues<TileCommandKind>())
        {
            // A steer's target is a TileDirection rather than an id, and the decoder refuses one outside the eight,
            // so it takes two legal directions here. That is a NARROWER field than an id, which is the point being
            // pinned: eight values cannot name an item, let alone describe one.
            (long target, long other) = kind == TileCommandKind.Steer
                ? ((long)TileDirection.N, (long)TileDirection.NE)
                : (9_000_001L, 9_000_002L);
            byte[] frame = TileProtocol.EncodeCommand(4, new TileCommand(kind, new TileCoord(3, 5, 0), TileMoveMode.Walk, target));

            Assert.Equal(24, frame.Length);
            Assert.True(TileProtocol.TryDecodeCommand(frame, planeCount: 4, out int seq, out TileCommand decoded));
            Assert.Equal(4, seq);
            Assert.Equal(kind, decoded.Kind);
            Assert.Equal(target, decoded.Target);
            Assert.NotEqual(
                frame,
                TileProtocol.EncodeCommand(4, new TileCommand(kind, new TileCoord(3, 5, 0), TileMoveMode.Walk, other)));
        }

        // The page resync request names a CONTAINER and a PAGE and no item at all, in two bytes, because
        // what comes back is the server's own stored page rather than anything the client described.
        Assert.Equal(2, ContainerPageSyncRequest.Bytes);
        Span<byte> request = stackalloc byte[ContainerPageSyncRequest.Bytes];
        Assert.Equal(2, new ContainerPageSyncRequest(ContainerId, PageIndex).Write(request));
        Assert.Equal(ContainerId, request[0]);
        Assert.Equal(PageIndex, request[1]);
    }

    [Fact]
    public void The_client_to_server_frame_tags_are_the_two_the_protocol_declares()
    {
        // The other half of the enumeration: a message reaches the server through a client frame tag, so a
        // NEW route is a new tag and this is where it shows up.
        FieldInfo[] tags = typeof(TileProtocol)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(byte)
                && field.Name.StartsWith("ClientFrame", StringComparison.Ordinal))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "ClientFrameCommand", "ClientFrameGameMessage" },
            tags.Select(field => field.Name));
        Assert.Equal(TileProtocol.ClientFrameCommand, TileProtocol.ClientFrameTag([TileProtocol.ClientFrameCommand]));

        // The command tag carries a TileCommand, whose every member is a name or a number. The game message
        // tag carries the game's own kind space, and the ONE client-to-server message this design defines on
        // it is the page resync request, which is two numbers.
        Assert.Empty(PayloadShapedMembers(typeof(TileCommand)));
        Assert.Equal(2, ContainerPageSyncRequest.Bytes);
        Assert.True(IsNameOrNumber(typeof(ContainerPageSyncRequest)));
    }

    /// <summary>The enumeration rule, run.</summary>
    static IEnumerable<Type> ClientMessageTypes()
        => new[]
            {
                typeof(ContainerPageSyncRequest).Assembly,
                typeof(ContainerOperation).Assembly,
                typeof(TileCommand).Assembly,
            }
            .Distinct()
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => !type.IsInterface && !type.IsEnum && !type.IsAbstract)
            .Where(type => MessageSuffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal)));

    /// <summary>Every operation kind, each carrying a payload and an event body, which the canonical
    /// encoding may not reach.</summary>
    static IEnumerable<ContainerOperation> EveryKind()
    {
        yield return ContainerOperation.Move("bank", 3, "bag", 4, 1, 7_001);
        yield return ContainerOperation.Split("bank", 3, 4, 2);
        yield return ContainerOperation.Merge("bank", 3, 4, 7_001, 7_002);
        yield return ContainerOperation.Grant("bank", 3, 100, 1, 7_001, Marker);
        yield return ContainerOperation.Take("bank", 3, 1, 7_001);
        yield return ContainerOperation.Craft("bank", 3, 7_001, Marker, Marker);
    }

    /// <summary>Whether a member could only ever be a name or a number: an enum, a number, or a value type
    /// built out of those. A reference type is the payload shape this invariant refuses, because bytes reach
    /// a message through an array, a string or a class, and so is a window over bytes, because a
    /// <see cref="ReadOnlyMemory{T}"/> is a struct whose own members are all numbers and would otherwise
    /// walk clean.</summary>
    static bool IsNameOrNumber(Type type)
    {
        if (type.IsEnum || type.IsPrimitive) return true;
        if (!type.IsValueType) return false;
        if (type.IsGenericType && MemoryShapes.Contains(type.GetGenericTypeDefinition())) return false;

        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .All(property => IsNameOrNumber(property.PropertyType))
            && type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .All(field => IsNameOrNumber(field.FieldType));
    }

    /// <summary>Whether a member is a NAME: a string, which is an identity rather than bytes.</summary>
    static bool IsName(Type type) => type == typeof(string);

    /// <summary>Every public instance member of a type whose own type is payload shaped.</summary>
    static IEnumerable<MemberInfo> PayloadShapedMembers(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            .Where(member => !IsNameOrNumber(MemberType(member)));

    static Type MemberType(MemberInfo member)
        => member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
}
