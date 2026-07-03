# KhaozEngine.Social + KhaozEngine.Social.Discord Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an engine-owned, provider-neutral social seam (`KhaozEngine.Social`) plus a pure-managed Discord Rich Presence backend (`KhaozEngine.Social.Discord`) so games stop shipping bespoke Discord code.

**Architecture:** Seam + opt-in backend, mirroring `KhaozEngine.Physics` / `KhaozEngine.Physics.Bepu`. The seam (`ISocialProvider`, value types, `NullSocialProvider` no-op default, and a generic `SocialPresenceController` orchestrator) lives in the `Foundation` umbrella and depends only on `KhaozEngine.Diagnostics`. The Discord backend is a pure-managed IPC client (Windows named pipe / unix domain socket, opcode+length+JSON framing over System.Text.Json), in NO umbrella, referencing only the seam. Zero native binaries, zero third-party NuGet.

**Tech Stack:** net10.0, C# latest, `System.IO.Pipes` (Windows) + `System.Net.Sockets` unix domain socket (macOS/Linux), `System.Text.Json`, xUnit. Nullable enabled, ImplicitUsings disabled (every file declares its `using`s).

## Global Constraints

Copied verbatim from the spec; every task implicitly includes these.

- **Target engine version: 9.10.0.** 9.9.0 was claimed by a concurrent GPU terrain-fuzz change tagged `v9.9.0`. Re-check `origin/main` + `git tag` again in the release task and take the next free minor if 9.10.0 is now taken.
- **Zero native, zero third-party.** Neither new package may reference any NuGet package or native binary. System.Text.Json + BCL only. (The engine tolerates Newtonsoft only as a forced transitive CVE-override under `KhaozEngine.Gpu`; never add it here.)
- **MonoGame-free, GPU-free.** Both packages are pure BCL. No Windowing/Render/Gpu references.
- **Never throw into the game loop.** Every `ISocialProvider` and backend entry point is best-effort: a Discord/socket failure degrades to disconnected and is swallowed. The only logging is debug-level via `KhaozEngine.Diagnostics`, never `Console.WriteLine`.
- **Headless-testable.** The seam and all backend logic except the live socket are unit-tested with no live Discord. Any test that touches a real socket is tagged `[Trait("Category", "LiveSocket")]` (CI runs `--filter "Category!=LiveSocket"`).
- **Provider-neutral seam.** The seam names none of Discord's concepts (no "Discord" in `KhaozEngine.Social`). A future `.Steam` / `.Native` backend must slot in behind the same `ISocialProvider` unchanged.
- **ImplicitUsings disabled** across the repo, so every `.cs` file lists its own `using` directives.
- **One version bump per batch.** Bump `<KhaozEngineVersion>` once, in the final release task, not per package.
- **Nullable enable.** `#nullable` is on repo-wide via Directory.Build.props; annotate reference types.

## File Structure

**New package `KhaozEngine.Social/`** (Foundation umbrella, deps: `KhaozEngine.Diagnostics`):
- `KhaozEngine.Social.csproj` - seam package project.
- `README.md` - PackageReadmeFile (required by the doc guard).
- `ISocialProvider.cs` - the provider-neutral seam interface.
- `RichPresence.cs` - `RichPresence`, `PresenceImage`, `PresenceParty`, `PresenceButton` value types.
- `SocialUser.cs` - `SocialUser` value type.
- `JoinRequest.cs` - `JoinRequest` (carries the requesting user + `Accept()`/`Reject()`).
- `NullSocialProvider.cs` - no-op default.
- `SocialPresenceController.cs` - `SocialPresenceController` + `SocialPresenceOptions` orchestrator.

**New package `KhaozEngine.Social.Discord/`** (no umbrella, deps: `KhaozEngine.Social`):
- `KhaozEngine.Social.Discord.csproj` - backend project.
- `README.md` - PackageReadmeFile.
- `DiscordSocialOptions.cs` - options (`ApplicationId`, etc.).
- `DiscordSocialProvider.cs` - `DiscordSocialProvider : ISocialProvider`.
- `Internal/DiscordIpcOpcode.cs` - opcode enum.
- `Internal/DiscordIpcCodec.cs` - frame encode/decode (pure, headless).
- `Internal/DiscordIpcPayloads.cs` - STJ DTOs + `RichPresence` -> activity mapping + dispatch parsing.
- `Internal/DiscordSocketPaths.cs` - unix candidate socket path enumeration (injectable env).
- `Internal/IDiscordIpcTransport.cs` - injectable IO seam (connect/read/write).
- `Internal/NamedPipeDiscordTransport.cs` - real transport (Windows pipe / unix domain socket).
- `Internal/DiscordIpcClient.cs` - ties codec + transport + handshake + dispatch pump.

**Tests `KhaozEngine.Tests/Social/`:**
- `FakeSocialProvider.cs` - records calls (the `FakeMusicBackend` pattern).
- `NullSocialProviderTests.cs`
- `SocialPresenceControllerTests.cs`
- `DiscordIpcCodecTests.cs`
- `DiscordActivityPayloadTests.cs`
- `DiscordSocketPathsTests.cs`
- `FakeDiscordIpcTransport.cs` - in-memory transport double.
- `DiscordIpcClientTests.cs`
- `DiscordSocialProviderTests.cs`
- `DiscordLiveSocketTests.cs` - `[Trait("Category","LiveSocket")]`, excluded in CI.

**Wiring / docs modified:**
- `KhaozEngine.slnx`, `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`, `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/DEPENDENCY-SEAMS.md`, `docs/USING-KHAOZENGINE.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `CLAUDE.md`.

## Notes for the implementer (engine conventions)

- **Build one project fast:** `dotnet build KhaozEngine.Social/KhaozEngine.Social.csproj`. **Run one test class:** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SocialPresenceControllerTests"`. **Full suite (CI parity):** `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Category!=LiveSocket"`.
- `local-feed/` must exist before restore: `mkdir -p local-feed`.
- Commit style: `area(scope): summary`. Use `social:` scope for feature commits; on the version-bump commit use the version as scope, e.g. `social(9.10.0): ...`.
- All work happens in the worktree at `/Users/antonio/KhaozEngine/.claude/worktrees/feature+social-discord` on branch `worktree-feature+social-discord`. Do NOT cd to the main checkout.

---

### Task 1: Scaffold `KhaozEngine.Social` seam package (value types, interface, Null provider)

**Files:**
- Create: `KhaozEngine.Social/KhaozEngine.Social.csproj`
- Create: `KhaozEngine.Social/RichPresence.cs`
- Create: `KhaozEngine.Social/SocialUser.cs`
- Create: `KhaozEngine.Social/JoinRequest.cs`
- Create: `KhaozEngine.Social/ISocialProvider.cs`
- Create: `KhaozEngine.Social/NullSocialProvider.cs`
- Create: `KhaozEngine.Social/README.md`
- Modify: `KhaozEngine.slnx` (add the project)
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add ProjectReference)
- Modify: `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj` (add ProjectReference + Description)
- Test: `KhaozEngine.Tests/Social/NullSocialProviderTests.cs`

**Interfaces:**
- Produces: `ISocialProvider`, `NullSocialProvider`, and the value types `RichPresence`, `PresenceImage`, `PresenceParty`, `PresenceButton`, `SocialUser`, `JoinRequest` in `namespace KhaozEngine.Social`. Signatures as written below; every later task consumes them.

- [ ] **Step 1: Create the value types**

`KhaozEngine.Social/RichPresence.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Social;

/// <summary>
/// A provider-neutral rich-presence descriptor. A game fills the fields it wants; empty/default
/// fields are omitted by the backend. Only <see cref="Details"/>, <see cref="State"/> and
/// <see cref="StartTimestampUtc"/> are required for a basic "playing X" presence.
/// </summary>
public readonly record struct RichPresence
{
    /// <summary>First line on the profile (e.g. "In the overworld").</summary>
    public string? Details { get; init; }

    /// <summary>Second line on the profile (e.g. "Solo - 04:12").</summary>
    public string? State { get; init; }

    /// <summary>When set, the platform renders an elapsed timer counting up from this instant.</summary>
    public DateTime? StartTimestampUtc { get; init; }

    /// <summary>When set, the platform renders a countdown to this instant.</summary>
    public DateTime? EndTimestampUtc { get; init; }

    /// <summary>Large profile image (asset key + hover text).</summary>
    public PresenceImage LargeImage { get; init; }

    /// <summary>Small profile image (asset key + hover text).</summary>
    public PresenceImage SmallImage { get; init; }

    /// <summary>Party grouping (id + current/max size). A non-zero <see cref="PresenceParty.Max"/> shows "(size of max)".</summary>
    public PresenceParty Party { get; init; }

    /// <summary>Opaque secret enabling a "Join Game" action on the profile; the game's netcode encodes/decodes it.</summary>
    public string? JoinSecret { get; init; }

    /// <summary>Opaque secret enabling a "Spectate" action.</summary>
    public string? SpectateSecret { get; init; }

    /// <summary>Up to two profile buttons (label + URL). Ignored beyond the platform's limit.</summary>
    public IReadOnlyList<PresenceButton>? Buttons { get; init; }
}

/// <summary>A presence image: an uploaded asset key plus optional hover text. Default is "no image".</summary>
public readonly record struct PresenceImage(string? Key, string? Text);

/// <summary>Party grouping for presence. <see cref="Id"/> groups members; <see cref="Size"/>/<see cref="Max"/> render "(n of m)".</summary>
public readonly record struct PresenceParty(string? Id, int Size, int Max);

/// <summary>A profile button: display label + URL to open.</summary>
public readonly record struct PresenceButton(string Label, string Url);
```

`KhaozEngine.Social/SocialUser.cs`:
```csharp
namespace KhaozEngine.Social;

/// <summary>
/// A platform user identity. <see cref="Username"/> is the login/handle (e.g. the Discord username);
/// <see cref="GlobalName"/> is the display name where the platform distinguishes the two.
/// </summary>
public readonly record struct SocialUser(string Id, string Username, string? GlobalName);
```

`KhaozEngine.Social/JoinRequest.cs`:
```csharp
using System;

namespace KhaozEngine.Social;

/// <summary>
/// An inbound "ask to join" from another user. The game calls <see cref="Accept"/> or
/// <see cref="Reject"/> exactly once; both are best-effort and never throw.
/// </summary>
public sealed class JoinRequest
{
    private readonly Action<bool>? respond;
    private bool answered;

    public JoinRequest(SocialUser user, Action<bool>? respond)
    {
        User = user;
        this.respond = respond;
    }

    /// <summary>The user asking to join.</summary>
    public SocialUser User { get; }

    /// <summary>Approve the request (idempotent; only the first call has effect).</summary>
    public void Accept() => Answer(true);

    /// <summary>Decline the request (idempotent; only the first call has effect).</summary>
    public void Reject() => Answer(false);

    private void Answer(bool accept)
    {
        if (answered)
        {
            return;
        }

        answered = true;
        respond?.Invoke(accept);
    }
}
```

- [ ] **Step 2: Create the seam interface**

`KhaozEngine.Social/ISocialProvider.cs`:
```csharp
using System;

namespace KhaozEngine.Social;

/// <summary>
/// A provider-neutral social/presence backend (Discord today, Steam/other tomorrow). Every method is
/// best-effort: a transport failure degrades to disconnected and never throws into the caller. Games
/// normally talk to <see cref="SocialPresenceController"/> rather than this directly.
/// </summary>
public interface ISocialProvider : IDisposable
{
    /// <summary>True once connected to the platform client and ready to publish presence.</summary>
    bool IsConnected { get; }

    /// <summary>Connect for the given platform application/client id. Returns false on any failure.</summary>
    bool TryInitialize(string applicationId);

    /// <summary>Pump platform callbacks. Call once per frame on the main thread.</summary>
    void Update();

    /// <summary>Publish the local player's rich presence.</summary>
    void SetPresence(in RichPresence presence);

    /// <summary>Clear any published presence.</summary>
    void ClearPresence();

    /// <summary>The local platform identity, once connected. Returns false when unknown.</summary>
    bool TryGetLocalUser(out SocialUser user);

    /// <summary>Raised when a friend activates "Join Game"; carries the game-encoded join secret.</summary>
    event Action<string> JoinRequested;

    /// <summary>Raised when another user asks to join; the game accepts or rejects the request.</summary>
    event Action<JoinRequest> JoinRequestReceived;
}
```

