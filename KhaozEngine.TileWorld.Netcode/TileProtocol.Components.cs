using System;
using System.IO;
using System.Text;
using KhaozEngine.Replication;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The replicated half of the tile wire: which components cross it, under which extension ids, and how each one
/// encodes. Both heads build their registry from <c>CreateRegistry</c>, so the ids and the codecs cannot
/// drift apart the way two hand-written registrations would.
/// </summary>
public static partial class TileProtocol
{
    /// <summary>Extension id of <see cref="TileMoveState"/>. Registered NEAREST-SAMPLED rather than interpolated,
    /// because a tile is discrete (see <c>CreateRegistry</c>).</summary>
    public const ushort TileMoveStateTypeId = ReplicationRegistry.FirstExtensionTypeId + 0;

    /// <summary>Extension id of <see cref="TileRouteState"/>, the owner-only route.</summary>
    public const ushort TileRouteStateTypeId = ReplicationRegistry.FirstExtensionTypeId + 1;

    /// <summary>Extension id of <see cref="TileIdentity"/>.</summary>
    public const ushort TileIdentityTypeId = ReplicationRegistry.FirstExtensionTypeId + 2;

    /// <summary>Extension id of <see cref="TileHealth"/>, four payload bytes on the default channels.</summary>
    public const ushort TileHealthTypeId = ReplicationRegistry.FirstExtensionTypeId + 3;

    /// <summary>Extension id of <see cref="TileCombatState"/>, registered on the MIGRATE channel alone so it
    /// survives a region handoff and reaches no client at all.</summary>
    public const ushort TileCombatStateTypeId = ReplicationRegistry.FirstExtensionTypeId + 4;

    /// <summary>Extension id of <see cref="TileGroundItem"/>, a dropped stack on a tile.</summary>
    public const ushort TileGroundItemTypeId = ReplicationRegistry.FirstExtensionTypeId + 5;

    /// <summary>Extension id of <see cref="PendingTileCommand"/>, registered on the MIGRATE channel alone so the
    /// tick's command follows a body across a region boundary and reaches no client and no persistence blob.</summary>
    public const ushort PendingTileCommandTypeId = ReplicationRegistry.FirstExtensionTypeId + 6;

    /// <summary>Extension id of <see cref="TileObjectState"/>, an authored object away from its authored form.</summary>
    public const ushort TileObjectStateTypeId = ReplicationRegistry.FirstExtensionTypeId + 7;

    /// <summary>Extension id of <see cref="TileGroundItemInstance"/>, the instance a drop carries when it has
    /// one. A SIBLING of <see cref="TileGroundItemTypeId"/> rather than a wider version of it, so a client that
    /// never registered this id skips it and keeps reading the entity.</summary>
    public const ushort TileGroundItemInstanceTypeId = ReplicationRegistry.FirstExtensionTypeId + 8;

    /// <summary>The first id a GAME may register. Everything from
    /// <see cref="ReplicationRegistry.FirstExtensionTypeId"/> up to here belongs to the tile netcode, which is a
    /// block of SIXTEEN ids and leaves seven free after <see cref="TileGroundItemInstanceTypeId"/>.
    /// <para>It was <c>+8</c> until 18.14.0, which left a window TWO ids wide, and
    /// <see cref="TileObjectState"/> would have spent one of them. A window that runs out is not a minor
    /// release: ids at and above this one belong to games, so widening it renumbers every game registration in
    /// the fleet, and that gets more expensive with every consumer and every shipped client. Widening it here,
    /// while the fleet is four games and exactly one of them registers anything on this wire, is the cheap end
    /// of that trade. Sixteen because it is the width
    /// <see cref="ReplicationRegistry.FirstExtensionTypeId"/> itself reserves for engine built-ins, so the
    /// boundary reads as one block rather than as a running count of whatever has been added so far, and
    /// because the eight it left free was more than the tile netcode had spent in its entire life.</para>
    /// <para>A type id is a <see cref="ushort"/> and the space above this is not scarce, so nothing was taken
    /// from a game by moving it. What moved is where a game's own ids START, which is a WIRE break for any
    /// consumer whose two heads are not upgraded together. See the 18.14.0 changelog entry.</para></summary>
    public const ushort FirstGameTypeId = ReplicationRegistry.FirstExtensionTypeId + 16;

