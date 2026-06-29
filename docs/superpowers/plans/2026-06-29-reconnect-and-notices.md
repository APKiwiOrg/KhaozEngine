# Mid-session reconnect + server->client notice channel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a `WorldClient` survive a server restart: detect the drop fast, auto-reconnect on the same token, re-sync cleanly, surface the disconnect reason, and receive server-pushed notices with a graceful server drain.

**Architecture:** All changes are in `KhaozEngine.NetWorld` and `KhaozEngine.Netcode`. A 1-byte `ServerFrameKind` envelope (in `MoveProtocol`) multiplexes snapshots and notices on the server->client Data channel. `WorldClient` gains a connection state machine, a snapshot-starvation detector, a disconnect-reason, and a transport-factory ctor that rebuilds the transport + `NetClient` on reconnect while keeping the prediction/replication objects. `WorldServer`/`ShardedWorldServer` gain `BroadcastNotice` + a tick-driven `BeginDrain`.

**Tech Stack:** net10.0, C#, xUnit (headless tests via `LoopbackTransport` / `InMemoryHub`), MonoGame-free.

## Global Constraints

- Target framework `net10.0`; the engine is MonoGame-free.
- **No em-dashes** in any text, comment, doc, commit message, or changelog entry. Use periods, commas, colons, parentheses, or a rewrite. Plain hyphens are fine.
- Every new behaviour ships with a **headless test** in `KhaozEngine.Tests` (no real device/socket; use `LoopbackTransport` or `InMemoryHub`).
- One shared version line: `<KhaozEngineVersion>` in `Directory.Build.props`. One bump for this whole batch at the end (Task 10), not per task.
- Conventional commit subjects `area(scope): summary`; on the release commit the scope is the new version (e.g. `netcode(8.2.0): ...`).
- Additive where possible. The one signature touch is `WorldClient.Poll()` -> `WorldClient.Poll(float dt = 0f)` (binary/source compatible for existing `Poll()` calls).
- The snapshot wire format shifts by one leading byte (the frame envelope). This is internal (server + client always ship from the same engine version); call it out in the changelog but it is not a public-API break.
- `local-feed/` must exist before `dotnet restore` (`mkdir -p local-feed`).

**Build/test commands:**
- Build NetWorld: `dotnet build KhaozEngine.NetWorld/KhaozEngine.NetWorld.csproj`
- Run a single test class: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ClassName"`
- Full netcode suite: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorld|FullyQualifiedName~Netcode"`

---

### Task 1: `ServerFrameKind` envelope in `MoveProtocol`

A 1-byte tag so the server->client Data channel can carry both snapshots and notices. Pure codec, nothing wired yet.

**Files:**
- Modify: `KhaozEngine.NetWorld/MoveProtocol.cs`
- Test: `KhaozEngine.Tests/NetWorld/ServerFrameTests.cs` (create)

