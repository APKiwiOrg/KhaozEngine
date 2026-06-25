# MMO Phase 1D — Session lifecycle (NetServer / NetClient) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the raw `INetTransport` byte seam (Phase 0) into authenticated, slotted **sessions**: a `NetServer` that accepts connections, runs a handshake, authenticates via a seam, assigns a player slot, and raises join/leave/data events; and a `NetClient` that handshakes and surfaces joined/rejected/data events. This is the connection layer entity replication (1C) sits on.

**Architecture:** A thin session framing over the transport's reliable/unreliable channels: every session message carries a 1-byte opcode (`Hello` / `Welcome` / `Reject` / `Data`). The server maps each `NetConnectionId` to a small-integer **slot** (the same slot model `RemoteCommandQueue` already uses), bounded by `maxPlayers`. Authentication is a seam (`IConnectionAuthenticator`) with a dev `AllowAllAuthenticator`. All headless-tested over `LoopbackTransport`; no new external dependencies.

**Tech Stack:** net10.0, C# (Nullable enable, ImplicitUsings **disabled**), xUnit. All new code in `KhaozEngine.Netcode` (depends only on the Phase 0 transport types already there).

**Spec:** `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md` (Layer 1, sub-project 1D). Builds on Phase 0 (`INetTransport`, `NetConnectionId`, `NetEvent`, `LoopbackTransport`, shipped 7.35.0).

**Engine governance:** one release batch → one version bump at the end (Task 6), additive minor `7.35.0` → `7.36.0`. Per-item commits use `area(scope): summary`; the release commit uses the new version as scope. Work proceeds in this worktree.

---

## Design (decided here; no open forks)

**Wire framing (session layer).** Every payload handed to the transport is `[opcode:1 byte][body]`:

| Opcode | Dir | Body | Channel |
|---|---|---|---|
| `0x01` Hello | client→server | auth token bytes (may be empty) | ReliableOrdered |
| `0x02` Welcome | server→client | assigned slot, 4-byte little-endian int | ReliableOrdered |
| `0x03` Reject | server→client | UTF-8 reason | ReliableOrdered |
| `0x10` Data | both | the game's opaque payload | caller's choice (Unreliable/Reliable) |

The session layer strips the opcode before surfacing `Data` to the game, and prepends it on send. Control opcodes are always reliable.

**Handshake.** Client transport reports `Connected` → client sends `Hello(token)`. Server transport reports `Connected` → server records a *pending* connection (no slot yet). Server receives `Hello` → calls `IConnectionAuthenticator.TryAuthenticate`; on accept it allocates a slot, maps slot↔connection, sends `Welcome(slot)`, and raises `Joined(slot)`; on reject it sends `Reject(reason)` and disconnects (no slot, no Joined). Client receives `Welcome` → state `Joined`, exposes `Slot`; `Reject` → state `Rejected(reason)`.

**Slots.** `SlotAllocator`: hands out the lowest free slot `0..maxPlayers-1`, recycles on release, rejects when full. Same small-int model `RemoteCommandQueue` keys on, so 1C/commands line up.

**Out of scope for 1D** (later phases): entity replication, snapshots, interest management, reconnection/resume tokens, heartbeats/timeouts (the transport surfaces Disconnected; timeout policy is a later hardening pass).

---

## File structure

**New files (all in `KhaozEngine.Netcode`):**
- `SessionOpcode.cs` — the opcode enum + internal frame read/write helpers.
- `IConnectionAuthenticator.cs` — auth seam + `AllowAllAuthenticator` dev default.
- `SlotAllocator.cs` — lowest-free-slot allocator with recycle + cap.
- `ServerSessionEvent.cs` — `{ Kind (Joined/Left/Data), Slot, Data, Reliability }`.
- `ClientSessionEvent.cs` — `{ Kind (Joined/Rejected/Data/Disconnected), Slot, Data, Reliability, RejectReason }`.
- `NetServer.cs` — session server over an `INetTransport`.
- `NetClient.cs` — session client over an `INetTransport`.

**New test files (in `KhaozEngine.Tests/Netcode`):**
- `SlotAllocatorTests.cs`
- `NetSessionTests.cs` — end-to-end over `LoopbackTransport`.