    /// <summary>Cap on a replicated route, in steps, and the ONE definition of that number: it is also the ceiling
    /// on <see cref="TileMoveOptions.MaxRouteSteps"/>, which is where the cap is actually enforced. The SIMULATOR
    /// truncates a longer pathfinder result, identically on both heads and in its one route builder, so a route
    /// that reaches this encoder is already within the cap and the walk ends at the truncated route's last tile (as
    /// far as one click carries). The cap counts the steps STILL TO TAKE from the tile the player is committed to,
    /// so a step already in flight is not in it: it was charged when it was routed and committed when it started.
    /// <para>The encoder therefore REFUSES a longer route rather than truncating one. Truncating on the wire loses
    /// more than the tail: <see cref="TileRoute.End"/> is the DESTINATION, so a route shortened here would tell the
    /// owner it is walking somewhere it was never routed to, and it would keep saying so, with a different wrong
    /// answer, on every snapshot. A stack trace on the head that built an over-long route is the cheaper failure.
    /// </para></summary>
    public const int MaxRouteSteps = 256;

    /// <summary>Cap on a display name's UTF-8 encoding, in bytes. Enforced on write, where the name is truncated at
    /// a codepoint boundary, and on read, where a longer declared length is a malformed frame rather than something
    /// to clamp: no encoder on this wire emits one.</summary>
    public const int MaxDisplayNameBytes = 64;

    /// <summary>Cap on a <see cref="TileGroundItemInstance"/> payload, in bytes, and the number a peer's declared
    /// length is measured against before anything is allocated to its word.
    /// <para>It MIRRORS <c>ItemSlot.MaxPayloadBytes</c>, the same 512 that contracts 9.6 sizes a container page
    /// and a single-item game message against. A copy rather than a reference because this package carries no
    /// dependency on the item packages and must not gain one: the component is an opaque long and opaque bytes,
    /// and knowing what is IN them is exactly what it must not do. <c>KhaozEngine.Server.Tests</c> sees both
    /// packages and holds the two constants equal, which is where a drift is caught.</para></summary>
    public const int MaxInstancePayloadBytes = 512;

    /// <summary>
    /// Builds the registry BOTH heads must agree on, then hands it to <paramref name="registerExtensions"/> for the
    /// game's own components, which belong at or above <see cref="FirstGameTypeId"/>. A single factory is the point:
    /// a server and a client that build their registries independently agree only for as long as somebody keeps
    /// two lists in step.
    /// <para><see cref="TileMoveState"/> registers with <c>discreteSample: true</c>. It rides the same delayed
    /// render timeline an interpolated component does, but the buffered sample nearest the render time is written
    /// VERBATIM rather than blended, which is the only correct answer for an integer tile. Blending two samples
    /// would put a remote player between two squares it was never standing on, and every rules question asked of
    /// that state (which tile, which region, in reach of what) would be asked of a tile that does not exist. The
    /// smooth motion an observer sees comes from the step fraction the state already carries, not from the
    /// replication layer.</para>
    /// <para>The route is NOT part of <see cref="TileMoveState"/>'s encoding. It rides
    /// <see cref="TileRouteState"/> on the owner-only channel, so an observer's snapshot carries the step it is
    /// TAKING and nothing about the walk beyond it: the tile it is committed to, the tile it is leaving, and how
    /// far through. That pair is enough to draw the body exactly where its owner draws it, which is why an observer
    /// needs no route and no guess. The presentation fields are never written at all, on either component.</para>
    /// <para>Every reader here is hostile-safe in one of two ways. A field whose whole byte range is meaningful is
    /// CLAMPED, because there is no such thing as a malformed one. A field with a declared length is CHECKED
    /// against the component's OWN framed payload, and a frame that lies about it throws, which
    /// <c>ClientReplicationView.TryApply</c> turns into a false and the caller turns into a disconnect. Reading a
    /// lying frame as a short one would rebuild a route out of bytes that belong to another component, which is a
    /// plausible-looking answer to a question nobody asked. Bounding the check at the payload rather than at the
    /// end of the stream is what makes it fire on a real snapshot: with another entity behind this one, an
    /// unbounded check finds the bytes the lie asked for and passes.</para>
    /// <para>There is now a THIRD way, and exactly one reader takes it.
    /// <see cref="ReadGroundItemInstance"/> is TOTAL: a declared length past its own framed payload, or above
    /// <see cref="MaxInstancePayloadBytes"/>, answers an empty payload rather than throwing. The other two
    /// refuse because a short read there would rebuild something plausible out of bytes that belong elsewhere.
    /// An instance payload is opaque to the engine, so there is nothing for it to rebuild wrongly, and the
    /// honest answer to bytes that did not arrive whole is a drop carrying no instance rather than a session
    /// that loses every other drop and every body in it with the frame.</para>
    /// <para>A tile COORDINATE is neither, and it is the first rule's limit rather than a third case. The x and z
    /// are whole ints, so every value a frame can name is one the type holds. <see cref="TileMoveState"/> carries
    /// its plane in one byte, which is the wire's own bound. <see cref="TileGroundItem"/> and
    /// <see cref="PendingTileCommand"/> carry a whole int for compatibility, so the world-bound overload checks
    /// those two against its plane count. The legacy overload has no world count and keeps its unbounded read for
    /// callers that already build one registry independently of a document.</para>
    /// </summary>
    public static ReplicationRegistry CreateRegistry(Action<ReplicationRegistry>? registerExtensions = null)
        => CreateRegistry(planeCount: null, registerExtensions);

