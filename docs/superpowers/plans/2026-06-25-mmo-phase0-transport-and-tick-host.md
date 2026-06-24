# MMO Phase 0 — Transport seam + Fixed-tick host — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the two foundation sub-projects of the MMO netcode stack — an `INetTransport` byte-transport seam (with a deterministic in-memory loopback and a LiteNetLib UDP binding) and a headless `FixedTickHost` fixed-timestep accumulator — so every later phase (replication, interest management, zoning) has a wire seam and a render-independent server tick to build on.

**Architecture:** `INetTransport` is a pure send/receive seam that the netcode stack talks to instead of any concrete transport. Two implementations: `LoopbackTransport` (in-memory, no sockets/threads, deterministic — the headless test + local-play transport) and `LiteNetLib{Server,Client}Transport` (real reliable-UDP, reusing the existing `ChannelSplitter.ToDeliveryMethod` reliability mapping). `FixedTickHost` is a standalone accumulator promoted from SpaceGame's `FixedStepRunDriver`, stripped of its lockstep-specific input-delay/dual-counter model down to a single deterministic tick stream. Neither sub-project depends on the other; they ship together as one release batch.

**Tech Stack:** net10.0, C# (Nullable enable, ImplicitUsings **disabled** — every file needs explicit `using` directives), xUnit 2.9.2, LiteNetLib 2.1.2. New package `KhaozEngine.Simulation`; extends `KhaozEngine.Netcode` and `KhaozEngine.Netcode.LiteNetLib`.

**Spec:** `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md` (sub-projects 0A + 0B).

**Engine governance:** This is one release *batch* (sub-projects 0A + 0B). Per `CLAUDE.md`: commit each item individually with an `area(scope): summary` subject, but do the single `Directory.Build.props` version bump + `CHANGELOG.md` + `CHANGENOTES.md` + doc sweep + `dotnet pack` + tag **once at the end** (Task 8). Work in a dedicated worktree (this plan assumes one already exists).

---

## File structure

**New files (sub-project 0A — transport, in `KhaozEngine.Netcode`):**
- `KhaozEngine.Netcode/NetConnectionId.cs` — opaque connection handle (value type).
- `KhaozEngine.Netcode/NetEventType.cs` — Connected / Disconnected / Data enum.
- `KhaozEngine.Netcode/NetEvent.cs` — one drained transport event (value type).
- `KhaozEngine.Netcode/INetTransport.cs` — the seam interface.
- `KhaozEngine.Netcode/LoopbackTransport.cs` — in-memory paired transport.

**New files (0A — LiteNetLib binding, in `KhaozEngine.Netcode.LiteNetLib`):**
- `KhaozEngine.Netcode.LiteNetLib/LiteNetLibServerTransport.cs`
- `KhaozEngine.Netcode.LiteNetLib/LiteNetLibClientTransport.cs`

**New files (sub-project 0B — fixed-tick host, new package `KhaozEngine.Simulation`):**
- `KhaozEngine.Simulation/KhaozEngine.Simulation.csproj`
- `KhaozEngine.Simulation/FixedTickHost.cs`
- `KhaozEngine.Simulation/README.md`

**New test files (all in `KhaozEngine.Tests`):**
- `KhaozEngine.Tests/Netcode/LoopbackTransportTests.cs`
- `KhaozEngine.Tests/Netcode/LiteNetLibTransportTests.cs`
- `KhaozEngine.Tests/Simulation/FixedTickHostTests.cs`
- `KhaozEngine.Tests/Simulation/FixedTickHostSimulatorIntegrationTests.cs`

**Modified files:**
- `KhaozEngine.slnx` — register `KhaozEngine.Simulation`.
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — reference `KhaozEngine.Simulation`.
- `KhaozEngine.Server/KhaozEngine.Server.csproj` — pull `KhaozEngine.Simulation` into the server umbrella.
- Release-batch docs (Task 8): `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `README.md`, `CLAUDE.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `docs/USING-KHAOZENGINE.md`.

**Conventions to match (from existing code):** test namespace `KhaozEngine.Tests.<Area>`, `using Xunit;`, `[Fact]`, `Assert.*`. Transport types live in the `KhaozEngine.Netcode` namespace next to `RemoteCommandQueue`/`NetChannelReliability`. Run a focused test with `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~<Name>"`.

---

## Task 1: Transport value types (`NetConnectionId`, `NetEventType`, `NetEvent`)

**Files:**
- Create: `KhaozEngine.Netcode/NetConnectionId.cs`
- Create: `KhaozEngine.Netcode/NetEventType.cs`
- Create: `KhaozEngine.Netcode/NetEvent.cs`
- Test: `KhaozEngine.Tests/Netcode/LoopbackTransportTests.cs` (created here, fleshed out in Task 3)

- [ ] **Step 1: Write a failing test for the value types**

Create `KhaozEngine.Tests/Netcode/LoopbackTransportTests.cs`:

```csharp
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class LoopbackTransportTests
{
    [Fact]
    public void NetConnectionId_None_IsInvalid_AndPositiveIsValid()
    {
        Assert.False(NetConnectionId.None.IsValid);
        Assert.True(new NetConnectionId(1).IsValid);
        Assert.Equal(new NetConnectionId(1), new NetConnectionId(1)); // value equality
    }

    [Fact]
    public void NetEvent_FromData_CarriesPayloadAndReliability()
    {
        var ev = NetEvent.FromData(new NetConnectionId(1), new byte[] { 7, 8 }, NetChannelReliability.ReliableOrdered);
        Assert.Equal(NetEventType.Data, ev.Type);
        Assert.Equal(new NetConnectionId(1), ev.Connection);
        Assert.Equal(new byte[] { 7, 8 }, ev.Data);
        Assert.Equal(NetChannelReliability.ReliableOrdered, ev.Reliability);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails (does not compile)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LoopbackTransportTests"`