**Modified (Task 6, release):** `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `README.md`, `CLAUDE.md`, `docs/USING-KHAOZENGINE.md`, `docs/ROADMAP.md`, `docs/CONSUMERS.md` (engine-version line).

---

## Task 1: Opcodes + framing helpers

**Files:** Create `KhaozEngine.Netcode/SessionOpcode.cs`; Test `KhaozEngine.Tests/Netcode/NetSessionTests.cs` (create, framing tests).

- [ ] **Step 1: Failing test** — create `KhaozEngine.Tests/Netcode/NetSessionTests.cs`:

```csharp
using System;
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class NetSessionTests
{
    [Fact]
    public void Frame_RoundTrips_OpcodeAndBody()
    {
        byte[] framed = SessionFrame.Write(SessionOpcode.Data, new byte[] { 5, 6, 7 });
        SessionOpcode op = SessionFrame.ReadOpcode(framed);
        byte[] body = SessionFrame.ReadBody(framed);
        Assert.Equal(SessionOpcode.Data, op);
        Assert.Equal(new byte[] { 5, 6, 7 }, body);
    }

    [Fact]
    public void Frame_EmptyOrUnknown_IsSafe()
    {
        Assert.Equal(SessionOpcode.Unknown, SessionFrame.ReadOpcode(Array.Empty<byte>()));
        Assert.Equal(SessionOpcode.Unknown, SessionFrame.ReadOpcode(new byte[] { 0xFF }));
    }
}
```

- [ ] **Step 2: Run — fails** (`SessionFrame`/`SessionOpcode` missing).
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetSessionTests"`
Expected: FAIL (build error).

- [ ] **Step 3: Create `SessionOpcode.cs`**

```csharp
using System;

namespace KhaozEngine.Netcode;

/// <summary>Session-layer message kind, carried as the first byte of every framed session payload.</summary>
public enum SessionOpcode : byte
{
    /// <summary>Unrecognized / empty frame (defensive default for hostile input).</summary>
    Unknown = 0x00,
    /// <summary>client→server: auth token (body may be empty).</summary>
    Hello = 0x01,
    /// <summary>server→client: accepted; body is the assigned slot (4-byte little-endian int).</summary>
    Welcome = 0x02,
    /// <summary>server→client: rejected; body is the UTF-8 reason.</summary>
    Reject = 0x03,
    /// <summary>both: opaque game payload follows.</summary>
    Data = 0x10
}

/// <summary>Reads/writes the 1-byte-opcode session frame: <c>[opcode][body...]</c>.</summary>
public static class SessionFrame
{
    /// <summary>Allocates <c>[opcode][body]</c>.</summary>
    public static byte[] Write(SessionOpcode opcode, ReadOnlySpan<byte> body)
    {
        var buffer = new byte[1 + body.Length];
        buffer[0] = (byte)opcode;
        body.CopyTo(buffer.AsSpan(1));
        return buffer;
    }

    /// <summary>The opcode, or <see cref="SessionOpcode.Unknown"/> for an empty/unrecognized frame.</summary>
    public static SessionOpcode ReadOpcode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) return SessionOpcode.Unknown;
        return frame[0] switch
        {
            (byte)SessionOpcode.Hello => SessionOpcode.Hello,
            (byte)SessionOpcode.Welcome => SessionOpcode.Welcome,
            (byte)SessionOpcode.Reject => SessionOpcode.Reject,
            (byte)SessionOpcode.Data => SessionOpcode.Data,
            _ => SessionOpcode.Unknown
        };
    }

    /// <summary>The body after the opcode byte (empty for a 0- or 1-byte frame).</summary>
    public static byte[] ReadBody(ReadOnlySpan<byte> frame) =>
        frame.Length <= 1 ? Array.Empty<byte>() : frame.Slice(1).ToArray();
}
```

- [ ] **Step 4: Run — passes.** Expected: PASS (2 tests).
- [ ] **Step 5: Commit** — `git commit -m "netcode: add session frame opcode + read/write helpers"`.

---

## Task 2: Auth seam + slot allocator