- [ ] **Step 3: Write the failing NullSocialProvider test**

`KhaozEngine.Tests/Social/NullSocialProviderTests.cs`:
```csharp
using KhaozEngine.Social;
using Xunit;

namespace KhaozEngine.Tests;

public class NullSocialProviderTests
{
    [Fact]
    public void NullProvider_IsSilentAndNeverThrows()
    {
        ISocialProvider social = new NullSocialProvider();

        Assert.False(social.TryInitialize("123"));
        social.SetPresence(new RichPresence { Details = "x", State = "y" });
        social.ClearPresence();
        social.Update();

        Assert.False(social.IsConnected);
        Assert.False(social.TryGetLocalUser(out SocialUser user));
        Assert.Equal(default, user);

        social.Dispose();
    }
}
```

- [ ] **Step 4: Run the test to verify it fails to compile**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NullSocialProviderTests"`
Expected: FAIL - `KhaozEngine.Social` does not exist / type `NullSocialProvider` not found.

- [ ] **Step 5: Create the NullSocialProvider**

`KhaozEngine.Social/NullSocialProvider.cs`:
```csharp
using System;

namespace KhaozEngine.Social;

/// <summary>
/// No-op provider used when no social platform is available (headless servers, CI, tests, or a game
/// that did not add a backend). Silent, never connects, never throws. This is the default a
/// <see cref="SocialPresenceController"/> uses when no provider is supplied.
/// </summary>
public sealed class NullSocialProvider : ISocialProvider
{
    public bool IsConnected => false;
    public bool TryInitialize(string applicationId) => false;
    public void Update() { }
    public void SetPresence(in RichPresence presence) { }
    public void ClearPresence() { }

    public bool TryGetLocalUser(out SocialUser user)
    {
        user = default;
        return false;
    }

    public event Action<string> JoinRequested { add { } remove { } }
    public event Action<JoinRequest> JoinRequestReceived { add { } remove { } }

    public void Dispose() { }
}
```

- [ ] **Step 6: Create the seam project file**

`KhaozEngine.Social/KhaozEngine.Social.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Social</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <Description>Game-agnostic social/presence seam for KhaozEngine: ISocialProvider (rich presence, local identity, join/invite) with a NullSocialProvider no-op default and a SocialPresenceController that throttles/dedupes presence and self-disables on error. Provider-neutral and dependency-free (only KhaozEngine.Diagnostics); the Discord backend is the opt-in KhaozEngine.Social.Discord package. Pure BCL, headless-testable.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 7: Create a minimal per-package README (expanded in Task 9)**

`KhaozEngine.Social/README.md`:
```markdown
# KhaozEngine.Social

Game-agnostic social/presence seam. `ISocialProvider` is the provider-neutral contract (Discord today,
Steam/other later) for rich presence, local identity, and join/invite. `NullSocialProvider` is the
silent no-op default; `SocialPresenceController` adds throttling, dedupe, and error self-disable on top
of any provider. Depends only on `KhaozEngine.Diagnostics`. The Discord backend is the opt-in
[KhaozEngine.Social.Discord](../KhaozEngine.Social.Discord) package (in no umbrella).

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
```

- [ ] **Step 8: Register the project in the solution**

In `KhaozEngine.slnx`, add after the `KhaozEngine.Snapshot.Render3D` line (alongside the other packages; exact position is not significant):
```xml
  <Project Path="KhaozEngine.Social/KhaozEngine.Social.csproj" />
  <Project Path="KhaozEngine.Social.Discord/KhaozEngine.Social.Discord.csproj" />
```
(Adding both now avoids a second slnx edit in Task 4. The `.Discord` project is created in Task 4; a missing project path only affects that project's build until then, which is not built until Task 4.)

- [ ] **Step 9: Add the ProjectReference to the test project**

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, inside the `<ItemGroup>` of ProjectReferences (after the `KhaozEngine.Serialization` line), add:
```xml
    <ProjectReference Include="../KhaozEngine.Social/KhaozEngine.Social.csproj" />
    <ProjectReference Include="../KhaozEngine.Social.Discord/KhaozEngine.Social.Discord.csproj" />
```
(Same note: the `.Discord` reference resolves once Task 4 creates that project. If your executor builds the test project between Task 1 and Task 4, temporarily add only the `KhaozEngine.Social` line here and add the `.Discord` line in Task 4.)

- [ ] **Step 10: Add the seam to the Foundation umbrella**

In `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`:
1. Add a ProjectReference in alphabetical position (after `KhaozEngine.Serialization`, before `KhaozEngine.Terrain`):
```xml
    <ProjectReference Include="../KhaozEngine.Social/KhaozEngine.Social.csproj" />
```
2. Extend the `<Description>` inventory list. Change `Serialization, Collision, Platform, Terrain, Updates` to `Serialization, Social, Collision, Platform, Terrain, Updates`.

- [ ] **Step 11: Run the NullSocialProvider test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~NullSocialProviderTests"`
Expected: PASS (1 test).

- [ ] **Step 12: Commit**

```bash
git add KhaozEngine.Social KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Foundation/KhaozEngine.Foundation.csproj KhaozEngine.Tests/Social/NullSocialProviderTests.cs
git commit -m "social: KhaozEngine.Social seam (ISocialProvider, value types, NullSocialProvider)"
```

---

### Task 2: `SocialPresenceController` orchestration (throttle / dedupe / session-disable / elapsed)

**Files:**
- Create: `KhaozEngine.Social/SocialPresenceController.cs`
- Create: `KhaozEngine.Tests/Social/FakeSocialProvider.cs`
- Test: `KhaozEngine.Tests/Social/SocialPresenceControllerTests.cs`

**Interfaces:**
- Consumes: `ISocialProvider`, `RichPresence`, `SocialUser`, `JoinRequest` (Task 1).
- Produces: `SocialPresenceController` (game-facing) and `SocialPresenceOptions`. Signatures below; SpaceGame and the other consumers wrap this.

- [ ] **Step 1: Create the FakeSocialProvider test double**

`KhaozEngine.Tests/Social/FakeSocialProvider.cs`:
```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Social;

namespace KhaozEngine.Tests;

/// <summary>
/// Records calls made to an <see cref="ISocialProvider"/> so the orchestration in
/// <see cref="SocialPresenceController"/> can be asserted without a live platform.
/// </summary>
internal sealed class FakeSocialProvider : ISocialProvider
{
    public List<string> InitializedWith { get; } = new();
    public List<RichPresence> PresenceCalls { get; } = new();
    public int ClearCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public bool InitializeResult { get; set; } = true;
    public bool ConnectedResult { get; set; } = true;
    public SocialUser? LocalUser { get; set; }

    /// <summary>When set, the next call to the named method throws to exercise session-disable.</summary>
    public bool ThrowOnSetPresence { get; set; }
    public bool ThrowOnUpdate { get; set; }

    public bool IsConnected => ConnectedResult;

    public bool TryInitialize(string applicationId)
    {
        InitializedWith.Add(applicationId);
        return InitializeResult;
    }

    public void Update()
    {
        UpdateCalls++;
        if (ThrowOnUpdate)
        {
            throw new InvalidOperationException("boom");
        }
    }

    public void SetPresence(in RichPresence presence)
    {
        if (ThrowOnSetPresence)
        {
            throw new InvalidOperationException("boom");
        }

        PresenceCalls.Add(presence);
    }

    public void ClearPresence() => ClearCalls++;

    public bool TryGetLocalUser(out SocialUser user)
    {
        if (LocalUser is { } u)
        {
            user = u;
            return true;
        }

        user = default;
        return false;
    }

    public event Action<string>? JoinRequested;
    public event Action<JoinRequest>? JoinRequestReceived;

    public void RaiseJoinRequested(string secret) => JoinRequested?.Invoke(secret);
    public void RaiseJoinRequestReceived(JoinRequest request) => JoinRequestReceived?.Invoke(request);

    public void Dispose() => DisposeCalls++;
}
```

- [ ] **Step 2: Write the failing controller tests**

`KhaozEngine.Tests/Social/SocialPresenceControllerTests.cs`:
```csharp
using System;
using KhaozEngine.Social;
using Xunit;

namespace KhaozEngine.Tests;

public class SocialPresenceControllerTests
{
    private static SocialPresenceController Make(FakeSocialProvider fake, out FakeSocialProvider provider)
    {
        provider = fake;
        var options = new SocialPresenceOptions { RepublishInterval = TimeSpan.FromSeconds(15) };
        var controller = new SocialPresenceController(fake, options);
        controller.Initialize();
        return controller;
    }

    [Fact]
    public void Initialize_ForwardsToProvider()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        Assert.True(controller.IsEnabled);
        Assert.Single(fake.InitializedWith);
    }

    [Fact]
    public void FailedInitialize_DisablesController()
    {
        var fake = new FakeSocialProvider { InitializeResult = false };
        using var controller = new SocialPresenceController(fake);
        controller.Initialize();
        Assert.False(controller.IsEnabled);
        controller.SetPresence(new RichPresence { Details = "a" });
        Assert.Empty(fake.PresenceCalls);
    }

    [Fact]
    public void SetPresence_DedupesIdenticalContent()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        var p = new RichPresence { Details = "In Menu", State = "Idle" };
        controller.SetPresence(p);
        controller.SetPresence(p);
        Assert.Single(fake.PresenceCalls);
    }

    [Fact]
    public void SetPresence_ResendsWhenContentChanges()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        controller.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });
        controller.SetPresence(new RichPresence { Details = "In Game", State = "Fighting" });
        Assert.Equal(2, fake.PresenceCalls.Count);
    }

    [Fact]
    public void Force_ResendsIdenticalContent()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        var p = new RichPresence { Details = "In Game" };
        controller.SetPresence(p);
        controller.SetPresence(p, force: true);
        Assert.Equal(2, fake.PresenceCalls.Count);
    }

    [Fact]
    public void SetElapsedPresence_SetsStartTimestampFromElapsed()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        DateTime before = DateTime.UtcNow;
        controller.SetElapsedPresence(new RichPresence { Details = "In Game" }, TimeSpan.FromSeconds(60));
        DateTime after = DateTime.UtcNow;

        RichPresence sent = Assert.Single(fake.PresenceCalls);
        Assert.NotNull(sent.StartTimestampUtc);
        // start ~= now - 60s, within the wall-clock window of the call.
        Assert.InRange(sent.StartTimestampUtc!.Value,
            before.AddSeconds(-61), after.AddSeconds(-59));
    }

    [Fact]
    public void ProviderThrow_DisablesSessionAndDisposesProvider()
    {
        var fake = new FakeSocialProvider { ThrowOnSetPresence = true };
        using var controller = Make(fake, out _);
        controller.SetPresence(new RichPresence { Details = "boom" });
        Assert.False(controller.IsEnabled);
        Assert.Equal(1, fake.DisposeCalls);
        // Subsequent calls are silent no-ops.
        controller.SetPresence(new RichPresence { Details = "again" });
        controller.Update();
    }

    [Fact]
    public void JoinRequested_ForwardsThroughController()
    {
        var fake = new FakeSocialProvider();
        using var controller = Make(fake, out _);
        string? got = null;
        controller.JoinRequested += s => got = s;
        fake.RaiseJoinRequested("secret-123");
        Assert.Equal("secret-123", got);
    }

    [Fact]
    public void TryGetLocalUser_PassesThrough()
    {
        var fake = new FakeSocialProvider { LocalUser = new SocialUser("1", "kiwi", null) };
        using var controller = Make(fake, out _);
        Assert.True(controller.TryGetLocalUser(out SocialUser user));
        Assert.Equal("kiwi", user.Username);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SocialPresenceControllerTests"`