    /// <summary>
    /// Builds the shared tile registry with the world's plane count. The wire stays unchanged, including the
    /// whole-int planes on <see cref="TileGroundItem"/> and <see cref="PendingTileCommand"/>, while their readers
    /// refuse a value outside zero through <paramref name="planeCount"/> minus one.
    /// </summary>
    /// <param name="planeCount">The plane count both heads received from the world document.</param>
    /// <param name="registerExtensions">The game's component registrations.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="planeCount"/> is not positive.</exception>
    public static ReplicationRegistry CreateRegistry(
        int planeCount,
        Action<ReplicationRegistry>? registerExtensions = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(planeCount);
        return CreateRegistry((int?)planeCount, registerExtensions);
    }

    static ReplicationRegistry CreateRegistry(
        int? planeCount,
        Action<ReplicationRegistry>? registerExtensions)
    {
        var reg = new ReplicationRegistry();
        reg.Register<TileMoveState>(TileMoveStateTypeId, WriteMove, ReadMove, discreteSample: true);
        reg.Register<TileRouteState>(TileRouteStateTypeId, WriteRoute, ReadRoute,
            channels: ReplicationChannels.Default | ReplicationChannels.OwnerOnly);
        reg.Register<TileIdentity>(TileIdentityTypeId, WriteIdentity, ReadIdentity);
        reg.Register<TileHealth>(TileHealthTypeId, WriteHealth, ReadHealth);
        reg.Register<TileCombatState>(TileCombatStateTypeId, WriteCombat, ReadCombat,
            channels: ReplicationChannels.Migrate);
        reg.Register<TileGroundItem>(TileGroundItemTypeId, WriteGroundItem,
            reader => ReadGroundItem(reader, planeCount));
        // The DEFAULT channels, and NOT OwnerOnly, which is the obvious-looking wrong answer here. OwnerOnly
        // scopes a component to the client whose OWN net id equals the ENTITY's, and a drop's entity net id is
        // never a viewer's, so it would hide the instance from everyone including the player who dropped it.
        // It is the right modifier for a component on a player's own entity and for nothing else. What rides
        // here is the PUBLIC view of the payload, computed where the item changes rather than per viewer, and
        // an owner-only remainder is a targeted message rather than a channel flag.
        reg.Register<TileGroundItemInstance>(TileGroundItemInstanceTypeId,
            WriteGroundItemInstance, ReadGroundItemInstance);
        reg.Register<TileObjectState>(TileObjectStateTypeId, WriteObjectState, ReadObjectState);
        reg.Register<PendingTileCommand>(PendingTileCommandTypeId, WritePendingCommand,
            reader => ReadPendingCommand(reader, planeCount),
            channels: ReplicationChannels.Migrate);
        registerExtensions?.Invoke(reg);
        return reg;
    }

    /// <summary>
    /// Puts a route back onto a move state: the INVERSE of the component split above, and the one definition of
    /// that rule. <c>CreateRegistry</c> splits a walking player across two components on purpose, so every
    /// reader that needs the whole state has to put them back together, and two copies of how is how the heads
    /// drift apart.
    /// <para>The CLIENT's reason is the codec: <c>TileMoveState</c>'s encoding never writes a route
    /// (<see cref="WriteMove"/>), so a decoded state is ALWAYS idle and always needs assembling. A reconciliation
    /// basis without its route stands the player still, and the replay of the pending commands has nothing to walk
    /// along, so every snapshot cancels a walk the player never cancelled.</para>
    /// <para>The SERVER's reason is a cell handoff, and it is the harder one. A raw read is WRONG on the tick
    /// after one: the destination cell rebuilds the entity from its Migrate capture, that capture carries the
    /// route in <see cref="TileRouteState"/>, and the rebuilt state therefore reads as IDLE with its
    /// <c>InteractTarget</c> still set. An arrival test on that raw state fires a player's action a whole region
    /// short of the thing they clicked. The two halves are written together on every step, so it changes nothing
    /// for an entity that never crossed, which is exactly why the bug is invisible until a player walks over a
    /// region boundary mid click.</para>
    /// <para>A live route on the state is left alone rather than rebuilt, so a walking player costs no allocation
    /// here.</para>
    /// </summary>
    /// <param name="state">The state as the component holds it, or as the codec decoded it.</param>
    /// <param name="route">The entity's <see cref="TileRouteState"/>, default when it has none.</param>
    /// <returns><paramref name="state"/> with its route assembled.</returns>
    public static TileMoveState AssembleMoveState(in TileMoveState state, in TileRouteState route)
    {
        TileMoveState s = state;
        if (s.Route.IsIdle && route.Remaining is { Length: > 0 })
            s.Route = TileRoute.FromSteps(s.Tile, route.Remaining);
        return s;
    }