**Files:** Create `IConnectionAuthenticator.cs`, `SlotAllocator.cs`; Test `KhaozEngine.Tests/Netcode/SlotAllocatorTests.cs`.

- [ ] **Step 1: Failing tests** — create `KhaozEngine.Tests/Netcode/SlotAllocatorTests.cs`:

```csharp
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class SlotAllocatorTests
{
    [Fact]
    public void Allocate_HandsOutLowestFree_AndRecyclesOnRelease()
    {
        var alloc = new SlotAllocator(maxSlots: 3);
        Assert.True(alloc.TryAllocate(out int a)); Assert.Equal(0, a);
        Assert.True(alloc.TryAllocate(out int b)); Assert.Equal(1, b);
        alloc.Release(0);
        Assert.True(alloc.TryAllocate(out int c)); Assert.Equal(0, c); // 0 recycled, lowest free
    }

    [Fact]
    public void Allocate_WhenFull_Fails()
    {
        var alloc = new SlotAllocator(maxSlots: 1);
        Assert.True(alloc.TryAllocate(out _));
        Assert.False(alloc.TryAllocate(out int none));
        Assert.Equal(-1, none);
    }
}
```

- [ ] **Step 2: Run — fails.** Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SlotAllocatorTests"`. Expected: FAIL.

- [ ] **Step 3: Create `IConnectionAuthenticator.cs`**

```csharp
using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// Seam deciding whether a connecting client may join, given the token it presented in its Hello. The engine
/// ships <see cref="AllowAllAuthenticator"/> for dev/local; a real account/token check is the game's/infra's.
/// </summary>
public interface IConnectionAuthenticator
{
    /// <summary>Returns true to accept; on false, <paramref name="rejectReason"/> is sent to the client.</summary>
    bool TryAuthenticate(ReadOnlySpan<byte> token, out string rejectReason);
}

/// <summary>Accepts every connection. Dev/local default; never use as the only gate on an exposed server.</summary>
public sealed class AllowAllAuthenticator : IConnectionAuthenticator
{
    public bool TryAuthenticate(ReadOnlySpan<byte> token, out string rejectReason)
    {
        rejectReason = string.Empty;
        return true;
    }
}
```

- [ ] **Step 4: Create `SlotAllocator.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// Hands out the lowest free player slot in <c>[0, maxSlots)</c>, recycles released slots, and refuses when
/// full. Same small-integer slot model <see cref="RemoteCommandQueue{TCommand}"/> keys commands on.
/// </summary>
public sealed class SlotAllocator
{
    private readonly bool[] used;

    public SlotAllocator(int maxSlots)
    {
        if (maxSlots <= 0) throw new ArgumentOutOfRangeException(nameof(maxSlots), maxSlots, "must be positive");
        used = new bool[maxSlots];
    }

    /// <summary>Max concurrent slots.</summary>
    public int Capacity => used.Length;

    /// <summary>Allocates the lowest free slot. Returns false (slot = -1) when full.</summary>
    public bool TryAllocate(out int slot)
    {
        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i]) { used[i] = true; slot = i; return true; }
        }
        slot = -1;
        return false;
    }

    /// <summary>Frees a slot for reuse. Ignores an already-free or out-of-range slot.</summary>
    public void Release(int slot)
    {
        if (slot >= 0 && slot < used.Length) used[slot] = false;
    }
}
```

- [ ] **Step 5: Run — passes** (2 tests). **Step 6: Commit** — `git commit -m "netcode: add IConnectionAuthenticator seam + SlotAllocator"`.

---

## Task 3: Server/client session event types

**Files:** Create `ServerSessionEvent.cs`, `ClientSessionEvent.cs`. (No standalone tests; exercised in Task 5. Build-verify only.)

- [ ] **Step 1: Create `ServerSessionEvent.cs`**