Expected: FAIL — build error, `NetConnectionId`/`NetEvent`/`NetEventType` do not exist.

- [ ] **Step 3: Create `NetConnectionId.cs`**

```csharp
namespace KhaozEngine.Netcode;

/// <summary>
/// Opaque handle to a transport-level connection. Value 0 is the none/sentinel id; valid ids are positive.
/// Value-equatable so it can be a dictionary key and compared directly.
/// </summary>
public readonly record struct NetConnectionId(int Value)
{
    /// <summary>The sentinel "no connection" id.</summary>
    public static NetConnectionId None => new(0);

    /// <summary>True when this is a real (positive) connection id.</summary>
    public bool IsValid => Value > 0;
}
```

- [ ] **Step 4: Create `NetEventType.cs`**

```csharp
namespace KhaozEngine.Netcode;

/// <summary>The kind of a <see cref="NetEvent"/> drained from an <see cref="INetTransport"/>.</summary>
public enum NetEventType
{
    /// <summary>A peer connected; <see cref="NetEvent.Connection"/> identifies it.</summary>
    Connected,

    /// <summary>A peer disconnected; <see cref="NetEvent.Connection"/> identifies it.</summary>
    Disconnected,

    /// <summary>Data arrived; <see cref="NetEvent.Data"/> holds the payload, <see cref="NetEvent.Reliability"/> its channel.</summary>
    Data
}
```

- [ ] **Step 5: Create `NetEvent.cs`**

```csharp
using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// A single transport event drained via <see cref="INetTransport.TryDequeueEvent"/>. For
/// <see cref="NetEventType.Data"/> the payload is in <see cref="Data"/> and the channel it arrived on is
/// <see cref="Reliability"/>; for Connected/Disconnected the payload is empty.
/// </summary>
/// <remarks>
/// Phase 0 keeps <see cref="Data"/> as an owned <c>byte[]</c> copy for simplicity. A later phase replaces it
/// with pooled buffers to cut per-event allocation; consumers should treat the array as read-only and not retain it.
/// </remarks>
public readonly struct NetEvent
{
    public NetEvent(NetEventType type, NetConnectionId connection, byte[] data, NetChannelReliability reliability)
    {
        Type = type;
        Connection = connection;
        Data = data ?? Array.Empty<byte>();
        Reliability = reliability;
    }

    public NetEventType Type { get; }
    public NetConnectionId Connection { get; }
    public byte[] Data { get; }
    public NetChannelReliability Reliability { get; }

    public static NetEvent Connected(NetConnectionId c) =>
        new(NetEventType.Connected, c, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    public static NetEvent Disconnected(NetConnectionId c) =>
        new(NetEventType.Disconnected, c, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    public static NetEvent FromData(NetConnectionId c, byte[] data, NetChannelReliability reliability) =>
        new(NetEventType.Data, c, data, reliability);
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LoopbackTransportTests"`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Netcode/NetConnectionId.cs KhaozEngine.Netcode/NetEventType.cs KhaozEngine.Netcode/NetEvent.cs KhaozEngine.Tests/Netcode/LoopbackTransportTests.cs
git commit -m "netcode: add transport value types (NetConnectionId/NetEvent/NetEventType)"
```

---

## Task 2: `INetTransport` seam interface

**Files:**
- Create: `KhaozEngine.Netcode/INetTransport.cs`

- [ ] **Step 1: Create `INetTransport.cs`** (an interface has no behavior to test directly; it is exercised by `LoopbackTransport` in Task 3)

```csharp
using System;

namespace KhaozEngine.Netcode;

/// <summary>
/// The byte-transport seam: the only thing the netcode stack knows about the wire. Implementations are an
/// in-memory loopback (deterministic; headless tests + local play) or a real UDP binding
/// (KhaozEngine.Netcode.LiteNetLib). Server vs client role is decided at construction; this interface is
/// pure I/O. Single-threaded by contract: call <see cref="Poll"/> then drain with <see cref="TryDequeueEvent"/>
/// from the same thread that owns the host loop.
/// </summary>
public interface INetTransport : IDisposable
{
    /// <summary>Pumps the underlying transport, enqueueing any pending events for <see cref="TryDequeueEvent"/>.</summary>
    void Poll();

    /// <summary>Drains one queued event in arrival order. Returns false when none remain this poll.</summary>
    bool TryDequeueEvent(out NetEvent ev);

    /// <summary>Sends <paramref name="payload"/> to <paramref name="target"/> on the given reliability channel.</summary>
    void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability);

    /// <summary>Disconnects a single connection. No-op if the connection is unknown.</summary>
    void Disconnect(NetConnectionId connection);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build KhaozEngine.Netcode/KhaozEngine.Netcode.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Netcode/INetTransport.cs
