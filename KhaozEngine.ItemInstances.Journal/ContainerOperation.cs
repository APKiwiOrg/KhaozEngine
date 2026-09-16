using System;
using System.Text;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// The operation vocabulary a batched container commit is built from, spec 6.4. One kind per logical thing a
/// player or the server does to a paged container, and a durable varint per kind: the number is written into
/// a normalized intent and into an event payload, so a member is never renumbered and never reordered.
/// </summary>
public enum ContainerOperationKind
{
    /// <summary>Not an operation. It exists so the default value of
    /// <see cref="ContainerOperation"/> is refused rather than silently read as a move.</summary>
    None = 0,

    /// <summary>Relocate a whole entry, or units of a plain stack, into an EMPTY slot. The destination may be
    /// another container on the same stream, which is spec 5.6's two projection writes on one stream.</summary>
    Move = 1,

    /// <summary>Move units of a plain stack into an empty slot of the SAME container.</summary>
    Split = 2,

    /// <summary>Fold one occupied slot into another under spec 4.6's byte equality rule. It destroys an
    /// instance id, which is why its event names both.</summary>
    Merge = 3,

    /// <summary>Seat an arriving entry at a named slot, merging into what is already there when 4.6 allows
    /// it and otherwise opening the slot under the capacity gate of spec 5.7.</summary>
    Grant = 4,

    /// <summary>Remove units from a slot, which is the half of a withdraw this stream owns.</summary>
    Take = 5,

    /// <summary>Rewrite the payload of an owned item in place and consume the currency that paid for it,
    /// spec 10.6. The PLACEHOLDER of this phase: the operation and its page write are real, and the event
    /// BODY is the crafting framework's to encode.</summary>
    Craft = 6,
}

/// <summary>Who caused an operation, which is what spec 6.5 batches on.</summary>
public enum ContainerOperationOrigin
{
    /// <summary>The server caused it: a held craft, a processing run, a gathering run, an expiry sweep. It
    /// has no id of its own to lose, so it may ride any batch.</summary>
    Server = 0,

    /// <summary>A client asked for it, carrying the operation id it will resubmit after a reconnect. Two of
    /// these never share a batch, because <c>ResolveOperationAsync</c> is keyed on ONE id.</summary>
    Client = 1,
}