```csharp
using System;

namespace KhaozEngine.Netcode;

/// <summary>What a <see cref="NetServer"/> surfaces per drained event.</summary>
public enum ServerSessionEventKind { Joined, Left, Data }

/// <summary>One server-side session event: a player joined/left, or sent game data.</summary>
public readonly struct ServerSessionEvent
{
    public ServerSessionEvent(ServerSessionEventKind kind, int slot, byte[] data, NetChannelReliability reliability)
    {
        Kind = kind; Slot = slot; Data = data ?? Array.Empty<byte>(); Reliability = reliability;
    }

    public ServerSessionEventKind Kind { get; }
    public int Slot { get; }
    public byte[] Data { get; }
    public NetChannelReliability Reliability { get; }

    public static ServerSessionEvent Joined(int slot) => new(ServerSessionEventKind.Joined, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);
    public static ServerSessionEvent Left(int slot) => new(ServerSessionEventKind.Left, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);
    public static ServerSessionEvent FromData(int slot, byte[] data, NetChannelReliability r) => new(ServerSessionEventKind.Data, slot, data, r);
}
```

- [ ] **Step 2: Create `ClientSessionEvent.cs`**

```csharp
using System;

namespace KhaozEngine.Netcode;

/// <summary>What a <see cref="NetClient"/> surfaces per drained event.</summary>
public enum ClientSessionEventKind { Joined, Rejected, Data, Disconnected }

/// <summary>One client-side session event: join accepted (with slot), rejected (with reason), data, or dropped.</summary>
public readonly struct ClientSessionEvent
{
    public ClientSessionEvent(ClientSessionEventKind kind, int slot, byte[] data, NetChannelReliability reliability, string rejectReason)
    {
        Kind = kind; Slot = slot; Data = data ?? Array.Empty<byte>(); Reliability = reliability; RejectReason = rejectReason ?? string.Empty;
    }

    public ClientSessionEventKind Kind { get; }
    public int Slot { get; }
    public byte[] Data { get; }
    public NetChannelReliability Reliability { get; }
    public string RejectReason { get; }

    public static ClientSessionEvent Joined(int slot) => new(ClientSessionEventKind.Joined, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered, string.Empty);
    public static ClientSessionEvent Rejected(string reason) => new(ClientSessionEventKind.Rejected, -1, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered, reason);
    public static ClientSessionEvent FromData(byte[] data, NetChannelReliability r) => new(ClientSessionEventKind.Data, -1, data, r, string.Empty);
    public static ClientSessionEvent Disconnected() => new(ClientSessionEventKind.Disconnected, -1, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered, string.Empty);
}
```

- [ ] **Step 3: Build-verify** — `dotnet build KhaozEngine.Netcode/KhaozEngine.Netcode.csproj`. Expected: succeeded.
- [ ] **Step 4: Commit** — `git commit -m "netcode: add server/client session event types"`.

---

## Task 4: `NetServer`

**Files:** Create `KhaozEngine.Netcode/NetServer.cs`. (Behavior covered end-to-end in Task 5.)