git commit -m "netcode: add INetTransport seam interface"
```

---

## Task 3: `LoopbackTransport` (in-memory paired transport)

**Files:**
- Create: `KhaozEngine.Netcode/LoopbackTransport.cs`
- Test: `KhaozEngine.Tests/Netcode/LoopbackTransportTests.cs` (extend)

- [ ] **Step 1: Add failing tests** to `KhaozEngine.Tests/Netcode/LoopbackTransportTests.cs` (add these methods inside the existing class):

```csharp
    private static (LoopbackTransport server, LoopbackTransport client) Pair() => LoopbackTransport.CreatePair();

    private static System.Collections.Generic.List<NetEvent> Drain(LoopbackTransport t)
    {
        var list = new System.Collections.Generic.List<NetEvent>();
        t.Poll();
        while (t.TryDequeueEvent(out NetEvent ev)) list.Add(ev);
        return list;
    }

    [Fact]
    public void FirstPoll_YieldsConnected_OnBothEnds()
    {
        var (server, client) = Pair();
        Assert.Contains(Drain(server), e => e.Type == NetEventType.Connected && e.Connection == new NetConnectionId(1));
        Assert.Contains(Drain(client), e => e.Type == NetEventType.Connected && e.Connection == new NetConnectionId(1));
    }

    [Fact]
    public void Send_IsDeliveredToPeer_AfterPeerPolls_WithReliabilityPreserved()
    {
        var (server, client) = Pair();
        Drain(server); Drain(client); // clear the connect events

        server.Send(new NetConnectionId(1), new byte[] { 1, 2, 3 }, NetChannelReliability.UnreliableSequenced);

        var clientEvents = Drain(client);
        var data = Assert.Single(clientEvents, e => e.Type == NetEventType.Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, data.Data);
        Assert.Equal(NetChannelReliability.UnreliableSequenced, data.Reliability);
    }

    [Fact]
    public void Send_IsNotVisible_BeforePeerPolls()
    {
        var (server, client) = Pair();
        Drain(server); Drain(client);
        server.Send(new NetConnectionId(1), new byte[] { 9 }, NetChannelReliability.ReliableOrdered);
        Assert.False(client.TryDequeueEvent(out _)); // nothing surfaces without a Poll
    }

    [Fact]
    public void Disconnect_YieldsDisconnected_OnPeer()
    {
        var (server, client) = Pair();
        Drain(server); Drain(client);
        server.Disconnect(new NetConnectionId(1));
        Assert.Contains(Drain(client), e => e.Type == NetEventType.Disconnected);
    }
```

- [ ] **Step 2: Run to verify failure (does not compile — `LoopbackTransport` missing)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LoopbackTransportTests"`
Expected: FAIL — `LoopbackTransport` does not exist.

- [ ] **Step 3: Create `LoopbackTransport.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// A deterministic, in-memory transport pair: no sockets, no threads. <see cref="CreatePair"/> returns two
/// linked endpoints; a Send on one becomes a Data event on the other after that other endpoint Polls. Both
/// endpoints observe the peer as connection id 1, and each surfaces a Connected event for the peer on its
/// first Poll. Used for headless netcode tests and single-process local play.
/// </summary>
public sealed class LoopbackTransport : INetTransport
{
    private static readonly NetConnectionId PeerId = new(1);

    private readonly Queue<NetEvent> inbox = new();
    private readonly List<(byte[] data, NetChannelReliability reliability)> pendingFromPeer = new();
    private LoopbackTransport? peer;
    private bool announcedConnect;
    private bool disposed;

    private LoopbackTransport() { }

    /// <summary>Creates two linked endpoints (e.g. a server end and a client end).</summary>
    public static (LoopbackTransport a, LoopbackTransport b) CreatePair()
    {
        var a = new LoopbackTransport();
        var b = new LoopbackTransport();
        a.peer = b;
        b.peer = a;
        return (a, b);
    }

    public void Poll()
    {
        if (disposed) return;

        if (!announcedConnect && peer is not null)
        {
            announcedConnect = true;
            inbox.Enqueue(NetEvent.Connected(PeerId));
        }

        // Surface anything the peer sent us, in send order (deterministic).
        for (int i = 0; i < pendingFromPeer.Count; i++)
        {
            (byte[] data, NetChannelReliability reliability) = pendingFromPeer[i];
            inbox.Enqueue(NetEvent.FromData(PeerId, data, reliability));
        }
        pendingFromPeer.Clear();
    }

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (disposed || peer is null) return;
        if (target != PeerId)
            throw new ArgumentException($"Loopback has only peer id {PeerId.Value}.", nameof(target));
        peer.pendingFromPeer.Add((payload.ToArray(), reliability));
    }

    public void Disconnect(NetConnectionId connection)
    {
        if (disposed || peer is null) return;
        peer.inbox.Enqueue(NetEvent.Disconnected(PeerId));
        peer.peer = null;
        peer = null;
    }

    public void Dispose()
    {
        disposed = true;
        peer = null;
        inbox.Clear();
        pendingFromPeer.Clear();
    }
}
```