    // 41 bytes for a one-tile body, then up to two optional trailing bytes. The first is the interaction domain,
    // written while an entity interaction is pending. The second is the footprint size, written only for a body
    // larger than one tile. A large body always writes the domain slot ahead of it, as a zero when no entity
    // interaction is pending, so the size sits at the fixed offset 42 whatever the interaction. A one-tile body
    // therefore writes exactly the 41 or 42 bytes it always did.
    //
    // The component is an extension frame, and every decode hands this codec a reader bounded to the component's
    // own payload (ClientReplicationView's framed window on an apply, a Migrate adoption included, and a byte slice
    // of exactly that payload when a buffered discrete sample is written back). That bound is what lets each
    // trailing byte be read only when the payload holds it: a new reader defaults a missing domain to
    // AuthoredObject and a missing size to one. An OLDER reader still decodes a large state. It consumes the 41
    // bytes it knows and at most the domain byte, which a large body writes as a value that reader already
    // understands, and the replication reader advances over the size byte with the rest of the frame. What it
    // decodes is a one-tile body, which is why both heads upgrade together for a size above one to mean anything.
    //
    // StepFrom rides WITHOUT a plane of its own, and that is a rule rather than a saving: a step never changes
    // plane, so the glide's two tiles always share one, and a second plane byte would be a way to express a state
    // the simulator cannot produce and the presenter would have to defend against.
    //
    // It rides at all because an OBSERVER needs it. A remote's route is owner-only, so before this pair the only
    // way to draw a walking remote was to remember the tile it was last seen on and glide from there, which cost a
    // whole step of extra latency and could not tell a step from a teleport without a second rule. The pair says
    // where the body is outright.
    static void WriteMove(TileMoveState v, BinaryWriter w)
    {
        w.Write(v.Tile.X);
        w.Write(v.Tile.Z);
        w.Write((byte)v.Tile.Plane);
        w.Write(v.StepFrom.X);
        w.Write(v.StepFrom.Z);
        w.Write((byte)v.Facing);
        w.Write((byte)v.Mode);
        w.Write(v.StepTicks);
        w.Write(v.StepTotal);
        w.Write(v.Epoch);
        w.Write(v.InteractTarget);
        w.Write(v.CombatTarget);
        bool entityDomain = v.InteractTarget != 0 && v.InteractDomain == TileInteractionDomain.Entity;
        int size = v.FootprintSize;
        // A large body always writes the domain slot, as a zero when no entity interaction is pending, so its size
        // sits at a fixed offset. A one-tile body writes exactly the bytes it always did.
        if (entityDomain || size > 1)
            w.Write((byte)(entityDomain ? TileInteractionDomain.Entity : TileInteractionDomain.AuthoredObject));
        if (size > 1) w.Write((byte)size);
    }