- [ ] **Step 1: Create `NetServer.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Session server over an <see cref="INetTransport"/>: accepts connections, runs the Hello/Welcome handshake,
/// authenticates via <see cref="IConnectionAuthenticator"/>, assigns a player slot, and surfaces
/// Joined/Left/Data events (drain with <see cref="TryDequeueEvent"/> after <see cref="Poll"/>).
/// </summary>
public sealed class NetServer
{
    private readonly INetTransport transport;
    private readonly IConnectionAuthenticator authenticator;
    private readonly SlotAllocator slots;
    private readonly Dictionary<int, NetConnectionId> connectionBySlot = new();
    private readonly Dictionary<NetConnectionId, int> slotByConnection = new();
    private readonly Queue<ServerSessionEvent> inbox = new();

    public NetServer(INetTransport transport, int maxPlayers, IConnectionAuthenticator authenticator)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        slots = new SlotAllocator(maxPlayers);
    }

    /// <summary>Pumps the transport and processes handshake/data/disconnect into session events.</summary>
    public void Poll()
    {
        transport.Poll();
        while (transport.TryDequeueEvent(out NetEvent ev))
        {
            switch (ev.Type)
            {
                case NetEventType.Connected:
                    // Pending: no slot until a valid Hello arrives.
                    break;
                case NetEventType.Disconnected:
                    if (slotByConnection.TryGetValue(ev.Connection, out int leftSlot))
                    {
                        RemovePeer(ev.Connection, leftSlot);
                        inbox.Enqueue(ServerSessionEvent.Left(leftSlot));
                    }
                    break;
                case NetEventType.Data:
                    HandleData(ev);
                    break;
            }
        }
    }

    private void HandleData(NetEvent ev)
    {
        SessionOpcode op = SessionFrame.ReadOpcode(ev.Data);
        if (slotByConnection.TryGetValue(ev.Connection, out int slot))
        {
            // Established peer: only Data is meaningful; ignore stray control opcodes.
            if (op == SessionOpcode.Data)
                inbox.Enqueue(ServerSessionEvent.FromData(slot, SessionFrame.ReadBody(ev.Data), ev.Reliability));
            return;
        }

        // Not yet established: the only thing we accept is a Hello.
        if (op != SessionOpcode.Hello) return;

        byte[] token = SessionFrame.ReadBody(ev.Data);
        if (!authenticator.TryAuthenticate(token, out string reason))
        {
            transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Reject, Encoding.UTF8.GetBytes(reason)), NetChannelReliability.ReliableOrdered);
            transport.Disconnect(ev.Connection);
            return;
        }
        if (!slots.TryAllocate(out int newSlot))
        {
            transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Reject, Encoding.UTF8.GetBytes("server full")), NetChannelReliability.ReliableOrdered);
            transport.Disconnect(ev.Connection);
            return;
        }
        connectionBySlot[newSlot] = ev.Connection;
        slotByConnection[ev.Connection] = newSlot;
        var slotBytes = new byte[4];
        BitConverter.TryWriteBytes(slotBytes, newSlot); // little-endian on supported platforms
        transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Welcome, slotBytes), NetChannelReliability.ReliableOrdered);
        inbox.Enqueue(ServerSessionEvent.Joined(newSlot));
    }

    private void RemovePeer(NetConnectionId conn, int slot)
    {
        slotByConnection.Remove(conn);
        connectionBySlot.Remove(slot);
        slots.Release(slot);
    }

    /// <summary>Drains one session event. False when none remain this poll.</summary>
    public bool TryDequeueEvent(out ServerSessionEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    /// <summary>Sends game data to one slot. No-op for an unknown slot.</summary>
    public void SendTo(int slot, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (connectionBySlot.TryGetValue(slot, out NetConnectionId conn))
            transport.Send(conn, SessionFrame.Write(SessionOpcode.Data, payload), reliability);
    }

    /// <summary>Sends game data to every joined slot.</summary>
    public void Broadcast(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        byte[] frame = SessionFrame.Write(SessionOpcode.Data, payload);
        foreach (NetConnectionId conn in connectionBySlot.Values)
            transport.Send(conn, frame, reliability);
    }
}
```

- [ ] **Step 2: Build-verify** — `dotnet build KhaozEngine.Netcode/KhaozEngine.Netcode.csproj`. Expected: succeeded.
- [ ] **Step 3: Commit** — `git commit -m "netcode: add NetServer (handshake, slots, join/leave/data events)"`.

---

## Task 5: `NetClient` + end-to-end session test

**Files:** Create `KhaozEngine.Netcode/NetClient.cs`; extend `KhaozEngine.Tests/Netcode/NetSessionTests.cs`.

- [ ] **Step 1: Create `NetClient.cs`**