Expected: FAIL - `SocialPresenceController` / `SocialPresenceOptions` not found.

- [ ] **Step 4: Implement the controller**

`KhaozEngine.Social/SocialPresenceController.cs`:
```csharp
using System;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Social;

/// <summary>Tuning for <see cref="SocialPresenceController"/>.</summary>
public sealed class SocialPresenceOptions
{
    /// <summary>Minimum wall-clock time before an unchanged presence is re-published. Default 15s.</summary>
    public TimeSpan RepublishInterval { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Game-facing presence orchestrator over any <see cref="ISocialProvider"/>. Handles lazy init,
/// dedupe (only re-send when content changed), throttled republish, an elapsed-timer helper, and
/// session self-disable: any throw from the provider permanently disables social for the session and
/// disposes the provider, so a platform failure never reaches the game loop. Provider-neutral, so it
/// drives Discord today and any future backend unchanged.
/// </summary>
public sealed class SocialPresenceController : IDisposable
{
    private readonly ISocialProvider provider;
    private readonly SocialPresenceOptions options;

    private bool initializeAttempted;
    private bool enabled;
    private bool disabled;
    private bool disposed;

    private RichPresence lastPresence;
    private bool hasLastPresence;
    private DateTime lastPublishUtc = DateTime.MinValue;

    public SocialPresenceController(ISocialProvider? provider = null, SocialPresenceOptions? options = null)
    {
        this.provider = provider ?? new NullSocialProvider();
        this.options = options ?? new SocialPresenceOptions();

        this.provider.JoinRequested += OnJoinRequested;
        this.provider.JoinRequestReceived += OnJoinRequestReceived;
    }

    /// <summary>The platform application/client id to initialize with. Set before <see cref="Initialize"/>.</summary>
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>True when a provider is connected and presence will be published.</summary>
    public bool IsEnabled => enabled && !disabled && !disposed;

    /// <summary>Raised when a friend activates "Join Game"; carries the game-encoded join secret.</summary>
    public event Action<string>? JoinRequested;

    /// <summary>Raised when another user asks to join.</summary>
    public event Action<JoinRequest>? JoinRequestReceived;

    /// <summary>Connect the provider. Safe to call repeatedly; only the first attempt connects.</summary>
    public void Initialize()
    {
        if (initializeAttempted || disabled || disposed)
        {
            return;
        }

        initializeAttempted = true;
        try
        {
            enabled = provider.TryInitialize(ApplicationId);
            if (!enabled)
            {
                Disable();
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"social: initialize failed ({ex.GetType().Name}); disabling.");
            Disable();
        }
    }

    /// <summary>Publish presence, deduped by content and throttled by <see cref="SocialPresenceOptions.RepublishInterval"/>.</summary>
    public void SetPresence(in RichPresence presence, bool force = false)
    {
        if (!EnsureReady())
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        bool changed = !hasLastPresence || !presence.Equals(lastPresence);
        bool stale = now - lastPublishUtc >= options.RepublishInterval;
        if (!force && !changed && !stale)
        {
            return;
        }

        try
        {
            provider.SetPresence(presence);
            lastPresence = presence;
            hasLastPresence = true;
            lastPublishUtc = now;
        }
        catch (Exception ex)
        {
            Log.Debug($"social: set-presence failed ({ex.GetType().Name}); disabling.");
            Disable();
        }
    }

    /// <summary>
    /// Publish presence whose <see cref="RichPresence.StartTimestampUtc"/> is set to
    /// <c>UtcNow - elapsed</c>, so the platform renders a live "elapsed" timer. Dedupe ignores the
    /// derived timestamp (it changes every call), keying on the rest of the presence instead.
    /// </summary>
    public void SetElapsedPresence(in RichPresence presence, TimeSpan elapsed, bool force = false)
    {
        if (!EnsureReady())
        {
            return;
        }

        TimeSpan clamped = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        RichPresence withTimer = presence with { StartTimestampUtc = DateTime.UtcNow - clamped };

        // Dedupe on everything except the timestamp so we do not spam a per-frame elapsed update,
        // but still republish on the interval so the timer stays live after a reconnect.
        bool contentChanged = !hasLastPresence
            || !(lastPresence with { StartTimestampUtc = null }).Equals(presence with { StartTimestampUtc = null });
        bool stale = DateTime.UtcNow - lastPublishUtc >= options.RepublishInterval;
        if (!force && !contentChanged && !stale)
        {
            return;
        }

        try
        {
            provider.SetPresence(withTimer);
            lastPresence = withTimer;
            hasLastPresence = true;
            lastPublishUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Debug($"social: set-elapsed-presence failed ({ex.GetType().Name}); disabling.");
            Disable();
        }
    }

    /// <summary>Clear the published presence.</summary>
    public void ClearPresence()
    {
        if (!EnsureReady())
        {
            return;
        }

        try
        {
            provider.ClearPresence();
            hasLastPresence = false;
            lastPresence = default;
        }
        catch (Exception ex)
        {
            Log.Debug($"social: clear-presence failed ({ex.GetType().Name}); disabling.");
            Disable();
        }
    }

    /// <summary>Pump the provider. Call once per frame.</summary>
    public void Update()
    {
        if (!EnsureReady())
        {
            return;
        }

        try
        {
            provider.Update();
        }
        catch (Exception ex)
        {
            Log.Debug($"social: update failed ({ex.GetType().Name}); disabling.");
            Disable();
        }
    }

    /// <summary>The local platform identity (e.g. Discord username), once connected.</summary>
    public bool TryGetLocalUser(out SocialUser user)
    {
        user = default;
        if (!EnsureReady())
        {
            return false;
        }

        try
        {
            return provider.TryGetLocalUser(out user);
        }
        catch (Exception ex)
        {
            Log.Debug($"social: get-local-user failed ({ex.GetType().Name}); disabling.");
            Disable();
            user = default;
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        provider.JoinRequested -= OnJoinRequested;
        provider.JoinRequestReceived -= OnJoinRequestReceived;
        SafeDisposeProvider();
    }

    private bool EnsureReady()
    {
        if (disabled || disposed)
        {
            return false;
        }

        if (!initializeAttempted)
        {
            Initialize();
        }

        return enabled && !disabled;
    }

    private void Disable()
    {
        disabled = true;
        enabled = false;
        SafeDisposeProvider();
    }

    private void SafeDisposeProvider()
    {
        try
        {
            provider.Dispose();
        }
        catch
        {
            // Suppress all shutdown transport failures.
        }
    }

    private void OnJoinRequested(string secret) => JoinRequested?.Invoke(secret);
    private void OnJoinRequestReceived(JoinRequest request) => JoinRequestReceived?.Invoke(request);
}
```

Note on `Log.Debug`: confirm the actual API of `KhaozEngine.Diagnostics` before building (Task 2, Step 5). If the logger is not a static `Log.Debug(string)`, adapt to whatever the package exposes (e.g. an injected `ILogger`/`Logger` or `Diagnostics.Log`). Grep: `grep -rn "public static.*Debug\|class Log\b\|interface ILogger" KhaozEngine.Diagnostics/`. Use the same call the neighbouring packages (e.g. `KhaozEngine.Updates`) use.

- [ ] **Step 5: Verify the Diagnostics logging API, adjust the `Log.Debug` calls if needed**

Run: `grep -rn "Debug(" KhaozEngine.Updates/ KhaozEngine.Audio/ | head`
Expected: shows the real logging entry point. Make the controller match it (replace `Log.Debug(...)` throughout if the real API differs). Rebuild `KhaozEngine.Social` to confirm it compiles: `dotnet build KhaozEngine.Social/KhaozEngine.Social.csproj`.

- [ ] **Step 6: Run the controller tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SocialPresenceControllerTests"`
Expected: PASS (10 tests).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Social/SocialPresenceController.cs KhaozEngine.Tests/Social/FakeSocialProvider.cs KhaozEngine.Tests/Social/SocialPresenceControllerTests.cs
git commit -m "social: SocialPresenceController (dedupe, throttle, elapsed timer, session self-disable)"
```

---

### Task 3: Scaffold `KhaozEngine.Social.Discord` package + IPC frame codec

**Files:**
- Create: `KhaozEngine.Social.Discord/KhaozEngine.Social.Discord.csproj`
- Create: `KhaozEngine.Social.Discord/README.md`
- Create: `KhaozEngine.Social.Discord/Internal/DiscordIpcOpcode.cs`
- Create: `KhaozEngine.Social.Discord/Internal/DiscordIpcCodec.cs`
- Test: `KhaozEngine.Tests/Social/DiscordIpcCodecTests.cs`

**Interfaces:**
- Produces: `enum DiscordIpcOpcode`, `static class DiscordIpcCodec` with `byte[] EncodeFrame(DiscordIpcOpcode, string json)` and `bool TryDecodeFrame(ReadOnlySpan<byte>, out DiscordIpcOpcode, out string json, out int consumed)`. Task 6 (`DiscordIpcClient`) consumes these.

- [ ] **Step 1: Create the opcode enum**

`KhaozEngine.Social.Discord/Internal/DiscordIpcOpcode.cs`:
```csharp
namespace KhaozEngine.Social.Discord.Internal;