    // Every byte here is attacker controlled, and a byte cast into an enum is not validated by the runtime, so the
    // two enums are clamped rather than cast. An unclamped facing would reach TileDirections.Delta, which throws on
    // a direction that does not exist, and an unclamped mode would reach a step-cadence lookup that has no case for
    // it. Both would surface as an exception in a render loop rather than as the corrupt frame they are.
    static TileMoveState ReadMove(BinaryReader r)
    {
        int x = r.ReadInt32(), z = r.ReadInt32();
        // Not clamped, and there is nothing here to clamp it to: the plane is one byte on this wire, so a frame
        // cannot name a plane index this reader could reject, and a codec registered once for every world has no
        // plane count to measure it against. See the type doc for where an impossible plane IS caught.
        int plane = r.ReadByte();
        int fromX = r.ReadInt32(), fromZ = r.ReadInt32();
        byte facing = r.ReadByte();
        var s = TileMoveState.At(new TileCoord(x, z, plane),
            facing <= (byte)TileDirection.NE ? (TileDirection)facing : TileDirection.S);
        byte mode = r.ReadByte();
        s.Mode = mode <= (byte)TileMoveMode.Run ? (TileMoveMode)mode : TileMoveMode.Walk;
        s.StepTicks = r.ReadByte();
        s.StepTotal = r.ReadByte();
        s.Epoch = r.ReadUInt32();
        s.InteractTarget = r.ReadInt64();
        // No clamp, and it needs none: every 64 bit pattern is a legal net id value, so there is no malformed one to
        // reject. An id naming nothing simply stops resolving, which is the case the follow's rule 2 already handles
        // by clearing the lock, on both heads, on the first tick the resolver answers false.
        s.CombatTarget = r.ReadInt64();
        if (r.BaseStream.Position < r.BaseStream.Length)
        {
            byte domain = r.ReadByte();
            if (s.InteractTarget != 0 && domain == (byte)TileInteractionDomain.Entity)
                s.InteractDomain = TileInteractionDomain.Entity;
        }
        // Clamped rather than checked, the file's rule for a field whose every byte value has a size to clamp to.
        if (r.BaseStream.Position < r.BaseStream.Length)
            s.FootprintSize = Math.Clamp((int)r.ReadByte(), 1, TileMoveState.MaxFootprintSize);
        // At() seeded StepFrom onto the tile, which is what a frame naming anything but a STEP falls back to. A pair
        // that is not one tile apart is not a step: a teleport, a plane change, or a lie. Gliding between them would
        // walk the avatar over every tile in the gap, and it is a lie that costs, because Position is fed straight
        // to the reconcile error and the hard-snap gate. TileMoveState.IsStepOrigin is the ONE statement of what an
        // origin may be, shared with TileWorldServer's door so a hostile frame and a hand-built state are measured
        // by the same rule, in long arithmetic for the same overflow reason.
        var from = new TileCoord(fromX, fromZ, plane);
        if (TileMoveState.IsStepOrigin(from, s.Tile)) s.StepFrom = from;
        if (s.StepTotal == 0) s.StepTotal = 1;   // a hostile 0 would divide by zero in StepFraction
        // The step counter is the OTHER half of that division, and a step never spends more ticks than its total.
        // Left unclamped, a 250 against a total of 2 rides the wire intact and reads as a step fraction of 125: a
        // position 125 tiles out, fed straight to the reconcile error and the hard-snap gate.
        if (s.StepTicks > s.StepTotal) s.StepTicks = s.StepTotal;
        // A state whose glide has no origin to run from has no progress either, which is the invariant the
        // simulator normalizes to on the tick a body lands. Left alone, a frame could hand the owner a reconcile
        // basis that stands exactly where its prediction does and compares unequal to it, so every snapshot would
        // report a correction that moved nothing.
        if (!s.IsStepping) s.StepTicks = 0;
        return s;
    }

    // Refuses rather than truncates, for the reason in MaxRouteSteps' doc. A route over the cap cannot come from
    // TileMoveSimulator, which truncates at TileMoveOptions.MaxRouteSteps on both heads, so one here was built by
    // hand and is a local bug: worth the stack, exactly as an over-long game-message payload is.
    static void WriteRoute(TileRouteState v, BinaryWriter w)
    {
        TileDirection[] steps = v.Remaining ?? Array.Empty<TileDirection>();
        if (steps.Length > MaxRouteSteps)
            throw new ArgumentException(
                $"A replicated route is capped at {MaxRouteSteps} steps and this one carries {steps.Length}. " +
                "TileMoveSimulator truncates at TileMoveOptions.MaxRouteSteps, so this route was built elsewhere.",
                nameof(v));
        w.Write((ushort)steps.Length);
        for (int i = 0; i < steps.Length; i++) w.Write((byte)steps[i]);
    }

    // The bytes still inside THIS component's framed payload. ClientReplicationView hands an extension codec a
    // reader bounded to exactly its length-prefixed payload, so that is what this measures, and the distinction is
    // the whole point of measuring it: against the snapshot, a lying declared length is served out of the FOLLOWING
    // components' bytes whenever anything rides behind this component, and the apply returns true carrying a value
    // rebuilt from them. Only a lie that ran off the END of the stream would be caught. A non-seekable stream
    // cannot be measured at all, so it answers "no bound" and falls through to the short-read check at the read.
    static long PayloadRemaining(BinaryReader r)
        => r.BaseStream.CanSeek ? r.BaseStream.Length - r.BaseStream.Position : long.MaxValue;