```csharp
using System;
using System.Text;

namespace KhaozEngine.Netcode;

/// <summary>
/// Session client over an <see cref="INetTransport"/>: on transport connect it sends Hello(token), then
/// surfaces Joined(slot)/Rejected(reason)/Data/Disconnected (drain with <see cref="TryDequeueEvent"/> after
/// <see cref="Poll"/>). <see cref="Slot"/> is valid once joined.
/// </summary>
public sealed class NetClient
{
    private readonly INetTransport transport;
    private readonly byte[] token;
    private readonly System.Collections.Generic.Queue<ClientSessionEvent> inbox = new();
    private bool helloSent;

    public NetClient(INetTransport transport, byte[]? token = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.token = token ?? Array.Empty<byte>();
    }

    /// <summary>The assigned slot once <see cref="ClientSessionEventKind.Joined"/> has been observed, else -1.</summary>
    public int Slot { get; private set; } = -1;

    public void Poll()
    {
        transport.Poll();
        while (transport.TryDequeueEvent(out NetEvent ev))
        {
            switch (ev.Type)
            {
                case NetEventType.Connected:
                    if (!helloSent)
                    {
                        helloSent = true;
                        transport.Send(ev.Connection, SessionFrame.Write(SessionOpcode.Hello, token), NetChannelReliability.ReliableOrdered);
                    }
                    break;
                case NetEventType.Disconnected:
                    inbox.Enqueue(ClientSessionEvent.Disconnected());
                    break;
                case NetEventType.Data:
                    HandleData(ev);
                    break;
            }
        }
    }

    private void HandleData(NetEvent ev)
    {
        switch (SessionFrame.ReadOpcode(ev.Data))
        {
            case SessionOpcode.Welcome:
                byte[] body = SessionFrame.ReadBody(ev.Data);
                Slot = body.Length >= 4 ? BitConverter.ToInt32(body, 0) : -1;
                inbox.Enqueue(ClientSessionEvent.Joined(Slot));
                break;
            case SessionOpcode.Reject:
                inbox.Enqueue(ClientSessionEvent.Rejected(Encoding.UTF8.GetString(SessionFrame.ReadBody(ev.Data))));
                break;
            case SessionOpcode.Data:
                inbox.Enqueue(ClientSessionEvent.FromData(SessionFrame.ReadBody(ev.Data), ev.Reliability));
                break;
        }
    }

    public bool TryDequeueEvent(out ClientSessionEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    /// <summary>Sends game data to the server.</summary>
    public void Send(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        // Loopback/UDP both surface the server as connection id 1.
        transport.Send(new NetConnectionId(1), SessionFrame.Write(SessionOpcode.Data, payload), reliability);
    }
}
```

- [ ] **Step 2: Add the end-to-end tests** to `NetSessionTests.cs` (inside the class):

```csharp
    // Pumps both ends until each has no more events to surface this round (loopback settles in a few rounds).
    private static void Pump(NetServer server, NetClient client, int rounds = 8)
    {
        for (int i = 0; i < rounds; i++) { server.Poll(); client.Poll(); }
    }

    private static System.Collections.Generic.List<ClientSessionEvent> DrainClient(NetClient c)
    {
        var list = new System.Collections.Generic.List<ClientSessionEvent>();
        while (c.TryDequeueEvent(out ClientSessionEvent e)) list.Add(e);
        return list;
    }

    private static System.Collections.Generic.List<ServerSessionEvent> DrainServer(NetServer s)
    {
        var list = new System.Collections.Generic.List<ServerSessionEvent>();
        while (s.TryDequeueEvent(out ServerSessionEvent e)) list.Add(e);
        return list;
    }

    [Fact]
    public void Client_Handshakes_JoinsSlot0_AndExchangesData()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new NetServer(st, maxPlayers: 4, new AllowAllAuthenticator());
        var client = new NetClient(ct);

        Pump(server, client);

        Assert.Contains(DrainServer(server), e => e.Kind == ServerSessionEventKind.Joined && e.Slot == 0);
        Assert.Contains(DrainClient(client), e => e.Kind == ClientSessionEventKind.Joined && e.Slot == 0);
        Assert.Equal(0, client.Slot);

        // server -> client data
        server.SendTo(0, new byte[] { 99 }, NetChannelReliability.ReliableOrdered);
        Pump(server, client);
        Assert.Contains(DrainClient(client), e => e.Kind == ClientSessionEventKind.Data && e.Data.Length == 1 && e.Data[0] == 99);

        // client -> server data
        client.Send(new byte[] { 7, 7 }, NetChannelReliability.UnreliableSequenced);
        Pump(server, client);
        Assert.Contains(DrainServer(server), e => e.Kind == ServerSessionEventKind.Data && e.Slot == 0 && e.Data.Length == 2);
    }

    private sealed class DenyAll : IConnectionAuthenticator
    {
        public bool TryAuthenticate(ReadOnlySpan<byte> token, out string rejectReason)
        { rejectReason = "nope"; return false; }
    }

    [Fact]
    public void Client_RejectedByAuthenticator_GetsReason_NoSlot()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new NetServer(st, maxPlayers: 4, new DenyAll());
        var client = new NetClient(ct);

        Pump(server, client);

        Assert.DoesNotContain(DrainServer(server), e => e.Kind == ServerSessionEventKind.Joined);
        var rejected = Assert.Single(DrainClient(client), e => e.Kind == ClientSessionEventKind.Rejected);
        Assert.Equal("nope", rejected.RejectReason);
        Assert.Equal(-1, client.Slot);
    }
```