**Interfaces:**
- Produces: `enum ServerFrameKind : byte { Snapshot = 0, Notice = 1 }`; `byte[] MoveProtocol.EncodeServerFrame(ServerFrameKind kind, ReadOnlySpan<byte> payload)`; `bool MoveProtocol.TryDecodeServerFrame(ReadOnlySpan<byte> data, out ServerFrameKind kind, out byte[] payload)`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/ServerFrameTests.cs`:

```csharp
using System;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerFrameTests
{
    [Fact]
    public void Snapshot_frame_round_trips_kind_and_payload()
    {
        byte[] payload = { 1, 2, 3, 4, 5 };
        byte[] framed = MoveProtocol.EncodeServerFrame(ServerFrameKind.Snapshot, payload);

        Assert.True(MoveProtocol.TryDecodeServerFrame(framed, out ServerFrameKind kind, out byte[] body));
        Assert.Equal(ServerFrameKind.Snapshot, kind);
        Assert.Equal(payload, body);
    }

    [Fact]
    public void Notice_frame_round_trips_kind()
    {
        byte[] framed = MoveProtocol.EncodeServerFrame(ServerFrameKind.Notice, Array.Empty<byte>());
        Assert.True(MoveProtocol.TryDecodeServerFrame(framed, out ServerFrameKind kind, out byte[] body));
        Assert.Equal(ServerFrameKind.Notice, kind);
        Assert.Empty(body);
    }

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.False(MoveProtocol.TryDecodeServerFrame(Array.Empty<byte>(), out _, out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerFrameTests"`
Expected: FAIL to compile (`EncodeServerFrame`/`ServerFrameKind` not defined).

- [ ] **Step 3: Add the envelope to `MoveProtocol`**

In `KhaozEngine.NetWorld/MoveProtocol.cs`, add inside the `MoveProtocol` static class (e.g. just below the `FrameHeader` / `TryDecodeSnapshotFrame` block):

```csharp
    /// <summary>The kind of server->client frame riding the Data channel: a per-client snapshot, or an
    /// out-of-band <see cref="ServerNotice"/>. The first byte of every server->client Data payload.</summary>
    public enum ServerFrameKind : byte { Snapshot = 0, Notice = 1 }

    /// <summary>Wraps a server->client payload with its 1-byte <see cref="ServerFrameKind"/> tag so snapshots and
    /// notices share the Data channel. The receiver demuxes via <see cref="TryDecodeServerFrame"/>.</summary>
    public static byte[] EncodeServerFrame(ServerFrameKind kind, ReadOnlySpan<byte> payload)
    {
        var b = new byte[1 + payload.Length];
        b[0] = (byte)kind;
        payload.CopyTo(b.AsSpan(1));
        return b;
    }

    /// <summary>Splits a server frame into its kind and inner payload. False if empty.</summary>
    public static bool TryDecodeServerFrame(ReadOnlySpan<byte> data, out ServerFrameKind kind, out byte[] payload)
    {
        if (data.Length >= 1)
        {
            kind = (ServerFrameKind)data[0];
            payload = data.Slice(1).ToArray();
            return true;
        }
        kind = ServerFrameKind.Snapshot;
        payload = Array.Empty<byte>();
        return false;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerFrameTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/MoveProtocol.cs KhaozEngine.Tests/NetWorld/ServerFrameTests.cs
git commit -m "netcode(frame): add ServerFrameKind envelope to MoveProtocol"
```

---

### Task 2: Route snapshots through the envelope (server send + client receive)

Atomic switch: both servers wrap snapshots in `EncodeServerFrame(Snapshot, ...)`; `WorldClient` unwraps and routes the snapshot to the existing path. No new behaviour. The proof is that the existing round-trip suite stays green.

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldServer.cs:220` (the snapshot send in `Tick`)
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs:266-268` (the snapshot send in `Tick`)
- Modify: `KhaozEngine.NetWorld/WorldClient.cs:103-104` (the `Data` case in `Poll`)

**Interfaces:**
- Consumes: `MoveProtocol.EncodeServerFrame` / `TryDecodeServerFrame` / `ServerFrameKind` (Task 1).

- [ ] **Step 1: Wrap the WorldServer snapshot send**

In `KhaozEngine.NetWorld/WorldServer.cs`, in `Tick`, replace the per-client send:

```csharp
            byte[] snapshot = SnapshotWriter.WriteFiltered(world, registry, set);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
```

with:

```csharp
            byte[] snapshot = SnapshotWriter.WriteFiltered(world, registry, set);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot, frame);
            net.SendTo(slot, envelope, NetChannelReliability.ReliableOrdered);
```

- [ ] **Step 2: Wrap the ShardedWorldServer snapshot send**

In `KhaozEngine.NetWorld/ShardedWorldServer.cs`, in `Tick`, replace:

```csharp
            byte[] snapshot = host.SnapshotForClient(slot, config.InterestRadius);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            net.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
```

with:

```csharp
            byte[] snapshot = host.SnapshotForClient(slot, config.InterestRadius);
            byte[] frame = MoveProtocol.EncodeSnapshotFrame(netId, lastAckBySlot[slot], snapshot);
            byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot, frame);
            net.SendTo(slot, envelope, NetChannelReliability.ReliableOrdered);
```

- [ ] **Step 3: Unwrap on the client**

In `KhaozEngine.NetWorld/WorldClient.cs`, in `Poll`, replace the `Data` case:

```csharp
                case ClientSessionEventKind.Data:
                    OnSnapshot(ev.Data);
                    break;
```

with:

```csharp
                case ClientSessionEventKind.Data:
                    OnServerFrame(ev.Data);
                    break;
```

Add this private method (just above `OnSnapshot`):

```csharp
    private void OnServerFrame(byte[] data)
    {
        if (!MoveProtocol.TryDecodeServerFrame(data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)) return;
        switch (kind)
        {
            case MoveProtocol.ServerFrameKind.Snapshot:
                OnSnapshot(payload);
                break;
            // Notice handling is wired in a later task.
        }
    }
```

- [ ] **Step 4: Run the existing round-trip suite to verify it still passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldRoundTripTests|FullyQualifiedName~ShardedWorldServerTests|FullyQualifiedName~RemoteInterpolationTests"`
Expected: PASS (all existing snapshots flow through the envelope unchanged).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.NetWorld/ShardedWorldServer.cs KhaozEngine.NetWorld/WorldClient.cs
git commit -m "netcode(frame): route snapshots through the ServerFrameKind envelope"
```

---

### Task 3: `ServerNotice` type + codec

The typed notice (with an opaque escape hatch) and its hostile-safe wire codec.

**Files:**
- Create: `KhaozEngine.NetWorld/ServerNotice.cs`
- Modify: `KhaozEngine.NetWorld/MoveProtocol.cs` (add `EncodeNotice` / `TryDecodeNotice` + caps)
- Test: `KhaozEngine.Tests/NetWorld/ServerNoticeTests.cs` (create)

**Interfaces:**
- Produces: `enum ServerNoticeKind : byte { Custom = 0, Maintenance = 1, Shutdown = 2 }`; `readonly struct ServerNotice` with ctor `ServerNotice(ServerNoticeKind kind, string message, float? secondsUntil = null, byte[]? payload = null)` and properties `Kind`, `Message`, `SecondsUntil` (`float?`), `Payload` (`byte[]`); `MoveProtocol.MaxNoticeMessageBytes` (256), `MoveProtocol.MaxNoticePayloadBytes` (512); `byte[] MoveProtocol.EncodeNotice(in ServerNotice notice)`; `ServerNotice MoveProtocol.TryDecodeNotice(ReadOnlySpan<byte> data)` returning a best-effort decode (clamps; never throws).

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/ServerNoticeTests.cs`:

```csharp
using System;
using System.Text;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerNoticeTests
{
    [Fact]
    public void Round_trips_kind_message_and_seconds()
    {
        var n = new ServerNotice(ServerNoticeKind.Maintenance, "Restarting soon", secondsUntil: 30f);
        ServerNotice back = MoveProtocol.TryDecodeNotice(MoveProtocol.EncodeNotice(n));
        Assert.Equal(ServerNoticeKind.Maintenance, back.Kind);
        Assert.Equal("Restarting soon", back.Message);
        Assert.True(back.SecondsUntil.HasValue);
        Assert.Equal(30f, back.SecondsUntil!.Value, 3);
        Assert.Empty(back.Payload);
    }

    [Fact]
    public void Round_trips_absent_seconds_and_custom_payload()
    {
        byte[] payload = { 9, 8, 7 };
        var n = new ServerNotice(ServerNoticeKind.Custom, "evt", secondsUntil: null, payload: payload);
        ServerNotice back = MoveProtocol.TryDecodeNotice(MoveProtocol.EncodeNotice(n));
        Assert.Equal(ServerNoticeKind.Custom, back.Kind);
        Assert.False(back.SecondsUntil.HasValue);
        Assert.Equal(payload, back.Payload);
    }

    [Fact]
    public void Oversize_message_is_truncated_on_the_wire()
    {
        string huge = new string('x', 5000);
        var n = new ServerNotice(ServerNoticeKind.Custom, huge);
        byte[] wire = MoveProtocol.EncodeNotice(n);
        Assert.True(wire.Length < 1000, $"oversize message not capped: {wire.Length} bytes");
        ServerNotice back = MoveProtocol.TryDecodeNotice(wire);
        Assert.True(Encoding.UTF8.GetByteCount(back.Message) <= MoveProtocol.MaxNoticeMessageBytes);
    }

    [Fact]
    public void Corrupt_short_buffer_decodes_to_a_safe_default_without_throwing()
    {
        ServerNotice back = MoveProtocol.TryDecodeNotice(Array.Empty<byte>());
        Assert.Equal(string.Empty, back.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerNoticeTests"`
Expected: FAIL to compile (`ServerNotice` not defined).

- [ ] **Step 3: Create the `ServerNotice` type**

Create `KhaozEngine.NetWorld/ServerNotice.cs`:

```csharp
using System;

namespace KhaozEngine.NetWorld;

/// <summary>The kind of out-of-band notice a server pushes to connected clients. <see cref="Shutdown"/> also lets a
/// client attribute a following drop to <c>DisconnectReason.ServerShutdown</c> (a planned restart, not a crash).</summary>
public enum ServerNoticeKind : byte { Custom = 0, Maintenance = 1, Shutdown = 2 }

/// <summary>
/// A small typed message broadcast server->client and surfaced on <see cref="WorldClient"/> (event + latest
/// property) for the consumer to display, e.g. a maintenance/restart warning. Common cases are first-class
/// (<see cref="Kind"/> + <see cref="Message"/> + an optional <see cref="SecondsUntil"/> countdown); a
/// <see cref="ServerNoticeKind.Custom"/> notice may also carry an opaque <see cref="Payload"/> the game decodes.
/// </summary>
public readonly struct ServerNotice
{
    public ServerNotice(ServerNoticeKind kind, string message, float? secondsUntil = null, byte[]? payload = null)
    {
        Kind = kind;
        Message = message ?? string.Empty;
        SecondsUntil = secondsUntil;
        Payload = payload ?? Array.Empty<byte>();
    }

    /// <summary>What the notice is about.</summary>
    public ServerNoticeKind Kind { get; }

    /// <summary>Human-readable text (capped at <see cref="MoveProtocol.MaxNoticeMessageBytes"/> on the wire).</summary>
    public string Message { get; }

    /// <summary>Optional countdown in seconds (e.g. "restarting in N s"); null when not applicable.</summary>
    public float? SecondsUntil { get; }

    /// <summary>Opaque game-defined bytes for a <see cref="ServerNoticeKind.Custom"/> notice (capped at
    /// <see cref="MoveProtocol.MaxNoticePayloadBytes"/>); empty for the typed kinds.</summary>
    public byte[] Payload { get; }
}
```

- [ ] **Step 4: Add the codec to `MoveProtocol`**

In `KhaozEngine.NetWorld/MoveProtocol.cs`, add inside the class (the `using System.Text;` import is already present):

```csharp
    /// <summary>Upper bound on a <see cref="ServerNotice.Message"/>'s UTF-8 encoding, in bytes. Truncated on write
    /// (at a char boundary) and clamped on read, so a corrupt length can neither over-allocate nor desync.</summary>
    public const int MaxNoticeMessageBytes = 256;

    /// <summary>Upper bound on a <see cref="ServerNotice.Payload"/>, in bytes (same hostile-safe contract).</summary>
    public const int MaxNoticePayloadBytes = 512;

    // Notice: [kind:byte][flags:byte][secondsUntil:float?][msgLen:ushort][msg utf8][payloadLen:ushort][payload].
    // flags bit0 = secondsUntil present. Lengths are capped on write and clamped on read.
    private const byte NoticeFlagHasSeconds = 0x01;

    /// <summary>Encodes a <see cref="ServerNotice"/>. Message + payload are capped at their byte limits.</summary>
    public static byte[] EncodeNotice(in ServerNotice notice)
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write((byte)notice.Kind);
        byte flags = notice.SecondsUntil.HasValue ? NoticeFlagHasSeconds : (byte)0;
        bw.Write(flags);
        if (notice.SecondsUntil.HasValue) bw.Write(notice.SecondsUntil.Value);
        WriteCapped(bw, Encoding.UTF8.GetBytes(notice.Message ?? string.Empty), MaxNoticeMessageBytes, utf8Boundary: true);
        WriteCapped(bw, notice.Payload ?? Array.Empty<byte>(), MaxNoticePayloadBytes, utf8Boundary: false);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Best-effort decode of a notice frame. Never throws: a short/corrupt buffer yields a safe default
    /// (Custom, empty message, no seconds, empty payload), and declared lengths are clamped before allocating.</summary>
    public static ServerNotice TryDecodeNotice(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return new ServerNotice(ServerNoticeKind.Custom, string.Empty);
        int i = 0;
        var kind = (ServerNoticeKind)data[i++];
        byte flags = data[i++];
        float? seconds = null;
        if ((flags & NoticeFlagHasSeconds) != 0)
        {
            if (data.Length < i + 4) return new ServerNotice(kind, string.Empty);
            seconds = BitConverter.ToSingle(data.Slice(i, 4));
            i += 4;
            if (!float.IsFinite(seconds.Value)) seconds = null;   // hostile-safe: drop a NaN/Inf countdown
        }
        string message = ReadCapped(data, ref i, MaxNoticeMessageBytes, out byte[] msgBytes)
            ? Encoding.UTF8.GetString(msgBytes) : string.Empty;
        byte[] payload = ReadCapped(data, ref i, MaxNoticePayloadBytes, out byte[] payloadBytes) ? payloadBytes : Array.Empty<byte>();
        return new ServerNotice(kind, message, seconds, payload);
    }

    private static void WriteCapped(System.IO.BinaryWriter bw, byte[] bytes, int cap, bool utf8Boundary)
    {
        int len = Math.Min(bytes.Length, cap);
        if (utf8Boundary)
            while (len > 0 && len < bytes.Length && (bytes[len] & 0xC0) == 0x80) len--;  // do not split a codepoint
        bw.Write((ushort)len);
        bw.Write(bytes, 0, len);
    }

    private static bool ReadCapped(ReadOnlySpan<byte> data, ref int i, int cap, out byte[] bytes)
    {
        if (data.Length < i + 2) { bytes = Array.Empty<byte>(); return false; }
        int declared = BitConverter.ToUInt16(data.Slice(i, 2));
        i += 2;
        int take = Math.Min(Math.Min(declared, cap), Math.Max(0, data.Length - i));
        bytes = data.Slice(i, take).ToArray();
        i += declared;   // advance by the declared length so a later field stays frame-aligned (clamped read above)
        return true;
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerNoticeTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld/ServerNotice.cs KhaozEngine.NetWorld/MoveProtocol.cs KhaozEngine.Tests/NetWorld/ServerNoticeTests.cs
git commit -m "netcode(notice): add ServerNotice type + hostile-safe codec"
```

---

### Task 4: `WorldServer.BroadcastNotice` + client notice surface

The server pushes a notice; the client raises it and remembers the latest.

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldServer.cs` (add `BroadcastNotice`)
- Modify: `KhaozEngine.NetWorld/WorldClient.cs` (handle the `Notice` frame kind, add `NoticeReceived` + `LastNotice`)
- Test: `KhaozEngine.Tests/NetWorld/ServerNoticeDeliveryTests.cs` (create)

**Interfaces:**
- Consumes: `ServerNotice`, `MoveProtocol.EncodeNotice` / `TryDecodeNotice`, `MoveProtocol.EncodeServerFrame` / `ServerFrameKind`.
- Produces: `void WorldServer.BroadcastNotice(in ServerNotice notice)`; `event Action<ServerNotice>? WorldClient.NoticeReceived`; `ServerNotice? WorldClient.LastNotice { get; }`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/ServerNoticeDeliveryTests.cs`:

```csharp
using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerNoticeDeliveryTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Broadcast_notice_reaches_a_connected_client()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        ServerNotice? received = null;
        client.NoticeReceived += n => received = n;

        server.BroadcastNotice(new ServerNotice(ServerNoticeKind.Maintenance, "Restarting in 30s", 30f));
        for (int i = 0; i < 3; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.True(received.HasValue, "client never received the notice");
        Assert.Equal(ServerNoticeKind.Maintenance, received!.Value.Kind);
        Assert.Equal("Restarting in 30s", received.Value.Message);
        Assert.Equal(30f, received.Value.SecondsUntil!.Value, 3);
        Assert.True(client.LastNotice.HasValue);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerNoticeDeliveryTests"`
Expected: FAIL to compile (`BroadcastNotice` / `NoticeReceived` not defined).

- [ ] **Step 3: Add `BroadcastNotice` to `WorldServer`**

In `KhaozEngine.NetWorld/WorldServer.cs`, add (e.g. just below `Disconnect`):

```csharp
    /// <summary>Broadcasts a <see cref="ServerNotice"/> to every connected client (reliable-ordered), surfaced on
    /// <see cref="WorldClient.NoticeReceived"/>. Out-of-band: rides the Data channel alongside snapshots via the
    /// frame envelope, so it never disturbs the movement stream.</summary>
    public void BroadcastNotice(in ServerNotice notice)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, MoveProtocol.EncodeNotice(notice));
        net.Broadcast(envelope, NetChannelReliability.ReliableOrdered);
    }
```

- [ ] **Step 4: Surface the notice on `WorldClient`**

In `KhaozEngine.NetWorld/WorldClient.cs`, add the public surface near `LocalRenderState`:

```csharp
    /// <summary>Raised when the server pushes a <see cref="ServerNotice"/> (e.g. a maintenance/restart warning).</summary>
    public event Action<ServerNotice>? NoticeReceived;

    /// <summary>The most recent <see cref="ServerNotice"/> received, or null if none. Lets a consumer that attaches
    /// late, or polls instead of subscribing, still read the latest notice.</summary>
    public ServerNotice? LastNotice { get; private set; }
```

Extend `OnServerFrame` (from Task 2) to handle the `Notice` case:

```csharp
    private void OnServerFrame(byte[] data)
    {
        if (!MoveProtocol.TryDecodeServerFrame(data, out MoveProtocol.ServerFrameKind kind, out byte[] payload)) return;
        switch (kind)
        {
            case MoveProtocol.ServerFrameKind.Snapshot:
                OnSnapshot(payload);
                break;
            case MoveProtocol.ServerFrameKind.Notice:
                ServerNotice notice = MoveProtocol.TryDecodeNotice(payload);
                LastNotice = notice;
                NoticeReceived?.Invoke(notice);
                break;
        }
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerNoticeDeliveryTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.NetWorld/WorldClient.cs KhaozEngine.Tests/NetWorld/ServerNoticeDeliveryTests.cs
git commit -m "netcode(notice): WorldServer.BroadcastNotice + WorldClient notice surface"
```

---

### Task 5: `WorldServer.BeginDrain` + tick-driven countdown

A graceful drain primitive: broadcast the notice, run a grace countdown over `Tick`, expose completion. The countdown lives in a reusable `DrainController` so `ShardedWorldServer` reuses it in Task 6.

**Files:**
- Create: `KhaozEngine.NetWorld/DrainController.cs`
- Modify: `KhaozEngine.NetWorld/WorldServer.cs` (`BeginDrain`, `IsDraining`, `IsDrainComplete`, advance in `Tick`)
- Test: `KhaozEngine.Tests/NetWorld/ServerDrainTests.cs` (create)

**Interfaces:**
- Produces: `sealed class DrainController` with `void Begin(float graceSeconds)`, `void Advance(float dt)`, `bool IsDraining`, `bool IsComplete`; `void WorldServer.BeginDrain(in ServerNotice notice, float graceSeconds)`, `bool WorldServer.IsDraining`, `bool WorldServer.IsDrainComplete`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/ServerDrainTests.cs`:

```csharp
using System;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ServerDrainTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Begin_drain_broadcasts_the_notice_then_completes_after_the_grace()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        server.BeginDrain(new ServerNotice(ServerNoticeKind.Shutdown, "Restarting", 1f), graceSeconds: 1f);
        Assert.True(server.IsDraining);
        Assert.False(server.IsDrainComplete);

        // Pump the grace. The notice is delivered early; completion flips only after the grace elapses.
        bool sawNotice = false;
        for (int i = 0; i < 40; i++)   // 40 * (1/30)s ~= 1.33s > 1s grace
        {
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            if (client.LastNotice.HasValue) sawNotice = true;
        }
        Assert.True(sawNotice, "drain did not broadcast the notice");
        Assert.True(server.IsDrainComplete, "drain never completed after the grace period");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerDrainTests"`
Expected: FAIL to compile (`BeginDrain` not defined).

- [ ] **Step 3: Create `DrainController`**

Create `KhaozEngine.NetWorld/DrainController.cs`:

```csharp
namespace KhaozEngine.NetWorld;

/// <summary>A deterministic, tick-driven grace countdown shared by <see cref="WorldServer"/> and
/// <see cref="ShardedWorldServer"/> for a graceful drain. No wall clock: the host advances it by dt each tick.</summary>
public sealed class DrainController
{
    private float remaining;

    /// <summary>True between <see cref="Begin"/> and the grace elapsing.</summary>
    public bool IsDraining { get; private set; }

    /// <summary>True once the grace period has elapsed (the host should then flush + close).</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Starts the grace countdown. A non-positive grace completes on the next <see cref="Advance"/>.</summary>
    public void Begin(float graceSeconds)
    {
        remaining = graceSeconds;
        IsDraining = true;
        IsComplete = false;
    }

    /// <summary>Advances the countdown by dt; flips <see cref="IsComplete"/> when the grace elapses.</summary>
    public void Advance(float dt)
    {
        if (!IsDraining || IsComplete) return;
        remaining -= dt;
        if (remaining <= 0f) { IsComplete = true; IsDraining = false; }
    }
}
```

- [ ] **Step 4: Wire it into `WorldServer`**

In `KhaozEngine.NetWorld/WorldServer.cs`, add a field near the other readonly fields:

```csharp
    private readonly DrainController drain = new();
```

Add the public surface (near `BroadcastNotice`):

```csharp
    /// <summary>True while a graceful drain is in progress (see <see cref="BeginDrain"/>).</summary>
    public bool IsDraining => drain.IsDraining;

    /// <summary>True once a graceful drain's grace period has elapsed. The host then flushes persistence
    /// (<c>WorldPersistence.FlushAsync</c>) and disposes the transport to close the sockets.</summary>
    public bool IsDrainComplete => drain.IsComplete;

    /// <summary>Begins a graceful drain: broadcasts <paramref name="notice"/> now (warn players), then runs a
    /// <paramref name="graceSeconds"/> countdown over <see cref="Tick"/> while still serving snapshots, so clients
    /// see the warning. When <see cref="IsDrainComplete"/> flips, the host should flush persistence and close.</summary>
    public void BeginDrain(in ServerNotice notice, float graceSeconds)
    {
        BroadcastNotice(notice);
        drain.Begin(graceSeconds);
    }
```

Advance the drain at the end of `Tick` (after the `AdvanceWorldTick` block):

```csharp
        if (config.AdvanceWorldTick) world.AdvanceTick();
        drain.Advance(dt);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ServerDrainTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld/DrainController.cs KhaozEngine.NetWorld/WorldServer.cs KhaozEngine.Tests/NetWorld/ServerDrainTests.cs
git commit -m "netcode(drain): WorldServer.BeginDrain tick-driven graceful drain"
```

---

### Task 6: `ShardedWorldServer` notice + drain parity

Mirror `BroadcastNotice` + `BeginDrain` on the multi-cell server, reusing `DrainController`.

**Files:**
- Modify: `KhaozEngine.NetWorld/ShardedWorldServer.cs`
- Test: `KhaozEngine.Tests/NetWorld/ShardedNoticeDrainTests.cs` (create)

**Interfaces:**
- Consumes: `DrainController` (Task 5), `ServerNotice`, `MoveProtocol.EncodeServerFrame` / `EncodeNotice`.
- Produces: `void ShardedWorldServer.BroadcastNotice(in ServerNotice)`, `void ShardedWorldServer.BeginDrain(in ServerNotice, float)`, `bool ShardedWorldServer.IsDraining`, `bool ShardedWorldServer.IsDrainComplete`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/ShardedNoticeDrainTests.cs`:

```csharp
using System;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedNoticeDrainTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Sharded_broadcast_reaches_a_client_and_drain_completes()
    {
        var hub = new InMemoryHub();
        var config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 24f, OverlapMargin = 24f, MaxPlayers = 16 };
        var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);

        server.BeginDrain(new ServerNotice(ServerNoticeKind.Shutdown, "Restarting", 1f), graceSeconds: 1f);
        bool sawNotice = false;
        for (int i = 0; i < 40; i++)
        {
            server.Poll(); server.Tick(config.TickSeconds); client.Poll();
            if (client.LastNotice.HasValue) sawNotice = true;
        }
        Assert.True(sawNotice);
        Assert.True(server.IsDrainComplete);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ShardedNoticeDrainTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Add the parity surface to `ShardedWorldServer`**

In `KhaozEngine.NetWorld/ShardedWorldServer.cs`, add a field with the others:

```csharp
    private readonly DrainController drain = new();
```

Add the methods (e.g. below `Disconnect`):

```csharp
    /// <summary>Broadcasts a <see cref="ServerNotice"/> to every connected client (reliable-ordered). Same contract
    /// as <see cref="WorldServer.BroadcastNotice"/>.</summary>
    public void BroadcastNotice(in ServerNotice notice)
    {
        byte[] envelope = MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Notice, MoveProtocol.EncodeNotice(notice));
        net.Broadcast(envelope, NetChannelReliability.ReliableOrdered);
    }

    /// <summary>True while a graceful drain is in progress.</summary>
    public bool IsDraining => drain.IsDraining;

    /// <summary>True once a graceful drain's grace has elapsed (host then flushes persistence + closes).</summary>
    public bool IsDrainComplete => drain.IsComplete;

    /// <summary>Begins a graceful drain (broadcast + grace countdown). Same contract as
    /// <see cref="WorldServer.BeginDrain"/>.</summary>
    public void BeginDrain(in ServerNotice notice, float graceSeconds)
    {
        BroadcastNotice(notice);
        drain.Begin(graceSeconds);
    }
```

Advance the drain at the end of `Tick` (after the `AdvanceWorldTick` cell loop):

```csharp
        if (config.AdvanceWorldTick)
            foreach (CellSim cell in host.Cells) cell.World.AdvanceTick();
        drain.Advance(dt);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ShardedNoticeDrainTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.NetWorld/ShardedWorldServer.cs KhaozEngine.Tests/NetWorld/ShardedNoticeDrainTests.cs
git commit -m "netcode(notice): ShardedWorldServer notice + drain parity"
```

---

### Task 7: Connection state machine + reason (instance-ctor path)

Introduce the observable state machine and disconnect reason on the existing single-transport path. No reconnect yet: a drop is terminal, but now observable, and the swallowed `Rejected` event is handled.

**Files:**
- Create: `KhaozEngine.NetWorld/WorldConnectionState.cs`
- Create: `KhaozEngine.NetWorld/DisconnectReason.cs`
- Modify: `KhaozEngine.NetWorld/WorldClient.cs`
- Test: `KhaozEngine.Tests/NetWorld/WorldClientConnectionStateTests.cs` (create)

**Interfaces:**
- Produces: `enum WorldConnectionState { Connecting, Connected, Reconnecting, Disconnected }`; `enum DisconnectReason { None, RejectedToken, Unreachable, ServerShutdown, Timeout }`; on `WorldClient`: `WorldConnectionState ConnectionState { get; }`, `event Action<WorldConnectionState>? ConnectionStateChanged`, `DisconnectReason DisconnectReason { get; }`, `string DisconnectReasonDetail { get; }`. `Joined` is redefined as `ConnectionState == Connected`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/WorldClientConnectionStateTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldClientConnectionStateTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void Connects_through_Connecting_to_Connected()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds });

        Assert.Equal(WorldConnectionState.Connecting, client.ConnectionState);
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.True(client.Joined);
        Assert.Equal(DisconnectReason.None, client.DisconnectReason);
    }

    [Fact]
    public void Transport_drop_while_connected_is_Disconnected_Unreachable()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        INetTransport ct = hub.CreateClient();
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        hub.DisconnectClient(ct);
        for (int i = 0; i < 3; i++) { client.Poll(); }
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.Unreachable, client.DisconnectReason);
    }

    [Fact]
    public void Rejected_token_is_surfaced_as_RejectedToken_with_detail()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        // An authenticator that rejects every token with a known reason.
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default,
            authenticator: new RejectingAuthenticator("bad token"));
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds }, token: new byte[] { 1 });

        var states = new List<WorldConnectionState>();
        client.ConnectionStateChanged += s => states.Add(s);

        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.RejectedToken, client.DisconnectReason);
        Assert.Equal("bad token", client.DisconnectReasonDetail);
        Assert.Contains(WorldConnectionState.Disconnected, states);
    }

    private sealed class RejectingAuthenticator : IConnectionAuthenticator
    {
        private readonly string reason;
        public RejectingAuthenticator(string reason) => this.reason = reason;
        public bool TryAuthenticate(byte[] token, out string subject, out string failureReason)
        {
            subject = string.Empty;
            failureReason = reason;
            return false;
        }
    }
}
```

Note: confirm the `IConnectionAuthenticator` method name/signature by opening `KhaozEngine.Netcode/IConnectionAuthenticator.cs` before writing the stub; mirror it exactly (the server calls `authenticator.TryAuthenticate(token, out string subject, out string reason)`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldClientConnectionStateTests"`
Expected: FAIL to compile (`ConnectionState` / `WorldConnectionState` not defined).