- [ ] **Step 4: Run to verify all loopback tests pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LoopbackTransportTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Netcode/LoopbackTransport.cs KhaozEngine.Tests/Netcode/LoopbackTransportTests.cs
git commit -m "netcode: add deterministic in-memory LoopbackTransport + tests"
```

---

## Task 4: LiteNetLib UDP transport bindings

**Files:**
- Create: `KhaozEngine.Netcode.LiteNetLib/LiteNetLibServerTransport.cs`
- Create: `KhaozEngine.Netcode.LiteNetLib/LiteNetLibClientTransport.cs`
- Test: `KhaozEngine.Tests/Netcode/LiteNetLibTransportTests.cs`

Note: the live round-trip is a real-socket smoke (UDP over localhost), traited `Category=LiveSocket` so it can be excluded from deterministic headless CI; a construct/dispose test runs unconditionally. This mirrors the repo's "live smoke needs hardware, model is compile-verified" pattern (ROADMAP, input breadth).

- [ ] **Step 1: Write the tests**

Create `KhaozEngine.Tests/Netcode/LiteNetLibTransportTests.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class LiteNetLibTransportTests
{
    [Fact]
    public void Server_ConstructAndDispose_DoesNotThrow()
    {
        using var server = new LiteNetLibServerTransport(port: 0); // 0 = OS-assigned free port
        server.Poll();
    }

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void ClientServer_OverLocalhost_RoundTripsAMessage()
    {
        const int port = 47654;
        using var server = new LiteNetLibServerTransport(port);
        using var client = new LiteNetLibClientTransport("127.0.0.1", port);

        NetConnectionId clientOnServer = PumpUntil(server, client,
            () => TryFind(server, NetEventType.Connected, out NetConnectionId id) ? id : (NetConnectionId?)null)
            ?? throw new Exception("server never saw the client connect");

        server.Send(clientOnServer, new byte[] { 42 }, NetChannelReliability.ReliableOrdered);

        byte[]? received = PumpUntil(server, client,
            () => TryFindData(client, out byte[] d) ? d : null);
        Assert.NotNull(received);
        Assert.Equal(new byte[] { 42 }, received);
    }

    // Pumps both transports up to a bounded number of iterations until selector returns non-null.
    private static T? PumpUntil<T>(INetTransport a, INetTransport b, Func<T?> selector) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            a.Poll();
            b.Poll();
            T? hit = selector();
            if (hit is not null) return hit;
            Thread.Sleep(15);
        }
        return null;
    }

    private static NetConnectionId? PumpUntil(INetTransport a, INetTransport b, Func<NetConnectionId?> selector)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            a.Poll();
            b.Poll();
            NetConnectionId? hit = selector();
            if (hit is not null) return hit;
            Thread.Sleep(15);
        }
        return null;
    }

    private static bool TryFind(INetTransport t, NetEventType type, out NetConnectionId id)
    {
        while (t.TryDequeueEvent(out NetEvent ev))
        {
            if (ev.Type == type) { id = ev.Connection; return true; }
        }
        id = NetConnectionId.None;
        return false;
    }

    private static bool TryFindData(INetTransport t, out byte[] data)
    {
        while (t.TryDequeueEvent(out NetEvent ev))
        {
            if (ev.Type == NetEventType.Data) { data = ev.Data; return true; }
        }
        data = Array.Empty<byte>();
        return false;
    }
}
```

- [ ] **Step 2: Run to verify failure (transports missing)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LiteNetLibTransportTests"`
Expected: FAIL — `LiteNetLibServerTransport`/`LiteNetLibClientTransport` do not exist.

- [ ] **Step 3: Create `LiteNetLibServerTransport.cs`**

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using LiteNetLib;

namespace KhaozEngine.Netcode.LiteNetLib;

/// <summary>
/// Server-side <see cref="INetTransport"/> over LiteNetLib reliable-UDP. Listens on a UDP port, accepts
/// connections whose key matches <c>connectionKey</c>, and surfaces each peer as
/// <see cref="NetConnectionId"/> = <c>peer.Id + 1</c> (so a valid id is always positive). Reuses
/// <see cref="ChannelSplitter.ToDeliveryMethod"/> for the reliability mapping. Single-threaded: call
/// <see cref="Poll"/> from the host-loop thread.
/// </summary>
public sealed class LiteNetLibServerTransport : INetTransport
{
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager manager;
    private readonly Queue<NetEvent> inbox = new();
    private readonly Dictionary<int, NetPeer> peersById = new();
    private readonly string connectionKey;

    /// <param name="port">UDP port to listen on; 0 lets the OS assign a free port (useful in tests).</param>
    /// <param name="connectionKey">Shared key a client must present to be accepted.</param>
    public LiteNetLibServerTransport(int port, string connectionKey = "khaoz")
    {
        this.connectionKey = connectionKey ?? throw new ArgumentNullException(nameof(connectionKey));
        manager = new NetManager(listener);
        WireListener();
        if (!manager.Start(port))
            throw new InvalidOperationException($"Failed to start UDP listener on port {port}.");
    }

    private static NetConnectionId ToId(NetPeer peer) => new(peer.Id + 1);

    private void WireListener()
    {
        listener.ConnectionRequestEvent += request => request.AcceptIfKey(connectionKey);

        listener.PeerConnectedEvent += peer =>
        {
            peersById[peer.Id] = peer;
            inbox.Enqueue(NetEvent.Connected(ToId(peer)));
        };

        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            peersById.Remove(peer.Id);
            inbox.Enqueue(NetEvent.Disconnected(ToId(peer)));
        };

        listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            byte[] data = reader.GetRemainingBytes();
            NetChannelReliability reliability = deliveryMethod == DeliveryMethod.ReliableOrdered
                ? NetChannelReliability.ReliableOrdered
                : NetChannelReliability.UnreliableSequenced;
            inbox.Enqueue(NetEvent.FromData(ToId(peer), data, reliability));
            reader.Recycle();
        };
    }

    public void Poll() => manager.PollEvents();

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (peersById.TryGetValue(target.Value - 1, out NetPeer? peer))
            peer.Send(payload.ToArray(), ChannelSplitter.ToDeliveryMethod(reliability));
    }

    public void Disconnect(NetConnectionId connection)
    {
        if (peersById.TryGetValue(connection.Value - 1, out NetPeer? peer))
            peer.Disconnect();
    }

    public void Dispose() => manager.Stop();
}
```

- [ ] **Step 4: Create `LiteNetLibClientTransport.cs`**

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using LiteNetLib;

namespace KhaozEngine.Netcode.LiteNetLib;

/// <summary>
/// Client-side <see cref="INetTransport"/> over LiteNetLib reliable-UDP. Connects to a server on
/// construction and surfaces the server peer as <see cref="NetConnectionId"/> = <c>peer.Id + 1</c>. Reuses
/// <see cref="ChannelSplitter.ToDeliveryMethod"/> for the reliability mapping.
/// </summary>
public sealed class LiteNetLibClientTransport : INetTransport
{
    private readonly EventBasedNetListener listener = new();
    private readonly NetManager manager;
    private readonly Queue<NetEvent> inbox = new();
    private readonly Dictionary<int, NetPeer> peersById = new();

    public LiteNetLibClientTransport(string host, int port, string connectionKey = "khaoz")
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (connectionKey is null) throw new ArgumentNullException(nameof(connectionKey));
        manager = new NetManager(listener);
        WireListener();
        if (!manager.Start())
            throw new InvalidOperationException("Failed to start client transport.");
        manager.Connect(host, port, connectionKey);
    }

    private static NetConnectionId ToId(NetPeer peer) => new(peer.Id + 1);

    private void WireListener()
    {
        listener.PeerConnectedEvent += peer =>
        {
            peersById[peer.Id] = peer;
            inbox.Enqueue(NetEvent.Connected(ToId(peer)));
        };

        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            peersById.Remove(peer.Id);
            inbox.Enqueue(NetEvent.Disconnected(ToId(peer)));
        };

        listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
        {
            byte[] data = reader.GetRemainingBytes();
            NetChannelReliability reliability = deliveryMethod == DeliveryMethod.ReliableOrdered
                ? NetChannelReliability.ReliableOrdered
                : NetChannelReliability.UnreliableSequenced;
            inbox.Enqueue(NetEvent.FromData(ToId(peer), data, reliability));
            reader.Recycle();
        };
    }

    public void Poll() => manager.PollEvents();

    public bool TryDequeueEvent(out NetEvent ev)
    {
        if (inbox.Count > 0) { ev = inbox.Dequeue(); return true; }
        ev = default;
        return false;
    }

    public void Send(NetConnectionId target, ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (peersById.TryGetValue(target.Value - 1, out NetPeer? peer))
            peer.Send(payload.ToArray(), ChannelSplitter.ToDeliveryMethod(reliability));
    }

    public void Disconnect(NetConnectionId connection)
    {
        if (peersById.TryGetValue(connection.Value - 1, out NetPeer? peer))
            peer.Disconnect();
    }

    public void Dispose() => manager.Stop();
}
```

- [ ] **Step 5: Run the headless test (construct/dispose) and the live smoke**

Run (headless only, what CI relies on): `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LiteNetLibTransportTests&Category!=LiveSocket"`
Expected: PASS (1 test).

Run (full, including the live socket smoke): `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~LiteNetLibTransportTests"`
Expected: PASS (2 tests). If the live one is flaky on a constrained runner, it is excluded from the default CI filter above.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Netcode.LiteNetLib/LiteNetLibServerTransport.cs KhaozEngine.Netcode.LiteNetLib/LiteNetLibClientTransport.cs KhaozEngine.Tests/Netcode/LiteNetLibTransportTests.cs
git commit -m "netcode(litenetlib): add server/client INetTransport UDP bindings + smoke"
```

---

## Task 5: Scaffold the `KhaozEngine.Simulation` package

**Files:**
- Create: `KhaozEngine.Simulation/KhaozEngine.Simulation.csproj`
- Create: `KhaozEngine.Simulation/README.md`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Modify: `KhaozEngine.Server/KhaozEngine.Server.csproj`

Decision (recommended): Phase 0 `Simulation` is **zero-dependency** (a pure accumulator leaf, like `Pooling`). Ecs/Netcode references are added in later phases when the host steps a real world.

- [ ] **Step 1: Create `KhaozEngine.Simulation/KhaozEngine.Simulation.csproj`** (mirrors the `Pooling` leaf boilerplate; `<Version>` comes from the shared prop)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Simulation</PackageId>
    <Version>$(KhaozEngine5xVersion)</Version>
    <Description>Headless simulation host primitives for an authoritative server: FixedTickHost, a fixed-timestep accumulator that converts variable real-elapsed time into a whole number of fixed-dt ticks, decoupling simulation rate from frame/render rate (with a spiral-of-death backlog guard). Deterministic and dependency-free. The foundation the authoritative server loop is built on.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `KhaozEngine.Simulation/README.md`**

```markdown
# KhaozEngine.Simulation

Headless simulation-host primitives for an authoritative server.

- **`FixedTickHost`** — a fixed-timestep accumulator. Feed it variable real-elapsed time; it invokes your
  tick callback a whole number of times at a fixed `dt`, decoupling the simulation rate from the render/frame
  rate. Deterministic (the same elapsed-time sequence always yields the same tick count) and dependency-free,
  with a spiral-of-death guard that sheds backlog when ticks fall behind.