/// <summary>
/// One logical operation against a paged container, with the canonical parameter encoding spec 6.4 hashes
/// into a normalized intent.
/// <para>
/// <b>The encoding is <c>[Kind: varint][Parameters]</c>, and which parameters a kind writes is FIXED per
/// kind</b>, in one shared field order: container, slot, destination container, destination slot, definition
/// id, count, instance id, destination instance id. Every varint is unsigned and minimal (contracts 15), a
/// container name is written as <c>[Length: varint][UTF8]</c> over the journal's identity set, and an
/// instance id goes through <see cref="InstanceIdAllocator.WriteId"/> so the high node's sign bit cannot make
/// two encodings of one id. Canonical because the journal hashes the intent to detect a conflicting replay,
/// so two encodings of one batch must produce one byte sequence.
/// </para>
/// <para>
/// <b>Nothing that is an OUTCOME is in the encoding.</b> Not <see cref="Payload"/>, not
/// <see cref="EventPayload"/>, and not <see cref="Origin"/> or <see cref="PresentAtCommit"/>, which are
/// routing rather than intent. A client rebuilds its own intent from its click and never saw what the click
/// caused, which is exactly the property spec 6.5 leans on.
/// </para>
/// <para>
/// <b>Every kind names the slots it touches</b>, so the pages a batch will write are known before it is
/// applied. That is what lets the window of spec 6.4 close on the projection write cap BEFORE an operation
/// mutates the working copy, and it is also what makes a replay land where the original did rather than
/// wherever a free-slot search would put it today.
/// </para>
/// </summary>
public readonly record struct ContainerOperation
{
    [Flags]
    enum Field
    {
        None = 0,
        Container = 1,
        Slot = 2,
        DestinationContainer = 4,
        DestinationSlot = 8,
        DefinitionId = 16,
        Count = 32,
        InstanceId = 64,
        DestinationInstanceId = 128,
    }

    /// <summary>Which operation this is. <see cref="ContainerOperationKind.None"/> is refused.</summary>
    public ContainerOperationKind Kind { get; init; }

    /// <summary>The container the operation acts on, which is the name its pages are sectioned under.</summary>
    public string Container { get; init; }

    /// <summary>The slot acted on. A <see cref="ContainerOperationKind.Grant"/> names the slot the entry is
    /// seated at rather than searching for one.</summary>
    public int Slot { get; init; }

    /// <summary>The second container an operation touches: a move's destination, and left null for a move
    /// inside one container. A <see cref="ContainerOperationKind.Craft"/> names the CURRENCY's container
    /// here.</summary>
    public string? DestinationContainer { get; init; }

    /// <summary>The second slot an operation touches: a move's or a merge's destination, and a craft's
    /// currency slot.</summary>
    public int DestinationSlot { get; init; }

    /// <summary>The content definition a grant seats, or the currency a craft consumes. Zero on a craft
    /// means no currency is consumed.</summary>
    public int DefinitionId { get; init; }

    /// <summary>How many units the operation moves, grants, takes or consumes.</summary>
    public int Count { get; init; }

    /// <summary>The durable instance id the operation expects at <see cref="Slot"/>, 0 for a plain stack.
    /// <b>It is in the intent and that is load bearing</b> (spec 15.1): without it a replayed operation whose
    /// slot has been refilled by a different item would hash identically and apply to the wrong item.</summary>
    public long InstanceId { get; init; }

    /// <summary>The instance id expected at <see cref="DestinationSlot"/>, which a merge names because a
    /// merge destroys one of the two.</summary>
    public long DestinationInstanceId { get; init; }

    /// <summary>The instance payload a grant seats or a craft leaves behind. An OUTCOME, so it is not in the
    /// canonical encoding.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// The durable event body, which only <see cref="ContainerOperationKind.Craft"/> takes and which it
    /// REQUIRES. Spec 10.6 owns that body and the crafting framework encodes it, so this phase carries it
    /// rather than freezing a format under a durable event name. Every other kind writes its own canonical
    /// encoding as its event body.
    /// </summary>
    public ReadOnlyMemory<byte> EventPayload { get; init; }

    /// <summary>Who caused it, which is what spec 6.5 batches on.</summary>
    public ContainerOperationOrigin Origin { get; init; }

    /// <summary>The client's own durable operation id, required when <see cref="Origin"/> is
    /// <see cref="ContainerOperationOrigin.Client"/> and empty otherwise. It is what the client resubmits
    /// after a reconnect, and it becomes the whole batch's identity when the client heads one.</summary>
    public Guid OperationId { get; init; }

    /// <summary>Whether the consumer must not present the outcome before the commit lands. Value moving
    /// between accounts sets it, and an operation that sets it never shares an identity with anything
    /// else.</summary>
    public bool PresentAtCommit { get; init; }

    /// <summary>The durable event type this kind writes, which the commit builder stamps on its event.</summary>
    public string EventType => ItemInstanceEvents.EventTypeOf(Kind);

    /// <summary>The bytes <see cref="WriteCanonical"/> writes.</summary>
    public int CanonicalByteCount
    {
        get
        {
            Field fields = FieldsOf(Kind);
            int size = ContentVarint.Size((uint)Kind);
            if ((fields & Field.Container) != 0) size += NameSize(Container);
            if ((fields & Field.Slot) != 0) size += ContentVarint.Size((uint)Slot);
            if ((fields & Field.DestinationContainer) != 0) size += NameSize(DestinationContainer ?? Container);
            if ((fields & Field.DestinationSlot) != 0) size += ContentVarint.Size((uint)DestinationSlot);
            if ((fields & Field.DefinitionId) != 0) size += ContentVarint.Size((uint)DefinitionId);
            if ((fields & Field.Count) != 0) size += ContentVarint.Size((uint)Count);
            if ((fields & Field.InstanceId) != 0) size += InstanceIdAllocator.SizeOf(InstanceId);
            if ((fields & Field.DestinationInstanceId) != 0) size += InstanceIdAllocator.SizeOf(DestinationInstanceId);
            return size;
        }
    }

    /// <summary>Writes <c>[Kind: varint][Parameters]</c> and answers the bytes written.</summary>
    /// <param name="destination">At least <see cref="CanonicalByteCount"/> bytes.</param>
    /// <exception cref="ArgumentException">The operation is not one this vocabulary can encode, which
    /// <see cref="Validate"/> states in full.</exception>
    public int WriteCanonical(Span<byte> destination)
    {
        Validate();
        Field fields = FieldsOf(Kind);
        int written = ContentVarint.Write(destination, (uint)Kind);
        if ((fields & Field.Container) != 0) written += WriteName(destination[written..], Container);
        if ((fields & Field.Slot) != 0) written += ContentVarint.Write(destination[written..], (uint)Slot);
        if ((fields & Field.DestinationContainer) != 0)
            written += WriteName(destination[written..], DestinationContainer ?? Container);
        if ((fields & Field.DestinationSlot) != 0)
            written += ContentVarint.Write(destination[written..], (uint)DestinationSlot);
        if ((fields & Field.DefinitionId) != 0)
            written += ContentVarint.Write(destination[written..], (uint)DefinitionId);
        if ((fields & Field.Count) != 0) written += ContentVarint.Write(destination[written..], (uint)Count);
        if ((fields & Field.InstanceId) != 0)
            written += InstanceIdAllocator.WriteId(destination[written..], InstanceId);
        if ((fields & Field.DestinationInstanceId) != 0)
            written += InstanceIdAllocator.WriteId(destination[written..], DestinationInstanceId);
        return written;
    }

    /// <summary>The canonical encoding as a fresh array, which is what an identity takes.</summary>
    public byte[] ToCanonicalArray()
    {
        byte[] bytes = new byte[CanonicalByteCount];
        WriteCanonical(bytes);
        return bytes;
    }

    /// <summary>
    /// Refuses an operation this vocabulary cannot express. <b>It is not a game rule check.</b> An operation
    /// a game refuses never reaches the journal at all (spec 10.6), so everything here is a caller bug.
    /// </summary>
    /// <exception cref="ArgumentException">The kind is <see cref="ContainerOperationKind.None"/> or unknown,
    /// a name is missing or empty, a slot is negative, a required count or definition id is not positive, a
    /// client operation carries no id, or a craft carries no event body.</exception>
    public void Validate()
    {
        Field fields = FieldsOf(Kind);
        Require(!string.IsNullOrEmpty(Container), "An operation names the container it acts on.");
        if ((fields & Field.Slot) != 0) Require(Slot >= 0, "A slot is never negative.");
        if ((fields & Field.DestinationContainer) != 0)
            Require(DestinationContainer is null || DestinationContainer.Length > 0, "A destination container name is never empty.");
        if ((fields & Field.DestinationSlot) != 0)
            Require(DestinationSlot >= 0, "A destination slot is never negative.");
        if ((fields & Field.Count) != 0)
            Require(Count > 0 || Kind == ContainerOperationKind.Craft, "A count is positive.");
        if ((fields & Field.DefinitionId) != 0)
            Require(DefinitionId > 0 || Kind == ContainerOperationKind.Craft, "A definition id is positive.");
        Require(Count >= 0, "A count is never negative.");
        Require(DefinitionId >= 0, "A definition id is never negative.");
        Require(
            Origin != ContainerOperationOrigin.Client || OperationId != Guid.Empty,
            "A client originated operation carries the operation id it will resubmit.");
        Require(
            Origin == ContainerOperationOrigin.Client || OperationId == Guid.Empty,
            "A server caused operation has no id of its own: the batch mints one.");
        Require(
            Kind != ContainerOperationKind.Craft || !EventPayload.IsEmpty,
            "A craft carries the event body of spec 10.6, which the crafting framework encodes.");
        Require(
            Kind == ContainerOperationKind.Craft || EventPayload.IsEmpty,
            "Only a craft carries an event body: every other kind writes its own canonical encoding.");
    }

    /// <summary>Relocates a whole entry, or units of a plain stack, into an empty slot.</summary>
    /// <param name="container">The container the entry leaves.</param>
    /// <param name="slot">The slot it leaves.</param>
    /// <param name="destinationContainer">The container it arrives in, on this batch's stream.</param>
    /// <param name="destinationSlot">The empty slot it arrives at.</param>
    /// <param name="count">How many units move. The whole stack moves the entry, payload and all.</param>
    /// <param name="instanceId">The instance id expected at <paramref name="slot"/>.</param>
    public static ContainerOperation Move(
        string container, int slot, string destinationContainer, int destinationSlot, int count, long instanceId = 0)
        => new()
        {
            Kind = ContainerOperationKind.Move,
            Container = container,
            Slot = slot,
            DestinationContainer = destinationContainer,
            DestinationSlot = destinationSlot,
            Count = count,
            InstanceId = instanceId,
        };

    /// <summary>Moves units of a plain stack into an empty slot of the same container.</summary>
    /// <param name="container">The container.</param>
    /// <param name="slot">The stack's slot.</param>
    /// <param name="destinationSlot">The empty slot the split lands in.</param>
    /// <param name="count">How many units split off, fewer than the stack holds.</param>
    public static ContainerOperation Split(string container, int slot, int destinationSlot, int count)
        => new()
        {
            Kind = ContainerOperationKind.Split,
            Container = container,
            Slot = slot,
            DestinationSlot = destinationSlot,
            Count = count,
        };

    /// <summary>Folds one occupied slot into another under spec 4.6.</summary>
    /// <param name="container">The container.</param>
    /// <param name="slot">The slot merging away.</param>
    /// <param name="destinationSlot">The slot that survives.</param>
    /// <param name="instanceId">The instance id expected at <paramref name="slot"/>.</param>
    /// <param name="destinationInstanceId">The instance id expected at <paramref name="destinationSlot"/>.</param>
    public static ContainerOperation Merge(
        string container, int slot, int destinationSlot, long instanceId = 0, long destinationInstanceId = 0)
        => new()
        {
            Kind = ContainerOperationKind.Merge,
            Container = container,
            Slot = slot,
            DestinationSlot = destinationSlot,
            InstanceId = instanceId,
            DestinationInstanceId = destinationInstanceId,
        };

    /// <summary>Seats an arriving entry at a named slot.</summary>
    /// <param name="container">The container receiving it.</param>
    /// <param name="slot">The slot it lands in, empty or holding something it may merge with.</param>
    /// <param name="definitionId">The content definition granted.</param>
    /// <param name="count">How many units.</param>
    /// <param name="instanceId">The durable instance id, 0 for a plain stack.</param>
    /// <param name="payload">The canonical instance payload, empty for a plain stack.</param>
    public static ContainerOperation Grant(
        string container, int slot, int definitionId, int count, long instanceId = 0, ReadOnlyMemory<byte> payload = default)
        => new()
        {
            Kind = ContainerOperationKind.Grant,
            Container = container,
            Slot = slot,
            DefinitionId = definitionId,
            Count = count,
            InstanceId = instanceId,
            Payload = payload,
        };

    /// <summary>Removes units from a slot.</summary>
    /// <param name="container">The container.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="count">How many units leave. The whole stack empties the slot.</param>
    /// <param name="instanceId">The instance id expected at <paramref name="slot"/>.</param>
    public static ContainerOperation Take(string container, int slot, int count, long instanceId = 0)
        => new()
        {
            Kind = ContainerOperationKind.Take,
            Container = container,
            Slot = slot,
            Count = count,
            InstanceId = instanceId,
        };

    /// <summary>
    /// Rewrites an owned item's payload in place and consumes the currency that paid for it.
    /// </summary>
    /// <param name="container">The target's container.</param>
    /// <param name="slot">The target's slot.</param>
    /// <param name="instanceId">The target's instance id, which a craft always has.</param>
    /// <param name="payload">The payload as it stands AFTER the craft.</param>
    /// <param name="eventPayload">Spec 10.6's event body, which the crafting framework encodes.</param>
    /// <param name="currencyContainer">The currency's container, null for the target's own.</param>
    /// <param name="currencySlot">The currency's slot.</param>
    /// <param name="currencyDefinitionId">The currency consumed, 0 for a craft that consumes none.</param>
    /// <param name="currencyCount">How many units of it, 0 when none is consumed.</param>
    public static ContainerOperation Craft(
        string container,
        int slot,
        long instanceId,
        ReadOnlyMemory<byte> payload,
        ReadOnlyMemory<byte> eventPayload,
        string? currencyContainer = null,
        int currencySlot = 0,
        int currencyDefinitionId = 0,
        int currencyCount = 0)
        => new()
        {
            Kind = ContainerOperationKind.Craft,
            Container = container,
            Slot = slot,
            DestinationContainer = currencyContainer,
            DestinationSlot = currencySlot,
            DefinitionId = currencyDefinitionId,
            Count = currencyCount,
            InstanceId = instanceId,
            Payload = payload,
            EventPayload = eventPayload,
        };

    /// <summary>The same operation, attributed to a client and carrying the id it will resubmit.</summary>
    /// <param name="operationId">The client's own durable operation id.</param>
    public ContainerOperation FromClient(Guid operationId)
        => this with { Origin = ContainerOperationOrigin.Client, OperationId = operationId };

    /// <summary>Which container this operation's second slot is in, which is its own when none is named.</summary>
    public string DestinationContainerOrOwn => DestinationContainer ?? Container;

    static Field FieldsOf(ContainerOperationKind kind) => kind switch
    {
        ContainerOperationKind.Move =>
            Field.Container | Field.Slot | Field.DestinationContainer | Field.DestinationSlot | Field.Count | Field.InstanceId,
        ContainerOperationKind.Split =>
            Field.Container | Field.Slot | Field.DestinationSlot | Field.Count | Field.InstanceId,
        ContainerOperationKind.Merge =>
            Field.Container | Field.Slot | Field.DestinationSlot | Field.InstanceId | Field.DestinationInstanceId,
        ContainerOperationKind.Grant =>
            Field.Container | Field.Slot | Field.DefinitionId | Field.Count | Field.InstanceId,
        ContainerOperationKind.Take =>
            Field.Container | Field.Slot | Field.Count | Field.InstanceId,
        ContainerOperationKind.Craft =>
            Field.Container | Field.Slot | Field.DestinationContainer | Field.DestinationSlot | Field.DefinitionId
            | Field.Count | Field.InstanceId,
        _ => throw new ArgumentException(
            FormattableString.Invariant($"{kind} is not an operation this vocabulary encodes."), nameof(kind)),
    };

    static int NameSize(string name)
    {
        int bytes = Encoding.UTF8.GetByteCount(name);
        return ContentVarint.Size((uint)bytes) + bytes;
    }

    static int WriteName(Span<byte> destination, string name)
    {
        int bytes = Encoding.UTF8.GetByteCount(name);
        int written = ContentVarint.Write(destination, (uint)bytes);
        return written + Encoding.UTF8.GetBytes(name, destination[written..]);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