    // A declared count over the cap, or one this component's own payload cannot satisfy, is a MALFORMED frame: the
    // encoder above refuses to emit either. Throwing is what turns it into a false out of
    // ClientReplicationView.TryApply (and a disconnect) instead of a route silently rebuilt from whatever bytes
    // followed. Both checks run BEFORE the count is allocated against, so a hostile ushort can neither
    // over-allocate nor reach the stack allocation below.
    static TileRouteState ReadRoute(BinaryReader r)
    {
        int declared = r.ReadUInt16();
        if (declared > MaxRouteSteps)
            throw new InvalidDataException($"A replicated route declares {declared} steps, over the {MaxRouteSteps} cap.");
        long available = PayloadRemaining(r);
        if (available < declared)
            throw new InvalidDataException(
                $"A replicated route declares {declared} steps behind {available} payload bytes.");
        Span<byte> raw = stackalloc byte[declared];
        if (r.Read(raw) != declared)   // the unbounded fallback: a non-seekable stream has no length to check first
            throw new InvalidDataException($"A replicated route declares {declared} steps the frame does not hold.");
        var steps = new TileDirection[declared];
        for (int i = 0; i < declared; i++)
            steps[i] = raw[i] <= (byte)TileDirection.NE ? (TileDirection)raw[i] : TileDirection.W;
        return new TileRouteState { Remaining = steps };
    }

    // Truncated at a UTF-8 CODEPOINT boundary, never at the byte cap: cutting inside a multi-byte sequence ships
    // half a codepoint and the receiver decodes U+FFFD, so a name whose 64th byte lands mid-glyph would arrive
    // visibly broken. Backing off the continuation bytes costs at most three bytes of a name already at the cap.
    static void WriteIdentity(TileIdentity v, BinaryWriter w)
    {
        byte[] text = Encoding.UTF8.GetBytes(v.DisplayName ?? string.Empty);
        int take = Math.Min(text.Length, MaxDisplayNameBytes);
        while (take > 0 && take < text.Length && (text[take] & 0xC0) == 0x80) take--;
        w.Write((ushort)take);
        w.Write(text, 0, take);
    }

    // Checked on the same rule as the route, and for the same reason. The bytes themselves are decoded LENIENTLY:
    // Encoding.UTF8.GetString substitutes U+FFFD for an invalid sequence rather than throwing, which is what keeps
    // this reader total. A strict decoder here would hand a remote peer an exception in the apply loop.
    static TileIdentity ReadIdentity(BinaryReader r)
    {
        int declared = r.ReadUInt16();
        if (declared > MaxDisplayNameBytes)
            throw new InvalidDataException(
                $"A replicated display name declares {declared} bytes, over the {MaxDisplayNameBytes} cap.");
        long available = PayloadRemaining(r);
        if (available < declared)
            throw new InvalidDataException(
                $"A replicated display name declares {declared} bytes behind {available} payload bytes.");
        Span<byte> text = stackalloc byte[declared];
        if (r.Read(text) != declared)   // the unbounded fallback, as in ReadRoute
            throw new InvalidDataException($"A replicated display name declares {declared} bytes the frame does not hold.");
        return new TileIdentity { DisplayName = Encoding.UTF8.GetString(text) };
    }

    // Four bytes, both fields whole. No length prefix and nothing declared, so there is no lie a frame can tell
    // about its own size here and nothing for a reader to check.
    static void WriteGroundItem(TileGroundItem v, BinaryWriter w)
    {
        w.Write(v.ItemId);
        w.Write(v.Count);
        w.Write(v.X);
        w.Write(v.Z);
        w.Write(v.Plane);
    }

    // CLAMPED for the item, count and planar coordinates. Every bit pattern is meaningful to some game or world,
    // except a non-positive count, which would draw a stack of nothing and therefore clamps to one. The plane is
    // different because the world-bound registry knows its actual domain and refuses a value outside it. The
    // legacy registry has no count and keeps the whole int unchanged.
    static TileGroundItem ReadGroundItem(BinaryReader r, int? planeCount)
    {
        int itemId = r.ReadInt32();
        int count = r.ReadInt32();
        int x = r.ReadInt32();
        int z = r.ReadInt32();
        int plane = ReadPlane(r, planeCount, nameof(TileGroundItem));
        return new TileGroundItem { ItemId = itemId, Count = Math.Max(1, count), X = x, Z = z, Plane = plane };
    }

    // The id as an unsigned varint over its int64 bit pattern, then the payload behind its own varint length. Two
    // bytes for a drop whose instance has no fields, and one byte of id for the first 127 instances a world ever
    // mints, which is what makes a sibling component cheap enough to send every tick beside a twenty byte drop.
    //
    // REFUSES an over-cap payload rather than truncating one, WriteRoute's rule and for a sharper version of its
    // reason: these bytes are opaque, so a truncated payload is not a shorter instance, it is a DIFFERENT one that
    // every reader below would take at face value. Nothing the engine owns can reach this: SpawnGroundItem throws
    // at the door, so a component over the cap was seated by hand and the stack trace names the head that did it.
    static void WriteGroundItemInstance(TileGroundItemInstance v, BinaryWriter w)
    {
        byte[] payload = v.Payload ?? Array.Empty<byte>();
        if (payload.Length > MaxInstancePayloadBytes)
            throw new ArgumentException(
                $"A replicated item instance is capped at {MaxInstancePayloadBytes} payload bytes and this one " +
                $"carries {payload.Length}. TileWorldServer.SpawnGroundItem refuses one that long, so this " +
                "component was seated directly.", nameof(v));
        WriteUnsignedVarint(w, (ulong)v.InstanceId);
        WriteUnsignedVarint(w, (ulong)payload.Length);
        w.Write(payload);
    }