```csharp
var host = new FixedTickHost(tickSeconds: 1f / 30f);
// each frame / network pump:
host.Advance(elapsedSeconds, tick => world.Step(tick));
```

Part of the MMO netcode stack (sub-project 0B). See `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`.
```

- [ ] **Step 3: Register the project in `KhaozEngine.slnx`** — add this line next to the other foundation projects (e.g. after the `KhaozEngine.Pooling` entry):

```xml
  <Project Path="KhaozEngine.Simulation/KhaozEngine.Simulation.csproj" />
```

- [ ] **Step 4: Reference it from the test project** — in `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add to the `<ProjectReference>` `ItemGroup` (alphabetical, after the `Serialization` entry is fine):

```xml
    <ProjectReference Include="../KhaozEngine.Simulation/KhaozEngine.Simulation.csproj" />
```

- [ ] **Step 5: Pull it into the server umbrella** — in `KhaozEngine.Server/KhaozEngine.Server.csproj`, add under the `<!-- networking -->` group (or a new `<!-- simulation host -->` comment):

```xml
    <ProjectReference Include="../KhaozEngine.Simulation/KhaozEngine.Simulation.csproj" />
```

- [ ] **Step 6: Verify the solution builds with the new empty package**

Run: `dotnet build KhaozEngine.Simulation/KhaozEngine.Simulation.csproj && dotnet build KhaozEngine.Server/KhaozEngine.Server.csproj`
Expected: Build succeeded (the package has no code yet; that is fine).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Simulation/KhaozEngine.Simulation.csproj KhaozEngine.Simulation/README.md KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Server/KhaozEngine.Server.csproj
git commit -m "simulation: scaffold KhaozEngine.Simulation package (slnx + tests + server umbrella)"
```

---

## Task 6: `FixedTickHost` accumulator

**Files:**
- Create: `KhaozEngine.Simulation/FixedTickHost.cs`
- Test: `KhaozEngine.Tests/Simulation/FixedTickHostTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Simulation/FixedTickHostTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class FixedTickHostTests
{
    [Fact]
    public void Advance_OneExactTick_ProducesOneTick()
    {
        var host = new FixedTickHost(0.1f);
        var ticks = new List<long>();
        int produced = host.Advance(0.1f, ticks.Add);
        Assert.Equal(1, produced);
        Assert.Equal(new long[] { 0 }, ticks);
        Assert.Equal(1L, host.TickCount);
    }

    [Fact]
    public void Advance_AccumulatesFractionsAcrossCalls()
    {
        var host = new FixedTickHost(0.1f);
        var ticks = new List<long>();
        Assert.Equal(0, host.Advance(0.06f, ticks.Add)); // 0.06 < 0.1 -> no tick
        Assert.Equal(1, host.Advance(0.06f, ticks.Add)); // 0.12 total -> one tick, 0.02 left
        Assert.Equal(new long[] { 0 }, ticks);
    }

    [Fact]
    public void Advance_LargeElapsed_ProducesMultipleTicks_UpToCap()
    {
        var host = new FixedTickHost(0.1f);
        var ticks = new List<long>();
        int produced = host.Advance(10f, ticks.Add, maxTicksPerFrame: 4); // would be 100 ticks; capped at 4
        Assert.Equal(4, produced);
        Assert.Equal(new long[] { 0, 1, 2, 3 }, ticks);
    }

    [Fact]
    public void Advance_NegativeElapsed_IsClampedToZero()
    {
        var host = new FixedTickHost(0.1f);
        Assert.Equal(0, host.Advance(-5f, _ => { }));
        Assert.Equal(0L, host.TickCount);
    }

    [Fact]
    public void Reset_ZeroesAccumulatorAndCount()
    {
        var host = new FixedTickHost(0.1f);
        host.Advance(0.25f, _ => { }); // 2 ticks, 0.05 left over
        host.Reset();
        Assert.Equal(0L, host.TickCount);
        var ticks = new List<long>();
        Assert.Equal(0, host.Advance(0.05f, ticks.Add)); // leftover was cleared, 0.05 < 0.1
    }

    [Fact]
    public void Ctor_NonPositiveTick_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedTickHost(0f));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FixedTickHostTests"`
Expected: FAIL — `FixedTickHost` does not exist.

- [ ] **Step 3: Create `FixedTickHost.cs`**