- [ ] **Step 3: Create the enums**

Create `KhaozEngine.NetWorld/WorldConnectionState.cs`:

```csharp
namespace KhaozEngine.NetWorld;

/// <summary>The live connection state of a <see cref="WorldClient"/>.</summary>
public enum WorldConnectionState
{
    /// <summary>Initial connect in flight; no first join yet.</summary>
    Connecting,
    /// <summary>Joined and receiving snapshots (healthy).</summary>
    Connected,
    /// <summary>Was connected, lost it, now retrying (auto-reconnect).</summary>
    Reconnecting,
    /// <summary>Terminal: gave up, was rejected, or was closed.</summary>
    Disconnected,
}
```

Create `KhaozEngine.NetWorld/DisconnectReason.cs`:

```csharp
namespace KhaozEngine.NetWorld;

/// <summary>Why a <see cref="WorldClient"/> lost (or could not establish) its session.</summary>
public enum DisconnectReason
{
    /// <summary>Healthy / never disconnected.</summary>
    None,
    /// <summary>The server rejected the connect token (bad/expired). <see cref="WorldClient.DisconnectReasonDetail"/>
    /// carries the authenticator's reason string. Not retried by default.</summary>
    RejectedToken,
    /// <summary>The transport dropped with no prior shutdown notice (crash / network loss / unreachable).</summary>
    Unreachable,
    /// <summary>The transport dropped after a <see cref="ServerNoticeKind.Shutdown"/> notice (a planned restart).</summary>
    ServerShutdown,
    /// <summary>No snapshot arrived within the configured timeout while the transport was still nominally up.</summary>
    Timeout,
}
```