    // TOTAL, which is the third answer to this file's hostile-frame question and the only reader here that gives
    // it. A declared length past this component's own framed payload, or above the cap, answers a ZERO LENGTH
    // payload rather than throwing. The route and the display name throw because a short read there is a
    // plausible-looking answer to a question nobody asked (a route rebuilt out of another component's bytes), and
    // this is the case where it is not: the engine never decodes these bytes, so the only thing it can offer a
    // game is that what it hands over arrived whole. An empty payload says the instance did not, visibly, at the
    // cost of one drop, where a throw would cost the session every other drop and every body in it.
    static TileGroundItemInstance ReadGroundItemInstance(BinaryReader r)
    {
        if (!TryReadUnsignedVarint(r, out ulong rawId))
            return new TileGroundItemInstance { InstanceId = 0L, Payload = Array.Empty<byte>() };
        var instance = new TileGroundItemInstance { InstanceId = (long)rawId, Payload = Array.Empty<byte>() };
        if (!TryReadUnsignedVarint(r, out ulong declared)
            || declared > MaxInstancePayloadBytes
            || declared > (ulong)PayloadRemaining(r))
            return instance;
        var payload = new byte[(int)declared];
        if (r.Read(payload, 0, payload.Length) != payload.Length)   // the unbounded fallback, as in ReadRoute
            return instance;
        instance.Payload = payload;
        return instance;
    }

    // Unsigned minimal LEB128, seven value bits per byte, low group first, at most ten bytes for a 64 bit value,
    // never zig-zagged. PRIVATE, and that is the point rather than an oversight: contracts 15 puts ONE definition
    // of this in the tree, KhaozEngine.Catalog's ContentVarint, and this package carries no dependency on it and
    // must not gain one to encode a number it treats as opaque. A private pair that cannot be called from
    // anywhere else is not a second definition anyone can pick up by mistake, and TileGroundItemInstanceTests
    // pins its bytes against the known encodings so it cannot drift from the one that is.
    static void WriteUnsignedVarint(BinaryWriter w, ulong value)
    {
        while (value >= 0x80)
        {
            w.Write((byte)(value | 0x80));
            value >>= 7;
        }

        w.Write((byte)value);
    }

    // Total, through BaseStream.ReadByte rather than BinaryReader.ReadByte: the stream answers -1 at the end of
    // the framed payload where the reader throws, so running off the end is an answer here rather than an
    // exception. Refuses a non-terminating, over-wide or non-minimal encoding, because contracts 15 gives one
    // value exactly one byte form and a reader that accepted two would let a peer choose which.
    static bool TryReadUnsignedVarint(BinaryReader r, out ulong value)
    {
        value = 0;
        int shift = 0;
        int read = 0;
        while (shift <= 63)
        {
            int b = r.BaseStream.ReadByte();
            if (b < 0) { value = 0; return false; }
            read++;
            if (shift == 63 && (b & 0xFE) != 0) { value = 0; return false; }   // the tenth byte carries bit 63 alone
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if (b == 0 && read > 1) { value = 0; return false; }   // minimal: only a one byte form ends in a zero group
                return true;
            }

            shift += 7;
        }