```csharp
using System;

namespace KhaozEngine.Simulation;

/// <summary>
/// A headless fixed-timestep accumulator: converts variable real-elapsed time into a whole number of
/// fixed-dt ticks, decoupling simulation rate from frame/render rate. The authoritative server host loop is
/// built on this. Deterministic — a given sequence of elapsed-time values always yields the same tick count.
/// </summary>
/// <remarks>
/// Promotes the accumulator proven in SpaceGame's <c>FixedStepRunDriver</c>, reduced to a single tick stream
/// (the lockstep-specific input-delay / dual input-vs-sim counters are dropped: an authoritative server needs
/// only one fixed tick stream).
/// </remarks>
public sealed class FixedTickHost
{
    private readonly float tickSeconds;
    private float accumulatorSeconds;

    /// <param name="tickSeconds">Fixed timestep, seconds per tick (e.g. <c>1f / 30f</c>). Must be &gt; 0.</param>
    public FixedTickHost(float tickSeconds)
    {
        if (tickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds, "Tick duration must be > 0.");
        this.tickSeconds = tickSeconds;
    }

    /// <summary>Seconds per fixed tick.</summary>
    public float TickSeconds => tickSeconds;

    /// <summary>Total fixed ticks advanced since construction or the last <see cref="Reset"/>.</summary>
    public long TickCount { get; private set; }

    /// <summary>Clears the accumulator and the tick counter.</summary>
    public void Reset()
    {
        accumulatorSeconds = 0f;
        TickCount = 0;
    }

    /// <summary>
    /// Adds <paramref name="elapsedSeconds"/> (negative is clamped to 0) to the accumulator and invokes
    /// <paramref name="onTick"/> once per whole fixed step, at most <paramref name="maxTicksPerFrame"/> times,
    /// passing the running <see cref="TickCount"/>. When the cap is hit the accumulator is clamped to one
    /// tick's worth so the host sheds backlog instead of spiralling. Returns the number of ticks produced.
    /// </summary>
    public int Advance(float elapsedSeconds, Action<long> onTick, int maxTicksPerFrame = 8)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        int cap = Math.Max(1, maxTicksPerFrame);

        accumulatorSeconds += MathF.Max(0f, elapsedSeconds);
        int produced = 0;
        while (accumulatorSeconds >= tickSeconds && produced < cap)
        {
            accumulatorSeconds -= tickSeconds;
            onTick(TickCount);
            TickCount++;
            produced++;
        }

        if (produced >= cap)
            accumulatorSeconds = MathF.Min(accumulatorSeconds, tickSeconds);

        return produced;
    }
}
```

- [ ] **Step 4: Run to verify the tests pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FixedTickHostTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Simulation/FixedTickHost.cs KhaozEngine.Tests/Simulation/FixedTickHostTests.cs
git commit -m "simulation: add FixedTickHost fixed-timestep accumulator + tests"
```

---

## Task 7: Integration — `FixedTickHost` driving an `ITickSimulator` over a `RemoteCommandQueue`

Proves 0B + the existing Phase-0-adjacent netcode primitives compose: a fixed tick drains one command per slot and steps a game-supplied simulator deterministically. This is the shape the authoritative server loop takes in Phase 1 (with an ECS world as the state).

**Files:**
- Test: `KhaozEngine.Tests/Simulation/FixedTickHostSimulatorIntegrationTests.cs`

(No new production code — this task wires existing public types together to lock the contract. If it cannot be expressed with current public APIs, that is a finding to fold back into the spec, not a reason to add code here.)

- [ ] **Step 1: Write the integration test**

Create `KhaozEngine.Tests/Simulation/FixedTickHostSimulatorIntegrationTests.cs`:

```csharp
using KhaozEngine.Netcode;
using KhaozEngine.Simulation;
using Xunit;

namespace KhaozEngine.Tests.Simulation;

public class FixedTickHostSimulatorIntegrationTests
{
    // A trivial 1-D integrator: position advances by the command's velocity * dt each tick.
    private readonly record struct PosState(float X);
    private readonly record struct MoveCmd(float Velocity);

    private sealed class Integrator : ITickSimulator<PosState, MoveCmd>
    {
        public PosState Step(in PosState state, in MoveCmd command, float dt) =>
            new(state.X + command.Velocity * dt);
    }

    [Fact]
    public void FixedTicks_DrainCommands_AndStepSimulator_Deterministically()
    {
        const float dt = 0.1f;
        var host = new FixedTickHost(dt);
        var sim = new Integrator();
        var queue = new RemoteCommandQueue<MoveCmd>(neutralCommand: new MoveCmd(0f));

        // Two queued commands for slot 0: velocity 10 then 0.
        queue.Store(slot: 0, seq: 0, command: new MoveCmd(10f));
        queue.Store(slot: 0, seq: 1, command: new MoveCmd(0f));

        var state = new PosState(0f);
        // 0.3s elapsed -> exactly 3 ticks.
        int produced = host.Advance(0.3f, _ =>
        {
            MoveCmd cmd = queue.Dequeue(slot: 0, out _);
            state = sim.Step(state, cmd, dt);
        });

        Assert.Equal(3, produced);
        // tick0: +10*0.1=1.0 ; tick1: +0 ; tick2: neutral(0) -> 1.0
        Assert.Equal(1.0, state.X, 5);
        Assert.Equal(1, queue.GetLastAcknowledgedSeq(0)); // both real commands consumed
    }
}
```

- [ ] **Step 2: Run to verify it passes (all referenced types already exist)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~FixedTickHostSimulatorIntegrationTests"`
Expected: PASS (1 test).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/Simulation/FixedTickHostSimulatorIntegrationTests.cs
git commit -m "test(simulation): FixedTickHost drains RemoteCommandQueue and steps ITickSimulator"
```

---

## Task 8: Batch close — version bump, changelog, doc sweep, pack, tag

This is the single release step for the whole Phase 0 batch (per the one-bump-per-batch rule). Additive new package + new public API ⇒ **minor** bump: `7.34.0` → `7.35.0`.

- [ ] **Step 1: Run the full headless test suite (everything green before releasing)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Category!=LiveSocket"`
Expected: PASS (all existing tests + the new Loopback/FixedTickHost/integration/LiteNetLib-construct tests).

- [ ] **Step 2: Bump the shared version** in `Directory.Build.props`:

Change `<KhaozEngine5xVersion>7.34.0</KhaozEngine5xVersion>` to `<KhaozEngine5xVersion>7.35.0</KhaozEngine5xVersion>`.