- [ ] **Step 4: Wire the state machine into `WorldClient` (instance path)**

In `KhaozEngine.NetWorld/WorldClient.cs`:

(a) Add fields near the top of the class:

```csharp
    private WorldConnectionState state = WorldConnectionState.Connecting;
    private DisconnectReason disconnectReason = DisconnectReason.None;
    private string disconnectReasonDetail = string.Empty;
```

(b) Replace the existing `Joined` property:

```csharp
    /// <summary>True once the session handshake has joined.</summary>
    public bool Joined { get; private set; }
```

with:

```csharp
    /// <summary>True once the session handshake has joined (equivalent to
    /// <see cref="ConnectionState"/> == <see cref="WorldConnectionState.Connected"/>).</summary>
    public bool Joined => state == WorldConnectionState.Connected;

    /// <summary>The live connection state. Observe transitions via <see cref="ConnectionStateChanged"/>.</summary>
    public WorldConnectionState ConnectionState => state;

    /// <summary>Raised on every <see cref="ConnectionState"/> transition (new state passed).</summary>
    public event Action<WorldConnectionState>? ConnectionStateChanged;

    /// <summary>Why the session was lost (or could not be established); <see cref="DisconnectReason.None"/> while healthy.</summary>
    public DisconnectReason DisconnectReason => disconnectReason;

    /// <summary>Extra detail for the reason (the authenticator's reject string for
    /// <see cref="DisconnectReason.RejectedToken"/>); empty otherwise.</summary>
    public string DisconnectReasonDetail => disconnectReasonDetail;

    private void SetState(WorldConnectionState next)
    {
        if (state == next) return;
        state = next;
        ConnectionStateChanged?.Invoke(next);
    }
```