        value = 0;
        return false;
    }

    // Twenty-four bytes, every field whole. No length prefix and nothing declared, so there is no lie a frame can
    // tell about its own size here and nothing for a reader to check.
    static void WriteObjectState(TileObjectState v, BinaryWriter w)
    {
        w.Write(v.ObjectId);
        w.Write(v.State);
        w.Write(v.X);
        w.Write(v.Z);
        w.Write(v.Plane);
    }

    // NEITHER clamped NOR checked, which is the ground-item reader's rule taken one step further. Every bit
    // pattern of these ints is a meaningful coordinate to SOME world and every long is a legal object id, so
    // there is no malformed frame here. Where the ground item still clamps its count, this has nothing to clamp
    // to: State is opaque to the engine (see TileObjectState), so the engine cannot tell an inconsistent value
    // from a game's own constant, and a clamp here would be the engine inventing a meaning for it.
    //
    // The worst a hostile frame buys is a state on an object the document does not hold, or a state naming a
    // number the game has no look for. Both stop at the game's own map from a state to an archetype, which
    // answers nothing for both, and TileWorldView.OverrideArchetype ignores an id no loaded region holds.
    static TileObjectState ReadObjectState(BinaryReader r) => new()
    {
        ObjectId = r.ReadInt64(),
        State = r.ReadInt32(),
        X = r.ReadInt32(),
        Z = r.ReadInt32(),
        Plane = r.ReadInt32(),
    };

    // Twenty-two bytes, and the SECOND codec here whose bytes never come off a socket: registered on the Migrate
    // channel alone, so the only thing that ever encodes or decodes it is a cell handoff inside one server process.
    // Nothing is clamped for that reason. The plane rides as a whole int rather than the command frame's one byte,
    // preserving its wire layout, and a world-bound registry refuses it when it falls outside that world's count.
    //
    // It is registered AT ALL because the movement pass reads the three components together, so an entity that
    // arrived in the destination cell without this one fell out of the query. See PendingTileCommand's own doc for
    // why carrying it is safe: the movement pass resets it to Continue before the handoff runs, so what crosses is
    // the tick's neutral rather than a click waiting to be applied a second time.
    static void WritePendingCommand(PendingTileCommand v, BinaryWriter w)
    {
        w.Write((byte)v.Command.Kind);
        w.Write(v.Command.Goal.X);
        w.Write(v.Command.Goal.Z);
        w.Write(v.Command.Goal.Plane);
        w.Write((byte)v.Command.Mode);
        w.Write(v.Command.Target);
    }

    static PendingTileCommand ReadPendingCommand(BinaryReader r, int? planeCount)
    {
        var kind = (TileCommandKind)r.ReadByte();
        int x = r.ReadInt32(), z = r.ReadInt32();
        int plane = ReadPlane(r, planeCount, nameof(PendingTileCommand));
        var mode = (TileMoveMode)r.ReadByte();
        long target = r.ReadInt64();
        return new PendingTileCommand { Command = new TileCommand(kind, new TileCoord(x, z, plane), mode, target) };
    }

    static int ReadPlane(BinaryReader reader, int? planeCount, string component)
    {
        int plane = reader.ReadInt32();
        if (planeCount is int count && (uint)plane >= (uint)count)
            throw new InvalidDataException(
                $"A {component} frame names plane {plane}, outside the world's {count} planes.");
        return plane;
    }

    static void WriteHealth(TileHealth v, BinaryWriter w)
    {
        w.Write(v.Current);
        w.Write(v.Max);
    }

    // CLAMPED rather than checked, which is the other half of this file's hostile-frame rule: every bit pattern of a
    // ushort is a meaningful value, so there is no malformed frame here, only an inconsistent pair. Current above
    // Max would draw a health bar past its own track, and would make a fraction over one out of a division nothing
    // guards.
    static TileHealth ReadHealth(BinaryReader r)
    {
        ushort current = r.ReadUInt16();
        ushort max = r.ReadUInt16();
        return new TileHealth { Current = Math.Min(current, max), Max = max };
    }

    // Fifty-eight bytes, and the ONE codec here whose bytes never come off a socket: TileCombatState is registered
    // on the Migrate channel alone, so the only thing that ever encodes or decodes it is a cell handoff inside one
    // server process. Nothing is clamped for that reason, and the day it gains a Replicate bit is the day it needs
    // the same treatment ReadMove gives its enums. The roll-order and swung-at fields at the end cost a viewer
    // nothing for exactly that reason: no client ever reads any of this.
    static void WriteCombat(TileCombatState v, BinaryWriter w)
    {
        w.Write(v.AttackTicks);
        w.Write(v.CooldownRemaining);
        w.Write(v.LastDamagedBy);
        w.Write(v.LastDamagedTick);
        w.Write(v.LastAttackedBy);
        w.Write(v.LastAttackedTick);
        w.Write(v.LastCombatTick);
        w.Write(v.TargetSeen);
        w.Write(v.TargetSinceTick);
    }

    static TileCombatState ReadCombat(BinaryReader r) => new()
    {
        AttackTicks = r.ReadByte(),
        CooldownRemaining = r.ReadByte(),
        LastDamagedBy = r.ReadInt64(),
        LastDamagedTick = r.ReadInt64(),
        LastAttackedBy = r.ReadInt64(),
        LastAttackedTick = r.ReadInt64(),
        LastCombatTick = r.ReadInt64(),
        TargetSeen = r.ReadInt64(),
        TargetSinceTick = r.ReadInt64(),
    };
}