- [ ] **Step 3: Add the newest-first `CHANGELOG.md` entry** (top of file):

```markdown
## 7.35.0

- **MMO netcode stack, Phase 0 (transport seam + fixed-tick host).**
  - `KhaozEngine.Netcode`: new `INetTransport` byte-transport seam (`Poll`/`TryDequeueEvent`/`Send`/`Disconnect`)
    with the `NetConnectionId`, `NetEvent`, `NetEventType` value types, plus `LoopbackTransport` — a
    deterministic, socket-free, thread-free in-memory transport pair for headless tests and local play.
  - `KhaozEngine.Netcode.LiteNetLib`: `LiteNetLibServerTransport` / `LiteNetLibClientTransport` implement
    `INetTransport` over reliable-UDP, reusing `ChannelSplitter.ToDeliveryMethod` for the reliability mapping.
  - New package `KhaozEngine.Simulation` (zero-dependency leaf): `FixedTickHost`, a headless fixed-timestep
    accumulator that turns variable elapsed time into a deterministic whole number of fixed-dt ticks (with a
    spiral-of-death backlog guard), decoupling simulation rate from render rate. Promoted from SpaceGame's
    `FixedStepRunDriver`, reduced to a single authoritative tick stream. Added to the `KhaozEngine.Server` umbrella.
  - All headless-tested over the loopback transport; the live UDP round-trip is a `Category=LiveSocket` smoke.
  - Design: `docs/superpowers/specs/2026-06-25-mmo-netcode-stack-design.md`; plan:
    `docs/superpowers/plans/2026-06-25-mmo-phase0-transport-and-tick-host.md`.
```

- [ ] **Step 4: Add the one-line `CHANGENOTES.md` digest** (top of file):

```markdown
- **7.35.0** — MMO netcode Phase 0: `INetTransport` seam + `LoopbackTransport` + LiteNetLib UDP bindings in
  Netcode, and a new `KhaozEngine.Simulation` package with the headless `FixedTickHost` fixed-timestep accumulator.
```

- [ ] **Step 5: Update the three guard-checked version declarations** to `7.35.0`:
  - `docs/CONSUMERS.md` "**Engine current version:** `7.35.0`"
  - `docs/ROADMAP.md` "Current released version: **7.35.0**"
  - `README.md` the four `<PackageReference ... Version="7.35.0" />` examples

- [ ] **Step 6: Full doc sweep for the new package + API** (per CLAUDE.md "full doc sweep on every change"):
  - `README.md` package-catalog table: add a `KhaozEngine.Simulation` row; extend the `KhaozEngine.Netcode` cell to mention `INetTransport`/`LoopbackTransport`; extend `KhaozEngine.Netcode.LiteNetLib` to mention the transport bindings. Add `KhaozEngine.Simulation` to the repo-layout block.
  - `README.md` / `docs/CONSUMERS.md` umbrella tables: note `KhaozEngine.Server` now pulls `KhaozEngine.Simulation`.
  - `CLAUDE.md` package map (the `<KhaozEngine5xVersion>` enumeration): add `Simulation` to the package list.
  - `docs/USING-KHAOZENGINE.md`: add a short usage section for `INetTransport` + `LoopbackTransport` + `FixedTickHost`.
  - `docs/ROADMAP.md`: mark "MMO netcode stack — Phase 0 (transport + fixed-tick host)" as shipped/started, linking the spec.
  - Mechanical check: `grep -rn "KhaozEngine.Simulation\|INetTransport\|FixedTickHost" *.md docs/*.md CLAUDE.md` and confirm every place that should mention them does.

- [ ] **Step 7: Verify the doc-version guard passes**

Run: `bash scripts/check-doc-versions.sh`
Expected: `all engine-version declarations match 7.35.0`.

- [ ] **Step 8: Pack to the local feed** (cumulative within the release)

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: packs all packages at `7.35.0`, including the new `KhaozEngine.Simulation.7.35.0.nupkg`.

- [ ] **Step 9: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md README.md CLAUDE.md docs/CONSUMERS.md docs/ROADMAP.md docs/USING-KHAOZENGINE.md
git commit -m "netcode(7.35.0): MMO Phase 0 - INetTransport seam + LoopbackTransport + FixedTickHost"
```

- [ ] **Step 10: Tag** (push of `main` + tag is the finishing step, handled per the engine release ritual / finishing menu — CI publishes to GitHub Packages on the `v*` tag)

```bash
git tag v7.35.0
```

---

## Definition of done (Phase 0)

- `INetTransport` exists with two working implementations (`LoopbackTransport`, LiteNetLib server+client), all headless-tested; the live UDP round-trip passes locally as a `LiveSocket`-traited smoke.
- `KhaozEngine.Simulation.FixedTickHost` exists, deterministic, with the spiral-of-death guard, and composes with `RemoteCommandQueue` + `ITickSimulator` (integration test green).
- `KhaozEngine.Simulation` is registered in the solution, the test project, and the `KhaozEngine.Server` umbrella.
- One version bump (`7.35.0`) with CHANGELOG + CHANGENOTES + full doc sweep; doc-version guard green; packed to `local-feed`; tagged `v7.35.0`.
- **Unblocks:** Phase 1 (`1C` replication binds to `INetTransport`; `1D` session lifecycle wraps it; the authoritative loop uses `FixedTickHost`).