(c) Replace the `Poll` event switch to drive the state machine:

```csharp
    public void Poll()
    {
        net.Poll();
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    disconnectReason = DisconnectReason.None;
                    disconnectReasonDetail = string.Empty;
                    SetState(WorldConnectionState.Connected);
                    break;
                case ClientSessionEventKind.Data:
                    OnServerFrame(ev.Data);
                    break;
                case ClientSessionEventKind.Rejected:
                    disconnectReason = DisconnectReason.RejectedToken;
                    disconnectReasonDetail = ev.RejectReason;
                    SetState(WorldConnectionState.Disconnected);
                    break;
                case ClientSessionEventKind.Disconnected:
                    if (state != WorldConnectionState.Disconnected)
                    {
                        disconnectReason = DisconnectReason.Unreachable;
                        SetState(WorldConnectionState.Disconnected);
                    }
                    break;
            }
        }
    }
```

(Delete the old `Joined = true;` / `Joined = false;` assignments; `Joined` is now derived.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldClientConnectionStateTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the existing round-trip suite (regression on the derived `Joined`)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldRoundTripTests|FullyQualifiedName~WorldClientLocalMovementTests"`
Expected: PASS (existing `client.Joined` assertions still hold).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.NetWorld/WorldConnectionState.cs KhaozEngine.NetWorld/DisconnectReason.cs KhaozEngine.NetWorld/WorldClient.cs KhaozEngine.Tests/NetWorld/WorldClientConnectionStateTests.cs
git commit -m "netcode(client): observable connection state + disconnect reason"
```

---

### Task 8: Snapshot-starvation timeout + shutdown-reason attribution

Add `Poll(float dt = 0f)`, the no-snapshots-for-N-seconds detector (`Timeout`), and `ServerShutdown` attribution (a drop after a shutdown notice). Still terminal (no reconnect).

**Files:**
- Modify: `KhaozEngine.NetWorld/WorldClient.cs`
- Modify: `KhaozEngine.NetWorld/WorldClient.cs` (the `WorldClientConfig` class at the top of the file)
- Test: `KhaozEngine.Tests/NetWorld/WorldClientTimeoutTests.cs` (create)

**Interfaces:**
- Produces: `WorldClient.Poll(float dt = 0f)`; `WorldClientConfig.DisconnectTimeoutSeconds` (`float`, default 3f). The starvation timer advances only with `dt > 0`.

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/NetWorld/WorldClientTimeoutTests.cs`:

```csharp
using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldClientTimeoutTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    [Fact]
    public void No_snapshots_for_the_timeout_window_is_Disconnected_Timeout()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, DisconnectTimeoutSeconds = 1f });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        // Stop ticking the server (no more snapshots) and advance the client's clock past the timeout.
        for (int i = 0; i < 40; i++) client.Poll(0.05f);   // 2.0s of dt > 1s timeout
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.Timeout, client.DisconnectReason);
    }

    [Fact]
    public void Drop_after_a_shutdown_notice_is_attributed_ServerShutdown()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        INetTransport ct = hub.CreateClient();
        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = config.TickSeconds, DisconnectTimeoutSeconds = 5f });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }

        server.BroadcastNotice(new ServerNotice(ServerNoticeKind.Shutdown, "Restarting", 1f));
        for (int i = 0; i < 3; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.LastNotice.HasValue);

        hub.DisconnectClient(ct);
        for (int i = 0; i < 3; i++) client.Poll();
        Assert.Equal(WorldConnectionState.Disconnected, client.ConnectionState);
        Assert.Equal(DisconnectReason.ServerShutdown, client.DisconnectReason);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldClientTimeoutTests"`
Expected: FAIL to compile (`Poll(float)` / `DisconnectTimeoutSeconds` not defined).

- [ ] **Step 3: Add the config field**

In `KhaozEngine.NetWorld/WorldClient.cs`, in `WorldClientConfig`, add:

```csharp
    /// <summary>Mid-session disconnect detector: declare the session lost after this many seconds with no server
    /// snapshot (only advances when <see cref="WorldClient.Poll(float)"/> is called with dt &gt; 0). Default 3s.</summary>
    public float DisconnectTimeoutSeconds { get; init; } = 3f;
```

- [ ] **Step 4: Add `Poll(float dt)`, the starvation timer, and shutdown attribution**

In `KhaozEngine.NetWorld/WorldClient.cs`:

(a) Add fields:

```csharp
    private readonly float disconnectTimeout;
    private float secondsSinceServerFrame;
    private bool sawShutdownNotice;
```

(b) In the ctor, capture the timeout (near `tickSeconds = config.TickSeconds;`):

```csharp
        disconnectTimeout = config.DisconnectTimeoutSeconds;
```

(c) Change `Poll()` to `Poll(float dt = 0f)` and reset/advance the starvation timer. Add a `gotFrame` flag set when a `Data` event arrives, and after the drain loop run the timer:

```csharp
    public void Poll(float dt = 0f)
    {
        net.Poll();
        bool gotFrame = false;
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    disconnectReason = DisconnectReason.None;
                    disconnectReasonDetail = string.Empty;
                    secondsSinceServerFrame = 0f;
                    SetState(WorldConnectionState.Connected);
                    break;
                case ClientSessionEventKind.Data:
                    gotFrame = true;
                    OnServerFrame(ev.Data);
                    break;
                case ClientSessionEventKind.Rejected:
                    disconnectReason = DisconnectReason.RejectedToken;
                    disconnectReasonDetail = ev.RejectReason;
                    SetState(WorldConnectionState.Disconnected);
                    break;
                case ClientSessionEventKind.Disconnected:
                    if (state != WorldConnectionState.Disconnected)
                    {
                        disconnectReason = sawShutdownNotice ? DisconnectReason.ServerShutdown : DisconnectReason.Unreachable;
                        SetState(WorldConnectionState.Disconnected);
                    }
                    break;
            }
        }
        if (gotFrame) secondsSinceServerFrame = 0f;

        if (state == WorldConnectionState.Connected && dt > 0f)
        {
            secondsSinceServerFrame += dt;
            if (secondsSinceServerFrame >= disconnectTimeout)
            {
                disconnectReason = DisconnectReason.Timeout;
                SetState(WorldConnectionState.Disconnected);
            }
        }
    }
```

(d) In `OnServerFrame`, set `sawShutdownNotice` when a shutdown notice arrives:

```csharp
            case MoveProtocol.ServerFrameKind.Notice:
                ServerNotice notice = MoveProtocol.TryDecodeNotice(payload);
                if (notice.Kind == ServerNoticeKind.Shutdown) sawShutdownNotice = true;
                LastNotice = notice;
                NoticeReceived?.Invoke(notice);
                break;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldClientTimeoutTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.NetWorld/WorldClient.cs KhaozEngine.Tests/NetWorld/WorldClientTimeoutTests.cs
git commit -m "netcode(client): snapshot-starvation timeout + ServerShutdown attribution"
```

---

### Task 9: Transport-factory ctor, auto-reconnect with backoff, IDisposable

The headline behaviour: a factory ctor that rebuilds the transport + `NetClient` on reconnect, resuming the same token, keeping the prediction object, and rebuilding the replication view so the avatar re-syncs cleanly.

**Files:**
- Create: `KhaozEngine.NetWorld/ReconnectBackoff.cs`
- Modify: `KhaozEngine.NetWorld/WorldClient.cs` (factory ctor, `IDisposable`, reconnect machine, config fields)
- Create: `KhaozEngine.Tests/NetWorld/RestartableHub.cs` (test helper)
- Test: `KhaozEngine.Tests/NetWorld/WorldClientReconnectTests.cs` (create)

**Interfaces:**
- Consumes: `WorldConnectionState`, `DisconnectReason`, the starvation timer + state machine (Tasks 7-8).
- Produces: `sealed class ReconnectBackoff { float InitialSeconds; float Multiplier; float MaxSeconds; int MaxAttempts; static ReconnectBackoff Default; }`; new ctor `WorldClient(Func<INetTransport> connect, Func<float,float,float> groundHeight, MoveTuning tuning, WorldClientConfig? config = null, byte[]? token = null, Func<float,float,Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null)`; `WorldClient : IDisposable`; `int WorldClient.ReconnectAttempt { get; }`; `float WorldClient.SecondsUntilNextRetry { get; }`; `WorldClientConfig.AutoReconnect` (bool, default true), `WorldClientConfig.RetryOnReject` (bool, default false), `WorldClientConfig.Reconnect` (`ReconnectBackoff`, default `ReconnectBackoff.Default`).

- [ ] **Step 1: Create `ReconnectBackoff`**

Create `KhaozEngine.NetWorld/ReconnectBackoff.cs`:

```csharp
using System;

namespace KhaozEngine.NetWorld;

/// <summary>Exponential backoff schedule for <see cref="WorldClient"/> auto-reconnect.</summary>
public sealed class ReconnectBackoff
{
    /// <summary>Delay before the first reconnect attempt, seconds.</summary>
    public float InitialSeconds { get; init; } = 0.5f;
    /// <summary>Per-attempt multiplier on the delay.</summary>
    public float Multiplier { get; init; } = 2f;
    /// <summary>Ceiling on the per-attempt delay, seconds.</summary>
    public float MaxSeconds { get; init; } = 5f;
    /// <summary>Maximum reconnect attempts before giving up (0 = unlimited).</summary>
    public int MaxAttempts { get; init; } = 0;

    public static ReconnectBackoff Default => new();

    /// <summary>The delay before attempt number <paramref name="attempt"/> (1-based), clamped to <see cref="MaxSeconds"/>.</summary>
    public float DelayForAttempt(int attempt)
    {
        double d = InitialSeconds * Math.Pow(Multiplier, Math.Max(0, attempt - 1));
        return (float)Math.Min(d, MaxSeconds);
    }
}
```

- [ ] **Step 2: Create the restartable test hub**

Create `KhaozEngine.Tests/NetWorld/RestartableHub.cs`. This wraps a swappable `InMemoryHub` so a `WorldClient`'s `connect` factory attaches to whatever server is current, and a "restart" rebuilds the hub:

```csharp
using KhaozEngine.Netcode;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A test harness for reconnect: holds the "current" <see cref="InMemoryHub"/>, hands a <see cref="WorldClient"/>
/// factory (<see cref="Connect"/>) that attaches a fresh client endpoint to whichever hub is current, and
/// <see cref="Restart"/> swaps in a brand-new hub (modelling a server process restart). The caller builds the
/// server over <see cref="ServerTransport"/> after each (re)start.
/// </summary>
public sealed class RestartableHub
{
    public InMemoryHub Current { get; private set; } = new();

    /// <summary>The current hub's server transport (hand to a fresh WorldServer/ShardedWorldServer).</summary>
    public INetTransport ServerTransport => Current.Server;

    /// <summary>A WorldClient transport factory: each call creates a client endpoint on the current hub.</summary>
    public System.Func<INetTransport> Connect => () => Current.CreateClient();

    /// <summary>Models a server restart: swaps in a new hub. The old endpoints stop receiving; the next
    /// <see cref="Connect"/> call (a reconnect attempt) attaches to the new hub.</summary>
    public void Restart() => Current = new InMemoryHub();
}
```

- [ ] **Step 3: Write the failing reconnect test**

Create `KhaozEngine.Tests/NetWorld/WorldClientReconnectTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldClientReconnectTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static WorldServer NewServer(KhaozEngine.Netcode.INetTransport t, WorldServerConfig config) =>
        new(t, config, Flat, MoveTuning.Default);

    [Fact]
    public void Reconnects_through_Reconnecting_back_to_Connected_after_a_restart()
    {
        var rh = new RestartableHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = NewServer(rh.ServerTransport, config);

        using var client = new WorldClient(rh.Connect, Flat, MoveTuning.Default,
            new WorldClientConfig
            {
                TickSeconds = config.TickSeconds,
                DisconnectTimeoutSeconds = 0.5f,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.2f },
            });

        // Initial connect.
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(0.016f); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        int firstNetId = client.LocalNetId;
        Assert.True(firstNetId > 0);

        // Restart: new server process. Stop ticking the old one; the client starves into Reconnecting.
        rh.Restart();
        var server2 = NewServer(rh.ServerTransport, config);

        bool sawReconnecting = false;
        for (int i = 0; i < 200; i++)
        {
            server2.Poll(); server2.Tick(config.TickSeconds); client.Poll(0.05f);
            if (client.ConnectionState == WorldConnectionState.Reconnecting) sawReconnecting = true;
            if (client.ConnectionState == WorldConnectionState.Connected && sawReconnecting) break;
        }

        Assert.True(sawReconnecting, "client never entered Reconnecting");
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.True(client.LocalNetId > 0, "no local net id after reconnect");

        // Replication resumed: the avatar is visible and controllable again.
        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        float zBefore = LocalZ(client);
        for (int i = 0; i < 12; i++)
        {
            client.SendInput(forward);
            server2.Poll(); server2.Tick(config.TickSeconds);
            client.Poll(0.016f); client.AdvancePresentation(config.TickSeconds);
        }
        Assert.True(LocalZ(client) < zBefore - 0.1f, "avatar not controllable after reconnect");
    }

    static float LocalZ(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.Z;
        throw new Xunit.Sdk.XunitException("no local entity after reconnect");
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldClientReconnectTests"`
Expected: FAIL to compile (the factory ctor / `IDisposable` / `Reconnect` config not defined).

- [ ] **Step 5: Add the reconnect config fields**

In `KhaozEngine.NetWorld/WorldClient.cs`, in `WorldClientConfig`, add:

```csharp
    /// <summary>Auto-reconnect on a mid-session drop (honored only when the factory ctor is used). Default true:
    /// the client rebuilds the transport, resumes the same token, and re-syncs without a manual rebuild.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>Keep retrying even after a token rejection. Default false: a rejected token is terminal (it will not
    /// fix itself), surfaced as <see cref="DisconnectReason.RejectedToken"/>.</summary>
    public bool RetryOnReject { get; init; } = false;

    /// <summary>Backoff schedule for auto-reconnect.</summary>
    public ReconnectBackoff Reconnect { get; init; } = ReconnectBackoff.Default;
```

- [ ] **Step 6: Convert `WorldClient` to a factory-backed, reconnecting, disposable client**

In `KhaozEngine.NetWorld/WorldClient.cs`:

(a) Change `net`, `world`, `view` from `readonly` to mutable (the reconnect rebuilds `net`/`world`/`view`):

```csharp
    private NetClient net;
    private World world = new();
    private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
    private ClientReplicationView view;
```

(b) Add reconnect fields:

```csharp
    private readonly Func<INetTransport>? connectFactory;   // null = single-shot instance path
    private INetTransport? ownedTransport;                  // disposed by us when factory-built
    private readonly byte[]? token;
    private readonly bool autoReconnect;
    private readonly bool retryOnReject;
    private readonly ReconnectBackoff backoff;
    private int attempt;                 // 0 while initial-connecting or connected; 1.. while reconnecting
    private bool awaitingBackoff;        // true while waiting out the inter-attempt delay
    private float retryWaitRemaining;    // backoff countdown
    private float attemptDeadlineRemaining;  // current live attempt's join deadline
```

(c) Refactor the existing instance ctor to delegate to a shared private core, and add the factory ctor. Replace the current ctor with:

```csharp
    /// <summary>Single-shot client over a caller-owned transport: no auto-reconnect (a drop is terminal, observable
    /// via <see cref="ConnectionState"/> + <see cref="DisconnectReason"/>). The caller owns disposing the transport.</summary>
    public WorldClient(INetTransport transport, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null)
        : this(connectFactory: null, transport, groundHeight, tuning, config, token, groundNormal, bounds, physics)
    {
        ArgumentNullException.ThrowIfNull(transport);
    }

    /// <summary>Reconnect-capable client: <paramref name="connect"/> is invoked once for the initial connection and
    /// again per reconnect attempt (rebuilding the transport + session), resuming the same <paramref name="token"/>.
    /// Auto-reconnect is on by default (see <see cref="WorldClientConfig.AutoReconnect"/>). This client owns and
    /// disposes the transports it builds; dispose the client to close the current one.</summary>
    public WorldClient(Func<INetTransport> connect, Func<float, float, float> groundHeight, MoveTuning tuning,
        WorldClientConfig? config = null, byte[]? token = null, Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null, IPhysicsWorld? physics = null)
        : this(connect ?? throw new ArgumentNullException(nameof(connect)), connect(), groundHeight, tuning, config,
               token, groundNormal, bounds, physics)
    {
    }

    private WorldClient(Func<INetTransport>? connectFactory, INetTransport transport,
        Func<float, float, float> groundHeight, MoveTuning tuning, WorldClientConfig? config, byte[]? token,
        Func<float, float, Vector3>? groundNormal, WorldBounds? bounds, IPhysicsWorld? physics)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        config ??= new WorldClientConfig();
        this.connectFactory = connectFactory;
        this.token = token;
        this.groundHeight = groundHeight;     // retained for rebuilding nothing; kept for clarity (unused after ctor)
        ownedTransport = connectFactory is not null ? transport : null;   // we dispose only what we built
        net = new NetClient(transport, token);
        view = new ClientReplicationView(registry);
        simulatorTuning = tuning;
        this.groundNormal = groundNormal;
        this.bounds = bounds;
        this.physics = physics;
        var simulator = new PlayerMoveSimulator(groundHeight, tuning, groundNormal, bounds, physics);
        PredictionSettings settings = config.Prediction ?? (PredictionSettings.Default with { TickSeconds = config.TickSeconds });
        prediction = new ClientPrediction<PlayerMoveState, MoveCommand>(simulator, settings);
        interpolateRemotes = config.InterpolateRemotes;
        tickSeconds = config.TickSeconds;
        snapshotInterval = config.TickSeconds;
        disconnectTimeout = config.DisconnectTimeoutSeconds;
        autoReconnect = config.AutoReconnect;
        retryOnReject = config.RetryOnReject;
        backoff = config.Reconnect;
        attemptDeadlineRemaining = disconnectTimeout;   // also bound the initial connect
    }
```

Note: the private core references helper fields the rebuild path needs. Add these fields (the ground/bounds/physics are only needed if you choose to rebuild the simulator; here `prediction` is kept across reconnect so they are not strictly required, but keep them to avoid signature churn):

```csharp
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly WorldBounds? bounds;
    private readonly IPhysicsWorld? physics;
    private readonly MoveTuning simulatorTuning;
```

(If a field ends up genuinely unused, drop it rather than leave it dangling. `prediction` is retained across reconnect, so the simulator is not rebuilt.)

(d) Add the reconnect helpers and update `Poll` to drive attempts. Replace the `Poll(float dt = 0f)` body with:

```csharp
    public void Poll(float dt = 0f)
    {
        // Waiting out a backoff delay between attempts: count down, then start the next attempt.
        if (awaitingBackoff)
        {
            retryWaitRemaining -= dt;
            if (retryWaitRemaining > 0f) return;
            StartAttempt();
        }

        net.Poll();
        bool gotFrame = false;
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
        {
            switch (ev.Kind)
            {
                case ClientSessionEventKind.Joined:
                    disconnectReason = DisconnectReason.None;
                    disconnectReasonDetail = string.Empty;
                    sawShutdownNotice = false;
                    attempt = 0;
                    secondsSinceServerFrame = 0f;
                    SetState(WorldConnectionState.Connected);
                    break;
                case ClientSessionEventKind.Data:
                    gotFrame = true;
                    OnServerFrame(ev.Data);
                    break;
                case ClientSessionEventKind.Rejected:
                    disconnectReason = DisconnectReason.RejectedToken;
                    disconnectReasonDetail = ev.RejectReason;
                    FailAttempt(allowReconnect: retryOnReject);
                    break;
                case ClientSessionEventKind.Disconnected:
                    if (state != WorldConnectionState.Disconnected)
                    {
                        disconnectReason = sawShutdownNotice ? DisconnectReason.ServerShutdown : DisconnectReason.Unreachable;
                        FailAttempt(allowReconnect: true);
                    }
                    break;
            }
        }
        if (gotFrame) secondsSinceServerFrame = 0f;
        if (dt <= 0f) return;

        if (state == WorldConnectionState.Connected)
        {
            secondsSinceServerFrame += dt;
            if (secondsSinceServerFrame >= disconnectTimeout)
            {
                disconnectReason = DisconnectReason.Timeout;
                FailAttempt(allowReconnect: true);
            }
        }
        else if (!awaitingBackoff && state != WorldConnectionState.Disconnected)
        {
            // A live attempt (initial Connecting or a reconnect attempt) that never joins: enforce a join deadline
            // so a down server (no transport-drop event over loopback) still fails the attempt and backs off.
            attemptDeadlineRemaining -= dt;
            if (attemptDeadlineRemaining <= 0f)
            {
                if (disconnectReason == DisconnectReason.None) disconnectReason = DisconnectReason.Timeout;
                FailAttempt(allowReconnect: true);
            }
        }
    }

    private bool CanReconnect => connectFactory is not null && autoReconnect;

    // A live attempt failed (drop, reject, or join-deadline). Either schedule another attempt or go terminal.
    private void FailAttempt(bool allowReconnect)
    {
        if (allowReconnect && CanReconnect && (backoff.MaxAttempts == 0 || attempt < backoff.MaxAttempts))
        {
            attempt = Math.Max(1, attempt + 1);
            retryWaitRemaining = backoff.DelayForAttempt(attempt);
            awaitingBackoff = true;
            SetState(WorldConnectionState.Reconnecting);
        }
        else
        {
            awaitingBackoff = false;
            SetState(WorldConnectionState.Disconnected);
        }
    }

    // Build a fresh transport + session for the next attempt, keeping prediction; drop stale replicated entities.
    private void StartAttempt()
    {
        awaitingBackoff = false;
        DisposeCurrentTransport();
        INetTransport transport = connectFactory!();
        ownedTransport = transport;
        net = new NetClient(transport, token);
        world = new World();
        view = new ClientReplicationView(registry);
        LocalNetId = -1;
        secondsSinceServerFrame = 0f;
        attemptDeadlineRemaining = disconnectTimeout;
        // Reset remote-interpolation bookkeeping so the new stream starts clean.
        snapshotInterval = tickSeconds;
        secondsSinceSnapshot = 0f;
        sawFirstSnapshot = false;
        // state stays Reconnecting until this attempt's Joined.
    }

    private void DisposeCurrentTransport()
    {
        if (connectFactory is not null) ownedTransport?.Dispose();
        ownedTransport = null;
    }

    /// <summary>Number of the in-flight reconnect attempt (0 while connected or on the initial connect). Render
    /// "reconnecting (attempt N)..." from this and <see cref="SecondsUntilNextRetry"/>.</summary>
    public int ReconnectAttempt => attempt;

    /// <summary>Seconds until the next reconnect attempt fires while waiting out backoff; 0 otherwise.</summary>
    public float SecondsUntilNextRetry => awaitingBackoff ? MathF.Max(0f, retryWaitRemaining) : 0f;

    /// <summary>Closes the current transport (if this client built it). Idempotent.</summary>
    public void Dispose()
    {
        DisposeCurrentTransport();
        SetState(WorldConnectionState.Disconnected);
    }
```

Note: confirm `MathF` is usable (the file already uses `Math.Clamp`; add `using System;` is already present). The `World` type comes from `KhaozEngine.Ecs` (already imported).

- [ ] **Step 7: Run the reconnect test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~WorldClientReconnectTests"`
Expected: PASS.

- [ ] **Step 8: Run the full NetWorld + Netcode suite (regression)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NetWorld|FullyQualifiedName~Netcode"`
Expected: PASS (all prior behaviour intact; instance-ctor paths unchanged).

- [ ] **Step 9: Commit**

```bash
git add KhaozEngine.NetWorld/ReconnectBackoff.cs KhaozEngine.NetWorld/WorldClient.cs KhaozEngine.Tests/NetWorld/RestartableHub.cs KhaozEngine.Tests/NetWorld/WorldClientReconnectTests.cs
git commit -m "netcode(client): transport-factory auto-reconnect with backoff (IDisposable)"
```

---

### Task 10: Release 8.2.0 (version bump + changelog + doc sweep + pack)

Per the engine release ritual. Push/tag is held and batched (confirm with the user before publishing); this task stops at the local pack.

**Files:**
- Modify: `Directory.Build.props` (`<KhaozEngineVersion>`)
- Modify: `CHANGELOG.md`
- Modify: `docs/CONSUMERS.md` ("Engine current version" line)
- Modify: `docs/ROADMAP.md` ("Current released version" line)
- Modify: `README.md` (the `<PackageReference>` example version)
- Modify: `CLAUDE.md` (the NetWorld package map: reconnect/notice/drain surface)
- Modify: `docs/USING-KHAOZENGINE.md` (a usage section for reconnect + notices)

- [ ] **Step 1: Confirm the current version and the three guard declarations**

Run: `bash scripts/check-doc-versions.sh`
Expected: passes at the current `8.1.0`. Note the three files it checks.

- [ ] **Step 2: Bump the version**

In `Directory.Build.props`, change `<KhaozEngineVersion>8.1.0</KhaozEngineVersion>` to `<KhaozEngineVersion>8.2.0</KhaozEngineVersion>`.

- [ ] **Step 3: Add the CHANGELOG entry (newest-first)**

In `CHANGELOG.md`, add at the top of the entries (one-line summary first, then detail). Use this content (no em-dashes):

```markdown
## 8.2.0

Mid-session reconnect + a server->client notice channel in NetWorld, so a client survives a server restart gracefully.

- `WorldClient` now exposes a live connection state machine (`ConnectionState`: Connecting / Connected / Reconnecting / Disconnected, with `ConnectionStateChanged`), a disconnect reason (`DisconnectReason`: None / RejectedToken / Unreachable / ServerShutdown / Timeout, plus `DisconnectReasonDetail`), and a fast mid-session disconnect detector (transport drop, or `WorldClientConfig.DisconnectTimeoutSeconds` of no snapshots, default 3s). `Joined` is now `ConnectionState == Connected`. The previously swallowed `Rejected` session event is surfaced as `RejectedToken` + detail.
- New `WorldClient(Func<INetTransport> connect, ...)` ctor adds auto-reconnect with backoff (`WorldClientConfig.AutoReconnect`, default true when a factory is supplied; `ReconnectBackoff`; `RetryOnReject`). It rebuilds the transport + session resuming the same connect token, keeps the prediction object, and rebuilds the replication view so the local avatar re-syncs to the authoritative (persistence-restored) state with no duplicate or desync. `ReconnectAttempt` + `SecondsUntilNextRetry` drive a "reconnecting..." UI. `WorldClient` is now `IDisposable` (it owns the transports the factory builds). The existing `WorldClient(INetTransport, ...)` ctor is unchanged (single-shot, no reconnect).
- `WorldClient.Poll()` is now `Poll(float dt = 0f)`; existing `Poll()` calls are unchanged (dt 0 pumps the net but freezes the health timers). Reconnect/timeout detection needs the consumer to pass real frame dt.
- Server->client notices: `ServerNotice { Kind, Message, SecondsUntil, Payload }` (`ServerNoticeKind` Custom / Maintenance / Shutdown), `WorldServer.BroadcastNotice` + `ShardedWorldServer.BroadcastNotice`, surfaced on `WorldClient.NoticeReceived` + `LastNotice`. Graceful drain: `BeginDrain(notice, graceSeconds)` + `IsDraining` / `IsDrainComplete` on both servers (tick-driven, no wall clock); the host flushes `WorldPersistence.FlushAsync()` then disposes the transport on completion.
- Wire note: the server->client Data stream now carries a 1-byte `ServerFrameKind` envelope (snapshot vs notice). Internal protocol only (server + client ship from the same engine version); no public-API break.
```

- [ ] **Step 4: Update the three guard-checked declarations**

- `docs/CONSUMERS.md`: set the "Engine current version" line to `8.2.0`.
- `docs/ROADMAP.md`: set the "Current released version" line to `8.2.0`.
- `README.md`: set the `<PackageReference ... Version="8.2.0" />` example.

Run: `bash scripts/check-doc-versions.sh`
Expected: PASS at `8.2.0`.

- [ ] **Step 5: Doc sweep (package map + usage)**

- In `CLAUDE.md`, in the `NetWorld` package description, add a sentence covering the new surface: `WorldClient` connection state machine + `DisconnectReason` + auto-reconnect (factory ctor, `ReconnectBackoff`, `IDisposable`, `Poll(dt)`); `ServerNotice`/`ServerNoticeKind` + `BroadcastNotice` (both servers) + `BeginDrain`/`IsDrainComplete`; the 1-byte `ServerFrameKind` envelope.
- In `docs/USING-KHAOZENGINE.md`, add a short "Reconnect + server notices" section: the factory ctor + `Poll(dt)` + `ConnectionState`/`ReconnectAttempt`/`SecondsUntilNextRetry`, reading `NoticeReceived`/`LastNotice`, and the server `BeginDrain` shutdown pattern (BeginDrain, tick until `IsDrainComplete`, `FlushAsync`, dispose transport).
- Mechanical check: `grep -rn "ServerNotice\|BeginDrain\|ConnectionState\|ReconnectBackoff" CLAUDE.md docs/USING-KHAOZENGINE.md docs/CONSUMERS.md README.md` and confirm every doc that should mention the new surface does.

- [ ] **Step 6: Pack to local-feed**

Run:
```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
```
Expected: `KhaozEngine.NetWorld.8.2.0.nupkg` + the other packages land in `local-feed/`.

- [ ] **Step 7: Run the full test suite once more**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit (single release commit)**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md CLAUDE.md docs/USING-KHAOZENGINE.md
git commit -m "netcode(8.2.0): mid-session reconnect + server->client notice channel"
```

- [ ] **Step 9: Hold the tag/push**

Do NOT `git tag` / push yet. Per the engine batch policy, surface to the user that 8.2.0 is packed locally and ready to tag + push (`git tag v8.2.0`, push `main` + the tag) when they want to publish.

---

## Self-Review

**Spec coverage:**
- Connection state machine (spec 1) -> Task 7 (+ Reconnecting in Task 9). Covered.
- Mid-session disconnect detector, configurable, default a few seconds (spec 2) -> Task 8 (`DisconnectTimeoutSeconds` = 3s) + transport drop in Task 7. Covered.
- Disconnect/reject reason (spec 3) -> Task 7 (RejectedToken/Unreachable) + Task 8 (Timeout/ServerShutdown). The swallowed `Rejected` is now handled. Covered.
- Auto-reconnect, backoff, same identity, attempt/next-retry (spec 4) -> Task 9. Covered.
- Notice channel (spec 5): frame demux (Tasks 1-2), type+codec (Task 3), server broadcast + client surface (Task 4), sharded parity (Task 6). Covered.
- Graceful drain (spec 6) -> Task 5 (`BeginDrain`) + Task 6 (sharded). Persistence flush stays the host's call (documented in Task 10 usage). Covered.
- Acceptance tests: reconnect across restart (Task 9), notice delivery (Task 4), drain broadcasts+closes (Task 5/6), distinct reasons bad-token/unreachable/server-shutdown/timeout (Tasks 7-8). Covered.
- Release + docs (spec 8) -> Task 10. Ruinborne adoption is out of scope (spec) and not in the plan. Covered.

**Placeholder scan:** No TBD/TODO; every code step shows full code. Two verification notes (confirm `IConnectionAuthenticator.TryAuthenticate` signature in Task 7; confirm an unused rebuild field is dropped in Task 9) are explicit cross-checks, not deferred work.

**Type consistency:** `ServerFrameKind` / `EncodeServerFrame` / `TryDecodeServerFrame` (Tasks 1-2-4-6), `ServerNotice` ctor + `EncodeNotice` / `TryDecodeNotice` (Tasks 3-4-6-8), `BroadcastNotice` (Tasks 4-6), `DrainController.Begin/Advance/IsDraining/IsComplete` + `BeginDrain`/`IsDrainComplete` (Tasks 5-6), `WorldConnectionState` / `DisconnectReason` / `ConnectionState` / `DisconnectReasonDetail` (Tasks 7-8-9), `Poll(float dt = 0f)` (Tasks 8-9), `ReconnectBackoff.DelayForAttempt` + factory ctor + `ReconnectAttempt` / `SecondsUntilNextRetry` (Task 9) are consistent across tasks.