- [ ] **Step 3: Run — passes.** Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetSessionTests"`. Expected: PASS (framing + 2 e2e tests).

If the loopback settle-rounds prove too few (an assertion misses because a message needed one more poll round), raise `rounds` in `Pump` — do not weaken an assertion.

- [ ] **Step 4: Commit** — `git commit -m "netcode: add NetClient + end-to-end session handshake/data/reject tests"`.

---

## Task 6: Batch release `7.36.0`

- [ ] **Step 1: Full headless suite green** — `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Category!=LiveSocket"`. Expected: PASS.
- [ ] **Step 2: Bump** `Directory.Build.props` `7.35.0` → `7.36.0`.
- [ ] **Step 3: `CHANGELOG.md`** newest-first entry:

```markdown
## 7.36.0

MMO netcode stack, Phase 1D (session lifecycle). `KhaozEngine.Netcode` gains `NetServer` / `NetClient` over
the `INetTransport` seam: a Hello/Welcome/Reject handshake (1-byte-opcode `SessionFrame`), `IConnectionAuthenticator`
seam (+ `AllowAllAuthenticator` dev default), `SlotAllocator` (lowest-free player slot, recycled, capped), and
Joined/Left/Data session events (`ServerSessionEvent` / `ClientSessionEvent`). Headless-tested end-to-end over
`LoopbackTransport` (handshake, data both directions, auth-reject). Additive, minor. Plan:
`docs/superpowers/plans/2026-06-25-mmo-phase1d-session-lifecycle.md`.
```

- [ ] **Step 4: `CHANGENOTES.md`** one-liner (top): `- **7.36.0**: MMO netcode Phase 1D - NetServer/NetClient session layer over INetTransport (handshake, auth seam, player slots, join/leave/data events). Additive, minor.`
- [ ] **Step 5: Guard-checked version lines** → `7.36.0` (`docs/CONSUMERS.md`, `docs/ROADMAP.md`, README `<PackageReference>` examples).
- [ ] **Step 6: Doc sweep** — README `KhaozEngine.Netcode` cell (mention `NetServer`/`NetClient`); `CLAUDE.md` Netcode note; `docs/USING-KHAOZENGINE.md` extend the multiplayer section with the session layer; `docs/ROADMAP.md` mark Phase 1D shipped under the MMO program. `grep -rn "NetServer\|NetClient\|SessionFrame" *.md docs/*.md CLAUDE.md` and confirm coverage.
- [ ] **Step 7: Guard** — `bash scripts/check-doc-versions.sh` → matches `7.36.0`.
- [ ] **Step 8: Pack** — `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`.
- [ ] **Step 9: Commit** — `git commit -m "netcode(7.36.0): MMO Phase 1D - NetServer/NetClient session lifecycle"`.
- [ ] **Step 10: Tag** — `git tag v7.36.0` (push + finishing handled per the engine release ritual).

---

## Definition of done (1D)

- `NetServer`/`NetClient` complete a handshake over `LoopbackTransport`, assign/observe slot 0, exchange data both ways, and the auth-reject path yields a reason with no slot — all headless.
- `SessionFrame`, `SessionOpcode`, `IConnectionAuthenticator` (+ `AllowAllAuthenticator`), `SlotAllocator`, and the two session-event types are public in `KhaozEngine.Netcode`.
- One version bump (`7.36.0`) with CHANGELOG + CHANGENOTES + doc sweep; guard green; packed; tagged.
- **Unblocks 1C (entity replication):** snapshots flow per slot via `NetServer.SendTo`/`Broadcast` + `ServerSessionEvent.Data`; commands key on the same slot as `RemoteCommandQueue`.