/// <summary>Discord IPC frame opcodes (little-endian 4-byte header field).</summary>
internal enum DiscordIpcOpcode
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}
```

- [ ] **Step 2: Write the failing codec test**

`KhaozEngine.Tests/Social/DiscordIpcCodecTests.cs`:
```csharp
using System;
using System.Text;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordIpcCodecTests
{
    [Fact]
    public void EncodeFrame_WritesLittleEndianOpcodeLengthThenUtf8Body()
    {
        byte[] frame = DiscordIpcCodec.EncodeFrame(DiscordIpcOpcode.Handshake, "{\"v\":1}");

        // header: opcode (0) then length (7) as little-endian int32
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, frame[0..4]);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, frame[4..8]);
        Assert.Equal("{\"v\":1}", Encoding.UTF8.GetString(frame, 8, 7));
        Assert.Equal(15, frame.Length);
    }

    [Fact]
    public void DecodeFrame_RoundTripsEncode()
    {
        byte[] frame = DiscordIpcCodec.EncodeFrame(DiscordIpcOpcode.Frame, "{\"cmd\":\"SET_ACTIVITY\"}");

        bool ok = DiscordIpcCodec.TryDecodeFrame(frame, out DiscordIpcOpcode op, out string json, out int consumed);

        Assert.True(ok);
        Assert.Equal(DiscordIpcOpcode.Frame, op);
        Assert.Equal("{\"cmd\":\"SET_ACTIVITY\"}", json);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void DecodeFrame_ReturnsFalseWhenHeaderIncomplete()
    {
        bool ok = DiscordIpcCodec.TryDecodeFrame(new byte[] { 1, 0, 0 }, out _, out _, out int consumed);
        Assert.False(ok);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void DecodeFrame_ReturnsFalseWhenBodyIncomplete()
    {
        // header says 10 bytes of body but only 2 present
        byte[] buf = new byte[8 + 2];
        buf[0] = 1;              // opcode
        buf[4] = 10;             // length low byte
        bool ok = DiscordIpcCodec.TryDecodeFrame(buf, out _, out _, out int consumed);
        Assert.False(ok);
        Assert.Equal(0, consumed);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordIpcCodecTests"`
Expected: FAIL - `KhaozEngine.Social.Discord` / `DiscordIpcCodec` not found.

- [ ] **Step 4: Implement the codec**

`KhaozEngine.Social.Discord/Internal/DiscordIpcCodec.cs`:
```csharp
using System;
using System.Buffers.Binary;
using System.Text;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Discord IPC framing: a 4-byte little-endian opcode, a 4-byte little-endian body length, then the
/// UTF-8 JSON body. Pure and allocation-simple so it is fully unit-testable without a live socket.
/// </summary>
internal static class DiscordIpcCodec
{
    public const int HeaderSize = 8;

    /// <summary>Encode one frame (header + UTF-8 body).</summary>
    public static byte[] EncodeFrame(DiscordIpcOpcode opcode, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json ?? string.Empty);
        byte[] frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>
    /// Try to decode one frame from the front of <paramref name="buffer"/>. Returns false (and
    /// consumed=0) if a full header+body is not yet present, so a caller can accumulate more bytes.
    /// </summary>
    public static bool TryDecodeFrame(ReadOnlySpan<byte> buffer, out DiscordIpcOpcode opcode, out string json, out int consumed)
    {
        opcode = default;
        json = string.Empty;
        consumed = 0;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        int op = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(0, 4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
        if (length < 0 || buffer.Length < HeaderSize + length)
        {
            return false;
        }

        opcode = (DiscordIpcOpcode)op;
        json = Encoding.UTF8.GetString(buffer.Slice(HeaderSize, length));
        consumed = HeaderSize + length;
        return true;
    }
}
```

- [ ] **Step 5: Create the backend project file**

`KhaozEngine.Social.Discord/KhaozEngine.Social.Discord.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Social.Discord</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <Description>Discord backend for the KhaozEngine.Social seam. Implements ISocialProvider over Discord Rich Presence with a pure-managed IPC client (Windows named pipe / unix domain socket, JSON framing) - no native libraries, no third-party packages. Rich presence, local identity, and join/invite/ask-to-join. The only Discord-aware assembly; consumers depend on the dependency-free seam and pick this backend explicitly like Netcode.LiteNetLib or WorldStore.Sqlite.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Social/KhaozEngine.Social.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Create a minimal per-package README (expanded in Task 9)**

`KhaozEngine.Social.Discord/README.md`:
```markdown
# KhaozEngine.Social.Discord

Discord backend for the [KhaozEngine.Social](../KhaozEngine.Social) seam. `DiscordSocialProvider`
implements `ISocialProvider` over Discord Rich Presence using a pure-managed IPC client (Windows named
pipe, unix domain socket) - no native libraries, no third-party NuGet. Opt-in: in no umbrella, added
explicitly by a game's client head like `KhaozEngine.Physics.Bepu`. Depends only on `KhaozEngine.Social`.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
```

- [ ] **Step 7: Run the codec tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordIpcCodecTests"`
Expected: PASS (4 tests). (The slnx + Tests csproj references to `.Discord` from Task 1 Steps 8-9 now resolve.)

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Social.Discord KhaozEngine.Tests/Social/DiscordIpcCodecTests.cs
git commit -m "social: KhaozEngine.Social.Discord package + IPC frame codec"
```

---

### Task 4: Discord activity payload mapping + dispatch parsing

**Files:**
- Create: `KhaozEngine.Social.Discord/Internal/DiscordIpcPayloads.cs`
- Test: `KhaozEngine.Tests/Social/DiscordActivityPayloadTests.cs`

**Interfaces:**
- Consumes: `RichPresence`, `SocialUser` (Task 1).
- Produces: `static class DiscordIpcPayloads` with:
  - `string Handshake(string clientId)`
  - `string SetActivity(int pid, in RichPresence presence, string nonce)`
  - `string Subscribe(string evt, string nonce)`
  - `bool TryParseReadyUser(string json, out SocialUser user)`
  - `bool TryParseDispatch(string json, out string eventName, out string dataJson)`
  - `bool TryParseJoinSecret(string dataJson, out string secret)`
  - `bool TryParseJoinRequestUser(string dataJson, out SocialUser user)`

  Task 6 (`DiscordIpcClient`) consumes these.

- [ ] **Step 1: Write the failing payload tests**

`KhaozEngine.Tests/Social/DiscordActivityPayloadTests.cs`:
```csharp
using System;
using System.Text.Json;
using KhaozEngine.Social;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordActivityPayloadTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Handshake_CarriesVersionAndClientId()
    {
        JsonElement root = Root(DiscordIpcPayloads.Handshake("12345"));
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("12345", root.GetProperty("client_id").GetString());
    }

    [Fact]
    public void SetActivity_MapsDetailsStateTimestampParty()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var presence = new RichPresence
        {
            Details = "In the overworld",
            State = "Solo",
            StartTimestampUtc = start,
            Party = new PresenceParty("party-1", 2, 4),
            LargeImage = new PresenceImage("map_forest", "Forest"),
            JoinSecret = "join-abc",
        };

        JsonElement root = Root(DiscordIpcPayloads.SetActivity(4242, presence, "nonce-1"));
        Assert.Equal("SET_ACTIVITY", root.GetProperty("cmd").GetString());
        Assert.Equal("nonce-1", root.GetProperty("nonce").GetString());

        JsonElement args = root.GetProperty("args");
        Assert.Equal(4242, args.GetProperty("pid").GetInt32());

        JsonElement activity = args.GetProperty("activity");
        Assert.Equal("In the overworld", activity.GetProperty("details").GetString());
        Assert.Equal("Solo", activity.GetProperty("state").GetString());

        long expectedUnix = ((DateTimeOffset)start).ToUnixTimeSeconds();
        Assert.Equal(expectedUnix, activity.GetProperty("timestamps").GetProperty("start").GetInt64());

        JsonElement party = activity.GetProperty("party");
        Assert.Equal("party-1", party.GetProperty("id").GetString());
        Assert.Equal(2, party.GetProperty("size")[0].GetInt32());
        Assert.Equal(4, party.GetProperty("size")[1].GetInt32());

        Assert.Equal("map_forest", activity.GetProperty("assets").GetProperty("large_image").GetString());
        Assert.Equal("join-abc", activity.GetProperty("secrets").GetProperty("join").GetString());
    }

    [Fact]
    public void SetActivity_OmitsEmptyFields()
    {
        JsonElement activity = Root(DiscordIpcPayloads.SetActivity(1, new RichPresence { Details = "x" }, "n"))
            .GetProperty("args").GetProperty("activity");
        Assert.False(activity.TryGetProperty("timestamps", out _));
        Assert.False(activity.TryGetProperty("party", out _));
        Assert.False(activity.TryGetProperty("assets", out _));
        Assert.False(activity.TryGetProperty("secrets", out _));
    }

    [Fact]
    public void TryParseReadyUser_ExtractsUser()
    {
        string json = """
        {"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"77","username":"kiwi","global_name":"Kiwi"}}}
        """;
        Assert.True(DiscordIpcPayloads.TryParseReadyUser(json, out SocialUser user));
        Assert.Equal("77", user.Id);
        Assert.Equal("kiwi", user.Username);
        Assert.Equal("Kiwi", user.GlobalName);
    }

    [Fact]
    public void TryParseDispatch_SplitsEventAndData()
    {
        string json = """{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{"secret":"s-1"}}""";
        Assert.True(DiscordIpcPayloads.TryParseDispatch(json, out string evt, out string data));
        Assert.Equal("ACTIVITY_JOIN", evt);
        Assert.True(DiscordIpcPayloads.TryParseJoinSecret(data, out string secret));
        Assert.Equal("s-1", secret);
    }

    [Fact]
    public void TryParseJoinRequestUser_ExtractsUser()
    {
        string data = """{"user":{"id":"9","username":"ally","global_name":null}}""";
        Assert.True(DiscordIpcPayloads.TryParseJoinRequestUser(data, out SocialUser user));
        Assert.Equal("9", user.Id);
        Assert.Equal("ally", user.Username);
        Assert.Null(user.GlobalName);
    }

    [Fact]
    public void TryParseReadyUser_ReturnsFalseOnGarbage()
    {
        Assert.False(DiscordIpcPayloads.TryParseReadyUser("not json", out _));
        Assert.False(DiscordIpcPayloads.TryParseReadyUser("{}", out _));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordActivityPayloadTests"`
Expected: FAIL - `DiscordIpcPayloads` not found.

- [ ] **Step 3: Implement the payloads (System.Text.Json)**

`KhaozEngine.Social.Discord/Internal/DiscordIpcPayloads.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.Social;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Builds the JSON command bodies the Discord IPC socket expects and parses the dispatches it sends
/// back. Uses System.Text.Json only (no third-party JSON). All parsing is defensive: malformed or
/// unexpected payloads return false rather than throwing.
/// </summary>
internal static class DiscordIpcPayloads
{
    public static string Handshake(string clientId)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("client_id", clientId ?? string.Empty);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Subscribe(string evt, string nonce)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("cmd", "SUBSCRIBE");
            w.WriteString("evt", evt);
            w.WriteString("nonce", nonce);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SetActivity(int pid, in RichPresence presence, string nonce)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("cmd", "SET_ACTIVITY");
            w.WriteString("nonce", nonce);

            w.WriteStartObject("args");
            w.WriteNumber("pid", pid);

            w.WriteStartObject("activity");
            WriteIfPresent(w, "details", presence.Details);
            WriteIfPresent(w, "state", presence.State);

            if (presence.StartTimestampUtc is { } start || presence.EndTimestampUtc is { } end0)
            {
                w.WriteStartObject("timestamps");
                if (presence.StartTimestampUtc is { } s)
                {
                    w.WriteNumber("start", ((DateTimeOffset)DateTime.SpecifyKind(s, DateTimeKind.Utc)).ToUnixTimeSeconds());
                }

                if (presence.EndTimestampUtc is { } e)
                {
                    w.WriteNumber("end", ((DateTimeOffset)DateTime.SpecifyKind(e, DateTimeKind.Utc)).ToUnixTimeSeconds());
                }

                w.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(presence.Party.Id) || presence.Party.Max > 0)
            {
                w.WriteStartObject("party");
                WriteIfPresent(w, "id", presence.Party.Id);
                if (presence.Party.Max > 0)
                {
                    w.WriteStartArray("size");
                    w.WriteNumberValue(presence.Party.Size);
                    w.WriteNumberValue(presence.Party.Max);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            if (HasImage(presence.LargeImage) || HasImage(presence.SmallImage))
            {
                w.WriteStartObject("assets");
                WriteIfPresent(w, "large_image", presence.LargeImage.Key);
                WriteIfPresent(w, "large_text", presence.LargeImage.Text);
                WriteIfPresent(w, "small_image", presence.SmallImage.Key);
                WriteIfPresent(w, "small_text", presence.SmallImage.Text);
                w.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(presence.JoinSecret) || !string.IsNullOrEmpty(presence.SpectateSecret))
            {
                w.WriteStartObject("secrets");
                WriteIfPresent(w, "join", presence.JoinSecret);
                WriteIfPresent(w, "spectate", presence.SpectateSecret);
                w.WriteEndObject();
            }

            if (presence.Buttons is { Count: > 0 } buttons)
            {
                w.WriteStartArray("buttons");
                int count = 0;
                foreach (PresenceButton b in buttons)
                {
                    if (count++ == 2)
                    {
                        break; // Discord allows at most two.
                    }

                    w.WriteStartObject();
                    w.WriteString("label", b.Label);
                    w.WriteString("url", b.Url);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject(); // activity
            w.WriteEndObject(); // args
            w.WriteEndObject(); // root
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryParseDispatch(string json, out string eventName, out string dataJson)
    {
        eventName = string.Empty;
        dataJson = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("evt", out JsonElement evt) || evt.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            eventName = evt.GetString() ?? string.Empty;
            dataJson = root.TryGetProperty("data", out JsonElement data) ? data.GetRawText() : "{}";
            return eventName.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseReadyUser(string json, out SocialUser user)
    {
        user = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("user", out JsonElement u))
            {
                return TryReadUser(u, out user);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseJoinRequestUser(string dataJson, out SocialUser user)
    {
        user = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.TryGetProperty("user", out JsonElement u))
            {
                return TryReadUser(u, out user);
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseJoinSecret(string dataJson, out string secret)
    {
        secret = string.Empty;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.TryGetProperty("secret", out JsonElement s) && s.ValueKind == JsonValueKind.String)
            {
                secret = s.GetString() ?? string.Empty;
                return secret.Length > 0;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadUser(JsonElement u, out SocialUser user)
    {
        user = default;
        if (!u.TryGetProperty("id", out JsonElement id) || !u.TryGetProperty("username", out JsonElement name))
        {
            return false;
        }

        string? global = u.TryGetProperty("global_name", out JsonElement g) && g.ValueKind == JsonValueKind.String
            ? g.GetString()
            : null;
        user = new SocialUser(id.GetString() ?? string.Empty, name.GetString() ?? string.Empty, global);
        return user.Id.Length > 0 && user.Username.Length > 0;
    }

    private static void WriteIfPresent(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            w.WriteString(name, value);
        }
    }

    private static bool HasImage(PresenceImage image) =>
        !string.IsNullOrEmpty(image.Key) || !string.IsNullOrEmpty(image.Text);
}
```

- [ ] **Step 4: Run the payload tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordActivityPayloadTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Social.Discord/Internal/DiscordIpcPayloads.cs KhaozEngine.Tests/Social/DiscordActivityPayloadTests.cs
git commit -m "social: Discord activity payload mapping + dispatch parsing (STJ)"
```

---

### Task 5: Unix socket path discovery

**Files:**
- Create: `KhaozEngine.Social.Discord/Internal/DiscordSocketPaths.cs`
- Test: `KhaozEngine.Tests/Social/DiscordSocketPathsTests.cs`

**Interfaces:**
- Produces: `static class DiscordSocketPaths` with `IEnumerable<string> UnixCandidates(int index, Func<string, string?> getEnv)` producing the ordered candidate unix socket paths for `discord-ipc-<index>` across `$XDG_RUNTIME_DIR`, `$TMPDIR`, `$TMP`, `$TEMP`, `/tmp`, plus Flatpak/Snap sandbox subdirs. Task 7 (`NamedPipeDiscordTransport`) consumes it.

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Social/DiscordSocketPathsTests.cs`:
```csharp
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordSocketPathsTests
{
    [Fact]
    public void UnixCandidates_UsesXdgRuntimeDirFirst_ThenTmpFallbacks()
    {
        var env = new Dictionary<string, string?>
        {
            ["XDG_RUNTIME_DIR"] = "/run/user/1000",
            ["TMPDIR"] = "/var/tmp-mac",
        };
        List<string> paths = DiscordSocketPaths.UnixCandidates(0, k => env.TryGetValue(k, out var v) ? v : null).ToList();

        Assert.Equal("/run/user/1000/discord-ipc-0", paths[0]);
        Assert.Contains("/var/tmp-mac/discord-ipc-0", paths);
        Assert.Contains("/tmp/discord-ipc-0", paths);
        // sandbox subdirs are derived from each base
        Assert.Contains("/run/user/1000/app/com.discordapp.Discord/discord-ipc-0", paths);
        Assert.Contains("/run/user/1000/snap.discord/discord-ipc-0", paths);
    }

    [Fact]
    public void UnixCandidates_HonorsIndex()
    {
        var env = new Dictionary<string, string?> { ["XDG_RUNTIME_DIR"] = "/run/user/1000" };
        List<string> paths = DiscordSocketPaths.UnixCandidates(3, k => env.TryGetValue(k, out var v) ? v : null).ToList();
        Assert.Equal("/run/user/1000/discord-ipc-3", paths[0]);
    }

    [Fact]
    public void UnixCandidates_SkipsMissingEnvVars_AlwaysIncludesTmp()
    {
        List<string> paths = DiscordSocketPaths.UnixCandidates(0, _ => null).ToList();
        Assert.Contains("/tmp/discord-ipc-0", paths);
        Assert.DoesNotContain(paths, p => p.StartsWith("/discord-ipc")); // no empty-base garbage
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordSocketPathsTests"`
Expected: FAIL - `DiscordSocketPaths` not found.

- [ ] **Step 3: Implement path discovery**

`KhaozEngine.Social.Discord/Internal/DiscordSocketPaths.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Enumerates the candidate unix-domain-socket paths Discord may expose <c>discord-ipc-N</c> on. On
/// macOS/Linux the Discord client puts the socket under a runtime/temp dir; sandboxed installs
/// (Flatpak, Snap) nest it one level deeper. Windows does not use this (it connects to the named pipe
/// <c>discord-ipc-N</c> directly).
/// </summary>
internal static class DiscordSocketPaths
{
    private static readonly string[] EnvBases = { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" };
    private static readonly string[] SandboxSubdirs =
    {
        "app/com.discordapp.Discord",
        "snap.discord",
    };

    public static IEnumerable<string> UnixCandidates(int index, Func<string, string?> getEnv)
    {
        string socket = $"discord-ipc-{index}";
        var seen = new HashSet<string>();
        var bases = new List<string>();

        foreach (string key in EnvBases)
        {
            string? value = getEnv(key);
            if (!string.IsNullOrEmpty(value))
            {
                bases.Add(TrimTrailingSlash(value));
            }
        }

        bases.Add("/tmp");

        foreach (string b in bases)
        {
            foreach (string path in Expand(b, socket))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> Expand(string baseDir, string socket)
    {
        yield return $"{baseDir}/{socket}";
        foreach (string sub in SandboxSubdirs)
        {
            yield return $"{baseDir}/{sub}/{socket}";
        }
    }

    private static string TrimTrailingSlash(string p) => p.Length > 1 && p.EndsWith('/') ? p[..^1] : p;
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordSocketPathsTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Social.Discord/Internal/DiscordSocketPaths.cs KhaozEngine.Tests/Social/DiscordSocketPathsTests.cs
git commit -m "social: Discord unix socket path discovery (xdg/tmp + flatpak/snap)"
```

---

### Task 6: IPC client (transport seam + handshake + dispatch pump)

**Files:**
- Create: `KhaozEngine.Social.Discord/Internal/IDiscordIpcTransport.cs`
- Create: `KhaozEngine.Social.Discord/Internal/DiscordIpcClient.cs`
- Create: `KhaozEngine.Tests/Social/FakeDiscordIpcTransport.cs`
- Test: `KhaozEngine.Tests/Social/DiscordIpcClientTests.cs`

**Interfaces:**
- Consumes: `DiscordIpcCodec`, `DiscordIpcOpcode`, `DiscordIpcPayloads` (Tasks 3-4).
- Produces:
  - `interface IDiscordIpcTransport : IDisposable` - `bool TryConnect()`, `bool IsConnected`, `void Write(ReadOnlySpan<byte>)`, `int Read(Span<byte>)` (0 = closed/no data).
  - `sealed class DiscordIpcClient` - `bool TryConnect(string clientId)`, `bool IsConnected`, `SocialUser? LocalUser`, `void SetActivity(in RichPresence)`, `void ClearActivity()`, `void Pump()`, events `Action<string> JoinSecretReceived`, `Action<SocialUser> JoinRequestUserReceived`, `IDisposable`. Task 7 (`DiscordSocialProvider`) consumes it.

- [ ] **Step 1: Create the transport seam interface**

`KhaozEngine.Social.Discord/Internal/IDiscordIpcTransport.cs`:
```csharp
using System;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// The raw byte transport under <see cref="DiscordIpcClient"/>: connect to the Discord socket, and
/// non-blocking read/write. Abstracted so the client's handshake + framing logic is unit-testable with
/// an in-memory fake and the real named-pipe / unix-socket IO lives in one small class.
/// </summary>
internal interface IDiscordIpcTransport : IDisposable
{
    bool IsConnected { get; }

    /// <summary>Attempt to connect to a running Discord client. Returns false if none is reachable.</summary>
    bool TryConnect();

    /// <summary>Write all bytes. May throw on a broken pipe; the caller treats a throw as disconnect.</summary>
    void Write(ReadOnlySpan<byte> bytes);

    /// <summary>Read available bytes into <paramref name="buffer"/>; returns 0 when nothing is available or the pipe closed.</summary>
    int Read(Span<byte> buffer);
}
```

- [ ] **Step 2: Create the in-memory fake transport**

`KhaozEngine.Tests/Social/FakeDiscordIpcTransport.cs`:
```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Social.Discord.Internal;

namespace KhaozEngine.Tests;

/// <summary>In-memory <see cref="IDiscordIpcTransport"/>: captures writes, replays queued reads.</summary>
internal sealed class FakeDiscordIpcTransport : IDiscordIpcTransport
{
    private readonly Queue<byte[]> incoming = new();
    private readonly List<byte> written = new();

    public bool ConnectResult { get; set; } = true;
    public bool IsConnected { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool ThrowOnWrite { get; set; }

    public IReadOnlyList<byte> Written => written;

    public bool TryConnect()
    {
        IsConnected = ConnectResult;
        return ConnectResult;
    }

    /// <summary>Queue a full frame the next Read(s) will surface.</summary>
    public void EnqueueFrame(DiscordIpcOpcode opcode, string json)
        => incoming.Enqueue(DiscordIpcCodec.EncodeFrame(opcode, json));

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (ThrowOnWrite)
        {
            throw new System.IO.IOException("broken pipe");
        }

        written.AddRange(bytes.ToArray());
    }

    public int Read(Span<byte> buffer)
    {
        if (incoming.Count == 0)
        {
            return 0;
        }

        byte[] next = incoming.Peek();
        if (next.Length > buffer.Length)
        {
            return 0; // caller must pass a big-enough buffer in these tests
        }

        incoming.Dequeue();
        next.CopyTo(buffer);
        return next.Length;
    }

    /// <summary>Helper: decode the last written frame for assertions.</summary>
    public bool TryReadLastWrittenFrame(out DiscordIpcOpcode opcode, out string json)
    {
        return DiscordIpcCodec.TryDecodeFrame(written.ToArray(), out opcode, out json, out _)
            ? true
            : DecodeLast(out opcode, out json);
    }

    private bool DecodeLast(out DiscordIpcOpcode opcode, out string json)
    {
        opcode = default;
        json = string.Empty;
        ReadOnlySpan<byte> span = written.ToArray();
        bool any = false;
        while (DiscordIpcCodec.TryDecodeFrame(span, out DiscordIpcOpcode op, out string body, out int consumed))
        {
            opcode = op;
            json = body;
            any = true;
            span = span.Slice(consumed);
        }

        return any;
    }

    public void Dispose() => DisposeCalls++;
}
```

- [ ] **Step 3: Write the failing client tests**

`KhaozEngine.Tests/Social/DiscordIpcClientTests.cs`:
```csharp
using KhaozEngine.Social;
using KhaozEngine.Social.Discord.Internal;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordIpcClientTests
{
    private static DiscordIpcClient Connected(FakeDiscordIpcTransport transport)
    {
        var client = new DiscordIpcClient(transport);
        Assert.True(client.TryConnect("app-1"));
        return client;
    }

    [Fact]
    public void TryConnect_SendsHandshakeFrame()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);

        Assert.True(transport.TryReadLastWrittenFrame(out DiscordIpcOpcode op, out _));
        // first frame written is the handshake
        Assert.True(DiscordIpcCodec.TryDecodeFrame(
            System.Linq.Enumerable.ToArray(transport.Written), out DiscordIpcOpcode first, out string firstJson, out _));
        Assert.Equal(DiscordIpcOpcode.Handshake, first);
        Assert.Contains("app-1", firstJson);
    }

    [Fact]
    public void TryConnect_ReturnsFalseWhenTransportCannotConnect()
    {
        var transport = new FakeDiscordIpcTransport { ConnectResult = false };
        var client = new DiscordIpcClient(transport);
        Assert.False(client.TryConnect("app-1"));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Pump_ReadyDispatch_SetsLocalUser()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"5","username":"kiwi","global_name":"Kiwi"}}}""");

        client.Pump();

        Assert.NotNull(client.LocalUser);
        Assert.Equal("kiwi", client.LocalUser!.Value.Username);
    }

    [Fact]
    public void Pump_ActivityJoin_RaisesJoinSecret()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        string? secret = null;
        client.JoinSecretReceived += s => secret = s;
        transport.EnqueueFrame(DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{"secret":"join-xyz"}}""");

        client.Pump();

        Assert.Equal("join-xyz", secret);
    }

    [Fact]
    public void SetActivity_WritesSetActivityFrame()
    {
        var transport = new FakeDiscordIpcTransport();
        using DiscordIpcClient client = Connected(transport);
        client.SetActivity(new RichPresence { Details = "In Game", State = "Solo" });

        Assert.True(transport.TryReadLastWrittenFrame(out DiscordIpcOpcode op, out string json));
        Assert.Equal(DiscordIpcOpcode.Frame, op);
        Assert.Contains("SET_ACTIVITY", json);
        Assert.Contains("In Game", json);
    }

    [Fact]
    public void WriteFailure_MarksDisconnected()
    {
        var transport = new FakeDiscordIpcTransport { ThrowOnWrite = true };
        var client = new DiscordIpcClient(transport);
        // handshake write throws -> connect fails cleanly
        Assert.False(client.TryConnect("app-1"));
        Assert.False(client.IsConnected);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordIpcClientTests"`
Expected: FAIL - `DiscordIpcClient` not found.

- [ ] **Step 5: Implement the client**

`KhaozEngine.Social.Discord/Internal/DiscordIpcClient.cs`:
```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Social;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Speaks the Discord IPC protocol over an <see cref="IDiscordIpcTransport"/>: handshake, SET_ACTIVITY,
/// SUBSCRIBE to join events, and a non-blocking dispatch pump. Every socket operation is wrapped so a
/// failure flips the client to disconnected rather than throwing. Pure protocol logic; the real socket
/// lives in <see cref="NamedPipeDiscordTransport"/>.
/// </summary>
internal sealed class DiscordIpcClient : IDisposable
{
    private readonly IDiscordIpcTransport transport;
    private readonly int pid;
    private readonly List<byte> readBuffer = new();
    private int nonce;

    public DiscordIpcClient(IDiscordIpcTransport transport, int? pid = null)
    {
        this.transport = transport;
        this.pid = pid ?? System.Environment.ProcessId;
    }

    public bool IsConnected { get; private set; }
    public SocialUser? LocalUser { get; private set; }

    public event Action<string>? JoinSecretReceived;
    public event Action<SocialUser>? JoinRequestUserReceived;

    public bool TryConnect(string clientId)
    {
        try
        {
            if (!transport.TryConnect())
            {
                return false;
            }

            WriteFrame(DiscordIpcOpcode.Handshake, DiscordIpcPayloads.Handshake(clientId));
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.Subscribe("ACTIVITY_JOIN", NextNonce()));
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.Subscribe("ACTIVITY_JOIN_REQUEST", NextNonce()));
            IsConnected = true;
            return true;
        }
        catch (Exception)
        {
            Disconnect();
            return false;
        }
    }

    public void SetActivity(in RichPresence presence)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.SetActivity(pid, presence, NextNonce()));
        }
        catch (Exception)
        {
            Disconnect();
        }
    }

    public void ClearActivity()
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            // SET_ACTIVITY with an empty activity clears presence.
            WriteFrame(DiscordIpcOpcode.Frame, DiscordIpcPayloads.SetActivity(pid, default, NextNonce()));
        }
        catch (Exception)
        {
            Disconnect();
        }
    }

    public void Pump()
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            DrainReads();
            ProcessFrames();
        }
        catch (Exception)
        {
            Disconnect();
        }
    }

    private void DrainReads()
    {
        Span<byte> chunk = stackalloc byte[4096];
        int read;
        while ((read = transport.Read(chunk)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                readBuffer.Add(chunk[i]);
            }
        }
    }

    private void ProcessFrames()
    {
        while (DiscordIpcCodec.TryDecodeFrame(readBuffer.ToArray(), out DiscordIpcOpcode op, out string json, out int consumed))
        {
            readBuffer.RemoveRange(0, consumed);
            HandleFrame(op, json);
        }
    }

    private void HandleFrame(DiscordIpcOpcode op, string json)
    {
        if (op == DiscordIpcOpcode.Close)
        {
            Disconnect();
            return;
        }

        if (op == DiscordIpcOpcode.Ping)
        {
            WriteFrame(DiscordIpcOpcode.Pong, json);
            return;
        }

        if (op != DiscordIpcOpcode.Frame)
        {
            return;
        }

        if (!DiscordIpcPayloads.TryParseDispatch(json, out string evt, out string data))
        {
            return;
        }

        switch (evt)
        {
            case "READY":
                if (DiscordIpcPayloads.TryParseReadyUser(json, out SocialUser user))
                {
                    LocalUser = user;
                }

                break;
            case "ACTIVITY_JOIN":
                if (DiscordIpcPayloads.TryParseJoinSecret(data, out string secret))
                {
                    JoinSecretReceived?.Invoke(secret);
                }

                break;
            case "ACTIVITY_JOIN_REQUEST":
                if (DiscordIpcPayloads.TryParseJoinRequestUser(data, out SocialUser requester))
                {
                    JoinRequestUserReceived?.Invoke(requester);
                }

                break;
        }
    }

    private void WriteFrame(DiscordIpcOpcode op, string json) => transport.Write(DiscordIpcCodec.EncodeFrame(op, json));

    private string NextNonce() => (++nonce).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void Disconnect()
    {
        IsConnected = false;
        LocalUser = null;
    }

    public void Dispose()
    {
        Disconnect();
        transport.Dispose();
    }
}
```

- [ ] **Step 6: Run the client tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordIpcClientTests"`
Expected: PASS (6 tests).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Social.Discord/Internal/IDiscordIpcTransport.cs KhaozEngine.Social.Discord/Internal/DiscordIpcClient.cs KhaozEngine.Tests/Social/FakeDiscordIpcTransport.cs KhaozEngine.Tests/Social/DiscordIpcClientTests.cs
git commit -m "social: Discord IPC client (transport seam, handshake, dispatch pump)"
```

---

### Task 7: Real transport + `DiscordSocialProvider`

**Files:**
- Create: `KhaozEngine.Social.Discord/Internal/NamedPipeDiscordTransport.cs`
- Create: `KhaozEngine.Social.Discord/DiscordSocialOptions.cs`
- Create: `KhaozEngine.Social.Discord/DiscordSocialProvider.cs`
- Test: `KhaozEngine.Tests/Social/DiscordSocialProviderTests.cs`
- Test: `KhaozEngine.Tests/Social/DiscordLiveSocketTests.cs`

**Interfaces:**
- Consumes: `DiscordIpcClient`, `IDiscordIpcTransport`, `DiscordSocketPaths` (Tasks 5-6); `ISocialProvider`, `RichPresence`, `SocialUser`, `JoinRequest` (Task 1).
- Produces: `sealed class DiscordSocialProvider : ISocialProvider` with a public ctor `DiscordSocialProvider(DiscordSocialOptions? options = null)` and an internal test ctor `DiscordSocialProvider(IDiscordIpcTransport transport)`; `sealed class DiscordSocialOptions { string ApplicationId; }`. This is the type SpaceGame's Desktop head news up.

- [ ] **Step 1: Implement the real transport (platform split)**

`KhaozEngine.Social.Discord/Internal/NamedPipeDiscordTransport.cs`:
```csharp
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Real Discord IPC transport. Windows connects to the named pipe <c>discord-ipc-N</c>; macOS/Linux
/// connect to the unix domain socket Discord exposes under a runtime/temp dir (via
/// <see cref="DiscordSocketPaths"/>) - .NET's NamedPipeClientStream on unix maps to
/// <c>/tmp/CoreFxPipe_*</c>, which is NOT Discord's path, so a raw Socket is used there instead. Tries
/// indices 0..9. Non-blocking reads.
/// </summary>
internal sealed class NamedPipeDiscordTransport : IDiscordIpcTransport
{
    private NamedPipeClientStream? pipe;   // Windows
    private Socket? socket;                 // unix

    public bool IsConnected => pipe is { IsConnected: true } || socket is { Connected: true };

    public bool TryConnect()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OperatingSystem.IsWindows())
            {
                if (TryConnectWindows(i))
                {
                    return true;
                }
            }
            else if (TryConnectUnix(i))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryConnectWindows(int index)
    {
        try
        {
            var client = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(100);
            pipe = client;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryConnectUnix(int index)
    {
        foreach (string path in DiscordSocketPaths.UnixCandidates(index, Environment.GetEnvironmentVariable))
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.Connect(new UnixDomainSocketEndPoint(path));
                s.Blocking = false;
                socket = s;
                return true;
            }
            catch (Exception)
            {
                // try next candidate
            }
        }

        return false;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (pipe is { } p)
        {
            p.Write(bytes);
            p.Flush();
        }
        else if (socket is { } s)
        {
            int sent = 0;
            byte[] tmp = bytes.ToArray();
            while (sent < tmp.Length)
            {
                sent += s.Send(tmp, sent, tmp.Length - sent, SocketFlags.None);
            }
        }
    }

    public int Read(Span<byte> buffer)
    {
        try
        {
            if (pipe is { } p)
            {
                // NamedPipeClientStream has no non-blocking mode; only read when data is buffered.
                return p.IsConnected && p.InBufferSize >= 0 && HasData(p) ? p.Read(buffer) : 0;
            }

            if (socket is { } s)
            {
                if (s.Available == 0)
                {
                    return 0;
                }

                byte[] tmp = new byte[buffer.Length];
                int n = s.Receive(tmp, 0, buffer.Length, SocketFlags.None);
                tmp.AsSpan(0, n).CopyTo(buffer);
                return n;
            }
        }
        catch (Exception)
        {
            return 0;
        }

        return 0;
    }

    private static bool HasData(NamedPipeClientStream p)
    {
        // A best-effort peek: NamedPipeClientStream in message/byte mode blocks on Read with no data.
        // We only call Read when the OS reports readable bytes via a 0-timeout poll on the safe handle.
        try
        {
            return p.CanRead && p.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { pipe?.Dispose(); } catch { /* ignore */ }
        try { socket?.Dispose(); } catch { /* ignore */ }
        pipe = null;
        socket = null;
    }
}
```

Implementer note: the Windows non-blocking read is the one platform subtlety. `NamedPipeClientStream.Read` blocks when no data is buffered, which would stall the game loop in `Pump()`. Before finalizing, verify the read path does not block on Windows: either (a) open the pipe with `PipeOptions.Asynchronous` and use `ReadAsync` with an already-completed check, or (b) use the pipe's `SafePipeHandle` with a native 0-timeout `PeekNamedPipe` to gate the `Read`. If a clean non-blocking peek is not achievable in the time budget, run the pump read on a short-lived background read loop that appends into a lock-protected buffer the `Pump()` drains (the client already tolerates chunked reads). Keep whichever approach behind this transport class so `DiscordIpcClient` stays unchanged. This is tested live in Step 6, not in unit tests.

- [ ] **Step 2: Create the options type**

`KhaozEngine.Social.Discord/DiscordSocialOptions.cs`:
```csharp
namespace KhaozEngine.Social.Discord;

/// <summary>Configuration for <see cref="DiscordSocialProvider"/>.</summary>
public sealed class DiscordSocialOptions
{
    /// <summary>The game's Discord Application (client) id. Required; presence is a no-op without it.</summary>
    public string ApplicationId { get; init; } = string.Empty;
}
```

- [ ] **Step 3: Write the failing provider tests**

`KhaozEngine.Tests/Social/DiscordSocialProviderTests.cs`:
```csharp
using KhaozEngine.Social;
using KhaozEngine.Social.Discord;
using Xunit;

namespace KhaozEngine.Tests;

public class DiscordSocialProviderTests
{
    [Fact]
    public void Initialize_ConnectsAndReportsConnected()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        Assert.True(provider.TryInitialize("app-1"));
        Assert.True(provider.IsConnected);
    }

    [Fact]
    public void SetPresence_WritesActivity()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        provider.SetPresence(new RichPresence { Details = "In Game" });

        Assert.True(transport.TryReadLastWrittenFrame(out _, out string json));
        Assert.Contains("SET_ACTIVITY", json);
        Assert.Contains("In Game", json);
    }

    [Fact]
    public void Update_ReadyDispatch_ExposesLocalUser()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        transport.EnqueueFrame(KhaozEngine.Social.Discord.Internal.DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"READY","data":{"user":{"id":"5","username":"kiwi","global_name":null}}}""");

        provider.Update();

        Assert.True(provider.TryGetLocalUser(out SocialUser user));
        Assert.Equal("kiwi", user.Username);
    }

    [Fact]
    public void Update_ActivityJoin_RaisesJoinRequested()
    {
        var transport = new FakeDiscordIpcTransport();
        using var provider = new DiscordSocialProvider(transport);
        provider.TryInitialize("app-1");
        string? secret = null;
        provider.JoinRequested += s => secret = s;
        transport.EnqueueFrame(KhaozEngine.Social.Discord.Internal.DiscordIpcOpcode.Frame,
            """{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{"secret":"j-1"}}""");

        provider.Update();

        Assert.Equal("j-1", secret);
    }

    [Fact]
    public void FailedConnect_IsNotConnected_AndNeverThrows()
    {
        var transport = new FakeDiscordIpcTransport { ConnectResult = false };
        using var provider = new DiscordSocialProvider(transport);
        Assert.False(provider.TryInitialize("app-1"));
        provider.SetPresence(new RichPresence { Details = "x" });
        provider.Update();
        Assert.False(provider.IsConnected);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordSocialProviderTests"`
Expected: FAIL - `DiscordSocialProvider` not found.

- [ ] **Step 5: Implement the provider**

`KhaozEngine.Social.Discord/DiscordSocialProvider.cs`:
```csharp
using System;
using KhaozEngine.Social;
using KhaozEngine.Social.Discord.Internal;

namespace KhaozEngine.Social.Discord;

/// <summary>
/// Discord Rich Presence <see cref="ISocialProvider"/>: a pure-managed IPC client (no native libs, no
/// third-party packages). Rich presence, local identity, and join/invite. Every operation is
/// best-effort - a Discord failure degrades to disconnected and never throws into the game.
/// </summary>
public sealed class DiscordSocialProvider : ISocialProvider
{
    private readonly string applicationIdFromOptions;
    private readonly DiscordIpcClient client;
    private string applicationId = string.Empty;

    /// <summary>Production ctor: real named-pipe / unix-socket transport.</summary>
    public DiscordSocialProvider(DiscordSocialOptions? options = null)
        : this(new NamedPipeDiscordTransport())
    {
        applicationIdFromOptions = options?.ApplicationId ?? string.Empty;
    }

    /// <summary>Test/custom-transport ctor.</summary>
    internal DiscordSocialProvider(IDiscordIpcTransport transport)
    {
        applicationIdFromOptions = string.Empty;
        client = new DiscordIpcClient(transport);
        client.JoinSecretReceived += OnJoinSecret;
        client.JoinRequestUserReceived += OnJoinRequestUser;
    }

    public bool IsConnected => client.IsConnected;

    public event Action<string>? JoinRequested;
    public event Action<JoinRequest>? JoinRequestReceived;

    public bool TryInitialize(string applicationId)
    {
        this.applicationId = string.IsNullOrEmpty(applicationId) ? applicationIdFromOptions : applicationId;
        if (string.IsNullOrEmpty(this.applicationId))
        {
            return false;
        }

        return client.TryConnect(this.applicationId);
    }

    public void Update() => client.Pump();

    public void SetPresence(in RichPresence presence) => client.SetActivity(presence);

    public void ClearPresence() => client.ClearActivity();

    public bool TryGetLocalUser(out SocialUser user)
    {
        if (client.LocalUser is { } u)
        {
            user = u;
            return true;
        }

        user = default;
        return false;
    }

    private void OnJoinSecret(string secret) => JoinRequested?.Invoke(secret);

    private void OnJoinRequestUser(SocialUser requester)
    {
        // The engine cannot answer ask-to-join over the current IPC subset, so the request is surfaced
        // for the game; Accept/Reject is a no-op respond callback until a reply path is added.
        JoinRequestReceived?.Invoke(new JoinRequest(requester, respond: null));
    }

    public void Dispose()
    {
        client.JoinSecretReceived -= OnJoinSecret;
        client.JoinRequestUserReceived -= OnJoinRequestUser;
        client.Dispose();
    }
}
```

Note: the production ctor delegates to the internal ctor for the transport but must also keep `applicationIdFromOptions`. Because C# runs the delegated ctor body after the target ctor, set `applicationIdFromOptions` in the internal ctor from a parameter instead if the executor hits an assignment-order issue. Simplest robust form: give the internal ctor an optional `string optionsAppId = ""` param and pass it from the public ctor (`: this(new NamedPipeDiscordTransport(), options?.ApplicationId ?? "")`). Adjust if the compiler flags the readonly double-assignment.

- [ ] **Step 6: Add the live-socket smoke test (excluded from CI)**

`KhaozEngine.Tests/Social/DiscordLiveSocketTests.cs`:
```csharp
using KhaozEngine.Social;
using KhaozEngine.Social.Discord;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Exercises the real Discord socket. Requires a running Discord client and a valid Application id, so
/// it is tagged LiveSocket and excluded from CI (`--filter "Category!=LiveSocket"`). Run manually with
/// a real app id to smoke-test presence on a dev machine.
/// </summary>
[Trait("Category", "LiveSocket")]
public class DiscordLiveSocketTests
{
    [Fact]
    public void ConnectsToLocalDiscordAndSetsPresence()
    {
        using var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "1478493292369936527" });
        bool connected = provider.TryInitialize(string.Empty);
        if (!connected)
        {
            return; // No Discord running: treat as inconclusive, not a failure.
        }

        provider.SetPresence(new RichPresence { Details = "KhaozEngine live test", State = "Running" });
        for (int i = 0; i < 20; i++)
        {
            provider.Update();
            System.Threading.Thread.Sleep(50);
        }

        Assert.True(provider.IsConnected);
    }
}
```

- [ ] **Step 7: Run the provider tests (CI filter) to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~DiscordSocialProviderTests"`
Expected: PASS (5 tests). The live test does not run under this filter.

- [ ] **Step 8: Run the whole Social area + CI filter to confirm nothing regressed**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Social&Category!=LiveSocket"`
Expected: PASS (all Social + Discord unit tests).

- [ ] **Step 9: Commit**

```bash
git add KhaozEngine.Social.Discord/Internal/NamedPipeDiscordTransport.cs KhaozEngine.Social.Discord/DiscordSocialOptions.cs KhaozEngine.Social.Discord/DiscordSocialProvider.cs KhaozEngine.Tests/Social/DiscordSocialProviderTests.cs KhaozEngine.Tests/Social/DiscordLiveSocketTests.cs
git commit -m "social: DiscordSocialProvider + real named-pipe/unix-socket transport"
```

---

### Task 8: Release - version bump, CHANGELOG, per-package READMEs, full doc sweep, pack

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify: `KhaozEngine.Social/README.md`, `KhaozEngine.Social.Discord/README.md`
- Modify: `README.md`
- Modify: `docs/DEPENDENCY-SEAMS.md`
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/CONSUMERS.md`
- Modify: `docs/ROADMAP.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: Re-check for a concurrent version bump**

Run:
```bash
git fetch --all --prune --tags
git log --oneline -3 origin/main
grep "<KhaozEngineVersion>" Directory.Build.props
git tag | grep -E "v9\.(9|10|11)" | sort -V
```
Expected: confirm current is `9.9.0` and the next free minor is `9.10.0`. If `9.10.0` is now taken (a new tag or a higher `<KhaozEngineVersion>` on `origin/main`), first merge `origin/main` into this branch, then use the next FREE minor throughout the rest of this task (substitute it for `9.10.0` everywhere below).

- [ ] **Step 2: Bump the engine version**

In `Directory.Build.props`, change `<KhaozEngineVersion>9.9.0</KhaozEngineVersion>` to `<KhaozEngineVersion>9.10.0</KhaozEngineVersion>`.

- [ ] **Step 3: Add the CHANGELOG entry (newest-first, above the `## 9.9.0` entry)**

In `CHANGELOG.md`, immediately after the intro paragraph and before `## 9.9.0`, insert:
```markdown
## 9.10.0

Engine-owned Discord: a provider-neutral social/presence seam (`KhaozEngine.Social`) plus a
pure-managed Discord Rich Presence backend (`KhaozEngine.Social.Discord`), so games retire their
bespoke Discord code. Additive minor, new packages, no behaviour change to existing packages.

- **`KhaozEngine.Social`** (new, `Foundation` umbrella, deps: `Diagnostics`) - the seam:
  - **`ISocialProvider`** - provider-neutral contract: `TryInitialize`, `IsConnected`, `Update`,
    `SetPresence(in RichPresence)`, `ClearPresence`, `TryGetLocalUser(out SocialUser)`, and the
    `JoinRequested` / `JoinRequestReceived` events. Every method is best-effort and never throws.
  - **Value types** - `RichPresence` (details/state/timestamps/images/party/secrets/buttons),
    `PresenceImage`, `PresenceParty`, `PresenceButton`, `SocialUser`, `JoinRequest`.
  - **`NullSocialProvider`** - silent no-op default (headless servers, CI, no backend added).
  - **`SocialPresenceController`** - game-facing orchestration over any provider: lazy init, dedupe,
    throttled republish (`SocialPresenceOptions.RepublishInterval`, default 15s), an elapsed-timer
    helper (`SetElapsedPresence`), and session self-disable so a platform failure never reaches the
    game loop.
- **`KhaozEngine.Social.Discord`** (new, opt-in, NOT in any umbrella; add explicitly like
  `Physics.Bepu`, deps: `KhaozEngine.Social`) - **`DiscordSocialProvider : ISocialProvider`** over a
  pure-managed Discord IPC client (Windows named pipe / macOS+Linux unix domain socket, opcode+length
  +JSON framing via System.Text.Json). No native libraries, no third-party NuGet. Rich presence, local
  Discord identity, and join (`ACTIVITY_JOIN` / `ACTIVITY_JOIN_REQUEST`). `DiscordSocialOptions` carries
  the game's Discord Application id.
- The native Discord Social SDK (friends/lobbies/voice) is deliberately out of scope; if ever needed it
  slots in behind the same `ISocialProvider` as a separate opt-in `.Native` backend.
- Tests: headless coverage of the Null provider, the controller (dedupe/throttle/elapsed/self-disable),
  the IPC frame codec, activity payload mapping + dispatch parsing, unix socket path discovery, and the
  IPC client + provider against an in-memory transport. A live-socket smoke test is tagged
  `[Trait("Category","LiveSocket")]` and excluded from CI.
```

- [ ] **Step 4: Expand `KhaozEngine.Social/README.md`**

Replace the file with the full package README:
```markdown
# KhaozEngine.Social

Game-agnostic social/presence seam. `ISocialProvider` is the provider-neutral contract (Discord today,
Steam/other later) for rich presence, local identity, and join/invite. Depends only on
`KhaozEngine.Diagnostics`. The Discord backend is the opt-in
[KhaozEngine.Social.Discord](../KhaozEngine.Social.Discord) package (in no umbrella). In the
`Foundation` umbrella metapackage.

## Types

- **`ISocialProvider`** - `TryInitialize(appId)`, `IsConnected`, `Update()` (pump per frame),
  `SetPresence(in RichPresence)`, `ClearPresence()`, `TryGetLocalUser(out SocialUser)`, and the
  `JoinRequested(string secret)` / `JoinRequestReceived(JoinRequest)` events. Best-effort: never throws.
- **`RichPresence`** - `Details`, `State`, `StartTimestampUtc`/`EndTimestampUtc`, `LargeImage`/
  `SmallImage` (`PresenceImage`), `Party` (`PresenceParty`), `JoinSecret`/`SpectateSecret`, `Buttons`
  (`PresenceButton`). Empty fields are omitted by the backend.
- **`SocialUser`** - `Id`, `Username`, `GlobalName` (the local platform identity).
- **`JoinRequest`** - an inbound ask-to-join; `Accept()` / `Reject()`.
- **`NullSocialProvider`** - silent no-op default (headless / no backend).
- **`SocialPresenceController`** - the orchestrator games use.

## Usage

```csharp
using KhaozEngine.Social;

// Headless / no backend: silent no-op.
var social = new SocialPresenceController();

// With the Discord backend (add KhaozEngine.Social.Discord):
// var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "<your-app-id>" });
// var social = new SocialPresenceController(provider);

social.ApplicationId = "<your-app-id>";
social.Initialize();

// From a menu:
social.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });

// In a run, with a live elapsed timer:
social.SetElapsedPresence(new RichPresence { Details = "In Game", State = "Boss Rush" }, elapsed);

// Once per frame:
social.Update();

// One-click join from a friend's profile:
social.JoinRequested += secret => myNetcode.JoinFromSecret(secret);
```

`SocialPresenceController` dedupes and throttles, so calling `SetPresence`/`SetElapsedPresence` every
frame is cheap. Any provider error disables social for the session without touching the game loop.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
```

- [ ] **Step 5: Expand `KhaozEngine.Social.Discord/README.md`**

Replace the file with:
```markdown
# KhaozEngine.Social.Discord

Discord backend for the [KhaozEngine.Social](../KhaozEngine.Social) seam. `DiscordSocialProvider`
implements `ISocialProvider` over Discord Rich Presence with a pure-managed IPC client - no native
libraries, no third-party NuGet. Opt-in: NOT in any umbrella; add it explicitly on a game's client head
like `KhaozEngine.Physics.Bepu` or `KhaozEngine.WorldStore.Sqlite`. Depends only on `KhaozEngine.Social`.

## How it works

Discord Rich Presence does not need Discord's C++ SDK. The Discord desktop client listens on a local
socket (`\\.\pipe\discord-ipc-N` on Windows, a unix domain socket `discord-ipc-N` under the runtime/temp
dir on macOS/Linux). This package speaks the IPC protocol directly (4-byte opcode + 4-byte length +
UTF-8 JSON, System.Text.Json), so it ships zero native binaries and nothing per-RID.

The native Discord Social SDK (friends list, lobbies, voice) is out of scope; if ever needed it would be
a separate opt-in `.Native` backend behind the same `ISocialProvider`.

## Usage

```csharp
using KhaozEngine.Social;
using KhaozEngine.Social.Discord;

// On the game's desktop head:
var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "<your-discord-app-id>" });
var social = new SocialPresenceController(provider);
social.Initialize();
// ... social.SetPresence(...); social.Update() once per frame; social.Dispose() at shutdown.
```

Get a Discord Application id from the Discord Developer Portal. If Discord is not running the provider
stays disconnected and every call is a silent no-op.

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
```

- [ ] **Step 6: Add the README catalog rows**

In `README.md`, in the package table, immediately after the `**KhaozEngine.Physics.Bepu**` row (line ~38) add two rows:
```markdown
| **KhaozEngine.Social** | Game-agnostic social/presence seam (in `Foundation`): `ISocialProvider` (rich presence, local identity, join/invite) with a `NullSocialProvider` no-op default and a `SocialPresenceController` that dedupes/throttles presence and self-disables on error. Provider-neutral (Discord today, Steam/other later). The backend is opt-in (see `Social.Discord`). | Diagnostics |
| **KhaozEngine.Social.Discord** | Discord Rich Presence backend (opt-in, NOT in any umbrella; add explicitly like `Physics.Bepu`): `DiscordSocialProvider : ISocialProvider` over a pure-managed Discord IPC client (Windows named pipe / unix domain socket, JSON framing) - no native libraries, no third-party NuGet. Rich presence, local Discord identity, `ACTIVITY_JOIN`/join-request. `DiscordSocialOptions` carries the game's Discord Application id. | KhaozEngine.Social |
```

- [ ] **Step 7: Update the Foundation umbrella row + repo layout**

In `README.md`:
1. Foundation umbrella table row (line ~65): add `Social` to the list, e.g. change `...Serialization/Collision/Physics/Terrain/Determinism/Platform/Updates)` to `...Serialization/Social/Collision/Physics/Terrain/Determinism/Platform/Updates)`.
2. Repo layout block (line ~164): after `KhaozEngine.Physics/   KhaozEngine.Physics.Bepu/` add `KhaozEngine.Social/   KhaozEngine.Social.Discord/`.

- [ ] **Step 8: Bump the guard-checked version examples in README**

Run `grep -nE 'PackageReference Include="KhaozEngine' README.md` and change every `Version="9.9.0"` to `Version="9.10.0"` in those example lines (the doc guard checks each one equals the engine version).

- [ ] **Step 9: Add the DEPENDENCY-SEAMS row**

In `docs/DEPENDENCY-SEAMS.md`, in the "Every seam in the engine" table (the `| Area | Seam | Backend(s) | Third-party library |` table), add a row after the `Audio` row:
```markdown
| Social / presence | `KhaozEngine.Social` (`ISocialProvider`, value types, `NullSocialProvider` no-op, `SocialPresenceController`) | `KhaozEngine.Social.Discord` (`DiscordSocialProvider`) | none - hand-rolled Discord IPC over `System.IO.Pipes` / `System.Net.Sockets` (no third-party lib) |
```
(This is the first seam whose backend has no third-party library; the "none" note is deliberate.)

- [ ] **Step 10: Add the USING section**

In `docs/USING-KHAOZENGINE.md`, add a new section modeled on the 3D physics section (find it with `grep -n "3D physics" docs/USING-KHAOZENGINE.md`). Insert after that section:
```markdown
## Social / Discord presence (KhaozEngine.Social / KhaozEngine.Social.Discord)

`KhaozEngine.Social` is the provider-neutral seam (in `Foundation`): `ISocialProvider` +
`SocialPresenceController`. `KhaozEngine.Social.Discord` is the opt-in Discord backend (add it
explicitly on the client head, like `Physics.Bepu`); a headless server or a game without it uses the
silent `NullSocialProvider`.

```csharp
using KhaozEngine.Social;
using KhaozEngine.Social.Discord;

// Desktop head: real Discord presence.
var provider = new DiscordSocialProvider(new DiscordSocialOptions { ApplicationId = "<discord-app-id>" });
var social = new SocialPresenceController(provider);
social.Initialize();

// Menu / gameplay set high-level presence; the controller dedupes + throttles:
social.SetPresence(new RichPresence { Details = "In Menu", State = "Idle" });
social.SetElapsedPresence(new RichPresence { Details = "In Game", State = "Boss Rush" }, runElapsed);

// One-click "Join Game" from a friend's profile (needs a JoinSecret on the presence):
social.JoinRequested += secret => myNetcode.JoinFromSecret(secret);

// Pump once per frame; dispose at shutdown.
social.Update();
```

A game keeps only its Discord Application id, its presence copy, and its mode->`RichPresence` mapping.
Everything else (connection, throttling, error handling) is engine-owned.
```

- [ ] **Step 11: Update CONSUMERS + ROADMAP + CLAUDE.md**

1. `docs/CONSUMERS.md`: change the `**Engine current version:** \`9.9.0\`` line to `9.10.0`. In the opt-in-packages prose (which lists `Physics.Bepu` / `WorldStore.*` / `Server.Admin`), add `Social.Discord`.
2. `docs/ROADMAP.md`: change `Current released version: **9.9.0**` to `**9.10.0**`. DELETE near-term item #1 ("### 1. Engine-level Discord social SDK" and its body), and renumber the following items (item 2 "Physics engine..." becomes item 1, etc.).
3. `CLAUDE.md`: in the "Opt-in, in NO umbrella, added explicitly:" line, add `Social.Discord` (e.g. `Physics.Bepu, WorldStore.Sqlite/.SqlServer, Server.Admin, Social.Discord`).

- [ ] **Step 12: Run the doc-version guard**

Run: `./scripts/check-doc-versions.sh`
Expected: `all engine-version declarations match 9.10.0; package inventory is documented` (no FAIL lines). If it flags a missing catalog row or README, fix per the message (both new packages need a bolded `**KhaozEngine.Social**` / `**KhaozEngine.Social.Discord**` row and their own README.md).

- [ ] **Step 13: Run the full test suite (CI parity)**

Run: `mkdir -p local-feed && dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "Category!=LiveSocket"`
Expected: PASS (all tests, including the new Social area).

- [ ] **Step 14: Pack (cumulative) to local-feed**

Run: `dotnet pack KhaozEngine.Social/KhaozEngine.Social.csproj KhaozEngine.Social.Discord/KhaozEngine.Social.Discord.csproj -c Release -o ./local-feed`
Expected: `KhaozEngine.Social.9.10.0.nupkg` and `KhaozEngine.Social.Discord.9.10.0.nupkg` in `local-feed/`. (At true release time, pack the whole solution from the main repo root per the engine ritual, since the worktree local-feed is discarded on worktree removal.)

- [ ] **Step 15: Commit + tag (HOLD the push)**

```bash
git add Directory.Build.props CHANGELOG.md README.md docs/DEPENDENCY-SEAMS.md docs/USING-KHAOZENGINE.md docs/CONSUMERS.md docs/ROADMAP.md CLAUDE.md KhaozEngine.Social/README.md KhaozEngine.Social.Discord/README.md
git commit -m "social(9.10.0): KhaozEngine.Social seam + Social.Discord backend; docs + changelog"
git tag v9.10.0
```
Do NOT push or push the tag. The engine batches pushes (CI publishes to GitHub Packages on every `v*`); the human confirms the push. Report readiness instead.

---

## Self-Review

**Spec coverage:**
- Seam (`ISocialProvider`, value types, `NullSocialProvider`) -> Task 1. ✓
- `SocialPresenceController` orchestration (throttle/dedupe/session-disable/elapsed) -> Task 2. ✓
- Discord backend package + IPC codec -> Task 3. ✓
- Activity payload mapping + dispatch parsing -> Task 4. ✓
- Unix socket discovery -> Task 5. ✓
- IPC client + transport seam -> Task 6. ✓
- Real transport (platform split) + `DiscordSocialProvider` + live-socket test -> Task 7. ✓
- Packaging (slnx/Foundation/Tests wiring) -> Task 1 Steps 8-10 + Task 3. ✓
- Full doc sweep + version bump + CHANGELOG + READMEs + pack -> Task 8. ✓
- Consumer migration -> delivered as handoff prompts after the plan (not a code task; see the parent session's handoff deliverable). ✓
- Out-of-scope (native SDK, other providers, achievements) -> noted in CHANGELOG + READMEs, no tasks. ✓

**Placeholder scan:** No "TBD/TODO/implement later". Two steps (Task 2 Step 5, Task 7 Step 1) explicitly ask the implementer to verify a real API (`KhaozEngine.Diagnostics` logging entry point; Windows non-blocking pipe read) rather than guessing - these are verification steps with concrete grep commands and fallbacks, not placeholders.

**Type consistency:** `ISocialProvider` members match across Tasks 1, 2 (FakeSocialProvider), 7 (DiscordSocialProvider). `RichPresence` field names match between Task 1, Task 4 (payload mapping), and the READMEs. `DiscordIpcClient` surface (`TryConnect`/`SetActivity`/`ClearActivity`/`Pump`/`LocalUser`/`JoinSecretReceived`/`JoinRequestUserReceived`) matches between Task 6 (definition) and Task 7 (consumer). `DiscordIpcPayloads` method names match between Task 4 (definition) and Task 6 (`DiscordIpcClient` calls). `SocialPresenceController` surface matches between Task 2 and the READMEs/USING doc in Task 8.
