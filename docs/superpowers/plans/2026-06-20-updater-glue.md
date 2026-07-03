# Updater Last-Mile Glue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Centralize the auto-updater last-mile glue (overlay UI, shim entry, signing/publish CLI, publish template + docs) into KhaozEngine so games adopt the updater with thin per-game config only. Ships as 7.2.0.

**Architecture:** Additive on top of the existing `KhaozEngine.Updates` package. New read-only `IUpdateStatus` decouples a Gui overlay (`UpdateOverlayView` + `UpdateOverlayScreen` in `KhaozEngine.Gui`, which gains a project reference to Updates) from the concrete `UpdateService`. The overlay raises events; `UpdateOverlayActions` is the one-line default wiring. A new `KhaozEngine.Updates.Tool` dotnet tool (`ke-updater`) wraps existing manifest/signing APIs via testable command logic kept in the Updates library. A parameterized `publish-update.sh` template + a README "Adopting the updater" section complete it.

**Tech Stack:** net10.0, C# (nullable enabled), xUnit, System.Numerics, RSA (BCL), `dotnet pack`/`PackAsTool`, MSBuild slnx.

---

## File Structure

**`KhaozEngine.Updates` (foundation, pure .NET) — new files:**
- `IUpdateStatus.cs` — read-only status view consumed by UI.
- `UpdaterShim.cs` — reusable shim entry (`Main` + `ResolveLogPath`).
- `ManifestToolCommands.cs` — testable `ke-updater` command logic (manifest/genkey/sign/verify).
- `UpdateOverlayActions.cs` — `OverlayAction` enum + state→action policy + `Trigger(UpdateService)`.
- `UpdateService.cs` (modify) — add `: IUpdateStatus`.
- `KhaozEngine.Updates.csproj` (modify) — bundle the publish template.
- `templates/publish-update.sh` — parameterized publish template.
- `README.md` (modify) — "Adopting the updater" section.

**`KhaozEngine.Updates.Tool` (new dotnet tool):**
- `KhaozEngine.Updates.Tool.csproj` — `PackAsTool`, command `ke-updater`.
- `Program.cs` — one-liner delegating to `ManifestToolCommands.Run`.

**`KhaozEngine.Gui` — new files:**
- `UpdateOverlayTheme.cs` — colours/layout/labels/binding.
- `UpdateOverlayView.cs` — the presenter widget.
- `UpdateOverlayScreen.cs` — thin `Screen` wrapper.
- `KhaozEngine.Gui.csproj` (modify) — project reference to Updates.

**`KhaozEngine.Tests` — new test files:**
- `Updates/FakeUpdateStatus.cs` — shared `IUpdateStatus` test double (no namespace, globally visible).
- `Updates/UpdaterShimTests.cs`, `Updates/ManifestToolCommandsTests.cs`, `Updates/UpdateOverlayActionsTests.cs`, `Updates/IUpdateStatusTests.cs`.
- `Gui/OverlayTestInput.cs` — shared `InputState` builders for overlay tests.
- `Gui/UpdateOverlayThemeTests.cs`, `Gui/UpdateOverlayViewTests.cs`, `Gui/UpdateOverlayScreenTests.cs`.

**Repo wiring:**
- `KhaozEngine.slnx` (modify) — add the Tool project.
- `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` — release bump.

---

## Task 1: `IUpdateStatus` read-only view

**Files:**
- Create: `KhaozEngine.Updates/IUpdateStatus.cs`
- Modify: `KhaozEngine.Updates/UpdateService.cs:22`
- Create: `KhaozEngine.Tests/Updates/FakeUpdateStatus.cs`
- Create: `KhaozEngine.Tests/Updates/IUpdateStatusTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Updates/FakeUpdateStatus.cs` (shared double, intentionally in the global namespace so every test file sees it without a using):

```csharp
using KhaozEngine.Updates;

/// <summary>Mutable IUpdateStatus test double.</summary>
public sealed class FakeUpdateStatus : IUpdateStatus
{
    public UpdateState State { get; set; } = UpdateState.Idle;
    public string? RemoteVersion { get; set; }
    public int FilesDownloaded { get; set; }
    public int TotalFilesToDownload { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalDownloadBytes { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsRequired { get; set; }
}
```

`KhaozEngine.Tests/Updates/IUpdateStatusTests.cs`:

```csharp
using KhaozEngine.Updates;
using Xunit;

public class IUpdateStatusTests
{
    [Fact]
    public void UpdateService_implements_IUpdateStatus() =>
        Assert.True(typeof(IUpdateStatus).IsAssignableFrom(typeof(UpdateService)));

    [Fact]
    public void Fake_double_carries_progress()
    {
        IUpdateStatus s = new FakeUpdateStatus { State = UpdateState.Downloading, FilesDownloaded = 2 };
        Assert.Equal(UpdateState.Downloading, s.State);
        Assert.Equal(2, s.FilesDownloaded);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~IUpdateStatusTests"`
Expected: FAIL to compile — `IUpdateStatus` does not exist.

- [ ] **Step 3: Create the interface**

`KhaozEngine.Updates/IUpdateStatus.cs`:

```csharp
namespace KhaozEngine.Updates;

/// <summary>
/// Read-only view of an in-flight update, consumed by UI (e.g. the Gui overlay) so the presenter never
/// needs the concrete <see cref="UpdateService"/> (which requires full options to construct). Implemented
/// by <see cref="UpdateService"/>; mirror it with a stub in tests.
/// </summary>
public interface IUpdateStatus
{
    /// <summary>Current lifecycle state.</summary>
    UpdateState State { get; }
    /// <summary>Newer version offered by the feed, or null before a check completes.</summary>
    string? RemoteVersion { get; }
    /// <summary>Files staged so far this download.</summary>
    int FilesDownloaded { get; }
    /// <summary>Total files this download must fetch.</summary>
    int TotalFilesToDownload { get; }
    /// <summary>Bytes staged so far this download.</summary>
    long BytesDownloaded { get; }
    /// <summary>Total bytes this download must fetch.</summary>
    long TotalDownloadBytes { get; }
    /// <summary>Last error message when <see cref="State"/> is <see cref="UpdateState.Failed"/>.</summary>
    string? ErrorMessage { get; }
    /// <summary>True when the offered update is marked required.</summary>
    bool IsRequired { get; }
}
```

- [ ] **Step 4: Make `UpdateService` implement it**

In `KhaozEngine.Updates/UpdateService.cs:22`, change:

```csharp
public sealed class UpdateService : IDisposable
```

to:

```csharp
public sealed class UpdateService : IDisposable, IUpdateStatus
```

(All eight members already exist as public properties on `UpdateService` — no other change needed.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~IUpdateStatusTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Updates/IUpdateStatus.cs KhaozEngine.Updates/UpdateService.cs KhaozEngine.Tests/Updates/FakeUpdateStatus.cs KhaozEngine.Tests/Updates/IUpdateStatusTests.cs
git commit -m "updates(updater-glue): IUpdateStatus read-only view on UpdateService"
```

---

## Task 2: `UpdaterShim` reusable shim entry

**Files:**
- Create: `KhaozEngine.Updates/UpdaterShim.cs`
- Create: `KhaozEngine.Tests/Updates/UpdaterShimTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Updates/UpdaterShimTests.cs`:

```csharp
using System.IO;
using KhaozEngine.Updates;
using Xunit;

public class UpdaterShimTests
{
    [Fact]
    public void ResolveLogPath_places_log_next_to_apply_config()
    {
        string[] args = { "--apply", Path.Combine("some", "dir", "apply-update.json") };
        Assert.Equal(Path.Combine("some", "dir", "updater.log"), UpdaterShim.ResolveLogPath(args));
    }

    [Fact]
    public void ResolveLogPath_falls_back_to_current_dir_when_no_path()
    {
        Assert.Equal(Path.Combine(".", "updater.log"), UpdaterShim.ResolveLogPath(new[] { "--apply" }));
    }

    [Fact]
    public void ResolveLogPath_handles_bare_filename()
    {
        Assert.Equal(Path.Combine(".", "updater.log"), UpdaterShim.ResolveLogPath(new[] { "--apply", "apply.json" }));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdaterShimTests"`
Expected: FAIL to compile — `UpdaterShim` does not exist.

- [ ] **Step 3: Write the implementation**

`KhaozEngine.Updates/UpdaterShim.cs`:

```csharp
using System;
using System.IO;

namespace KhaozEngine.Updates;

/// <summary>
/// The reusable updater-shim entry. A game's external updater exe becomes a one-liner:
/// <c>return KhaozEngine.Updates.UpdaterShim.Main(args);</c>. It opens an autoflush log next to the
/// apply-config file and forwards to <see cref="UpdateApplier.Run"/> with a real
/// <see cref="SystemUpdaterEnvironment"/>. The apply-config contract stays engine-owned, so the writer
/// (UpdateService) and reader (this shim) never drift.
/// </summary>
public static class UpdaterShim
{
    /// <summary>
    /// The log path: <c>updater.log</c> beside the apply-config file passed as <c>args[1]</c> (the value
    /// after <c>--apply</c>); the current directory when no path is present.
    /// </summary>
    public static string ResolveLogPath(string[] args)
    {
        string baseRef = args.Length > 1 ? args[1] : ".";
        string dir = Path.GetDirectoryName(baseRef) ?? ".";
        if (dir.Length == 0) dir = ".";
        return Path.Combine(dir, "updater.log");
    }

    /// <summary>Opens the log, runs the staged apply, returns the process exit code.</summary>
    public static int Main(string[] args)
    {
        string logPath = ResolveLogPath(args);
        using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        return UpdateApplier.Run(args, new SystemUpdaterEnvironment(msg =>
        {
            try { Console.WriteLine(msg); } catch { /* no console attached (GUI subsystem) */ }
            log.WriteLine(msg);
        }));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdaterShimTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates/UpdaterShim.cs KhaozEngine.Tests/Updates/UpdaterShimTests.cs
git commit -m "updates(updater-glue): reusable UpdaterShim entry"
```

---

## Task 3: `ManifestToolCommands` (ke-updater command logic)

**Files:**
- Create: `KhaozEngine.Updates/ManifestToolCommands.cs`
- Create: `KhaozEngine.Tests/Updates/ManifestToolCommandsTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Updates/ManifestToolCommandsTests.cs`:

```csharp
using System.IO;
using KhaozEngine.Updates;
using Xunit;

public class ManifestToolCommandsTests
{
    static (StringWriter outw, StringWriter errw) Writers() => (new StringWriter(), new StringWriter());

    [Fact]
    public void GenKey_then_sign_then_verify_roundtrips()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-rt").FullName;
        try
        {
            var (o, e) = Writers();
            Assert.Equal(0, ManifestToolCommands.Run(new[] { "genkey", "--out", dir }, o, e));
            string priv = Path.Combine(dir, "private.pem");
            string pub = Path.Combine(dir, "public.pem");
            Assert.True(File.Exists(priv));
            Assert.True(File.Exists(pub));

            string manifest = Path.Combine(dir, "manifest.json");
            File.WriteAllText(manifest, "{\"version\":\"1.0.0\"}");
            Assert.Equal(0, ManifestToolCommands.Run(new[] { "sign", "--manifest", manifest, "--key", priv }, o, e));
            Assert.True(File.Exists(manifest + ".sig"));

            Assert.Equal(0, ManifestToolCommands.Run(
                new[] { "verify", "--manifest", manifest, "--sig", manifest + ".sig", "--key", pub }, o, e));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Verify_fails_on_tampered_manifest()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-tamper").FullName;
        try
        {
            var (o, e) = Writers();
            ManifestToolCommands.Run(new[] { "genkey", "--out", dir }, o, e);
            string priv = Path.Combine(dir, "private.pem");
            string pub = Path.Combine(dir, "public.pem");
            string manifest = Path.Combine(dir, "manifest.json");
            File.WriteAllText(manifest, "{\"version\":\"1.0.0\"}");
            ManifestToolCommands.Run(new[] { "sign", "--manifest", manifest, "--key", priv }, o, e);
            File.WriteAllText(manifest, "{\"version\":\"6.6.6\"}"); // tamper after signing
            Assert.Equal(2, ManifestToolCommands.Run(
                new[] { "verify", "--manifest", manifest, "--sig", manifest + ".sig", "--key", pub }, o, e));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Manifest_writes_json_to_stdout()
    {
        string dir = Directory.CreateTempSubdirectory("ke-updater-man").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            var (o, e) = Writers();
            Assert.Equal(0, ManifestToolCommands.Run(
                new[] { "manifest", "--dir", dir, "--platform", "win-x64", "--version", "1.2.3" }, o, e));
            string json = o.ToString();
            Assert.Contains("\"version\"", json);
            Assert.Contains("1.2.3", json);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unknown_command_returns_nonzero()
    {
        var (o, e) = Writers();
        Assert.Equal(1, ManifestToolCommands.Run(new[] { "bogus" }, o, e));
    }

    [Fact]
    public void No_args_returns_nonzero_and_prints_usage()
    {
        var (o, e) = Writers();
        Assert.Equal(1, ManifestToolCommands.Run(System.Array.Empty<string>(), o, e));
        Assert.Contains("Usage", e.ToString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ManifestToolCommandsTests"`
Expected: FAIL to compile — `ManifestToolCommands` does not exist.

- [ ] **Step 3: Write the implementation**

`KhaozEngine.Updates/ManifestToolCommands.cs`:

```csharp
using System.IO;

namespace KhaozEngine.Updates;

/// <summary>
/// The reusable command logic behind the <c>ke-updater</c> dotnet tool. It lives in the Updates library
/// (not the tool exe) so it is unit-testable and the tool's <c>Program.cs</c> stays a one-liner. Thin
/// wrapper over the existing engine APIs: <see cref="UpdateManifest.GenerateFromDirectory"/>,
/// <see cref="ManifestSigner"/>, <see cref="ManifestVerifier"/>.
/// </summary>
public static class ManifestToolCommands
{
    const string Usage =
        "Usage: ke-updater <command>\n" +
        "  manifest --dir <path> --platform <id> --version <v> [--output <path>]\n" +
        "  genkey --out <dir>\n" +
        "  sign --manifest <manifest.json> --key <private.pem>\n" +
        "  verify --manifest <manifest.json> --sig <manifest.json.sig> --key <public.pem>";

    /// <summary>Dispatches on <c>args[0]</c>. Returns a process exit code (0 = success).</summary>
    public static int Run(string[] args, TextWriter outw, TextWriter errw)
    {
        if (args.Length == 0) { errw.WriteLine(Usage); return 1; }
        return args[0] switch
        {
            "manifest" => Manifest(args, outw, errw),
            "genkey" => GenKey(args, errw),
            "sign" => Sign(args, errw),
            "verify" => Verify(args, errw),
            _ => Fail(errw, $"Unknown command '{args[0]}'.\n{Usage}"),
        };
    }

    static int Manifest(string[] args, TextWriter outw, TextWriter errw)
    {
        string? dir = Opt(args, "--dir"), platform = Opt(args, "--platform"),
                version = Opt(args, "--version"), output = Opt(args, "--output");
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(version))
            return Fail(errw, "manifest: --dir, --platform and --version are required.");
        if (!Directory.Exists(dir)) return Fail(errw, $"Directory not found: {dir}");

        UpdateManifest manifest = UpdateManifest.GenerateFromDirectory(Path.GetFullPath(dir), version, platform);
        string json = manifest.Serialize();
        if (!string.IsNullOrWhiteSpace(output))
        {
            string? outDir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllText(output, json);
            errw.WriteLine($"Manifest written to {output} ({manifest.Files.Count} files)");
        }
        else outw.Write(json);
        return 0;
    }

    static int GenKey(string[] args, TextWriter errw)
    {
        string? outDir = Opt(args, "--out");
        if (string.IsNullOrWhiteSpace(outDir)) return Fail(errw, "genkey: --out <dir> is required.");
        Directory.CreateDirectory(outDir);
        ManifestKeyPair pair = ManifestSigner.GenerateKeyPair();
        string priv = Path.Combine(outDir, "private.pem");
        string pub = Path.Combine(outDir, "public.pem");
        File.WriteAllText(priv, pair.PrivateKeyPem);
        File.WriteAllText(pub, pair.PublicKeyPem);
        errw.WriteLine($"Wrote {priv} and {pub}. Keep private.pem secret; embed public.pem in TrustedPublicKeys.");
        return 0;
    }

    static int Sign(string[] args, TextWriter errw)
    {
        string? manifestPath = Opt(args, "--manifest"), keyPath = Opt(args, "--key");
        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(keyPath))
            return Fail(errw, "sign: --manifest and --key are required.");
        if (!File.Exists(manifestPath)) return Fail(errw, $"Manifest not found: {manifestPath}");
        if (!File.Exists(keyPath)) return Fail(errw, $"Key not found: {keyPath}");
        byte[] data = File.ReadAllBytes(manifestPath);
        byte[] sig = ManifestSigner.Sign(data, File.ReadAllText(keyPath));
        string sigPath = manifestPath + ".sig";
        File.WriteAllBytes(sigPath, sig);
        errw.WriteLine($"Wrote {sigPath} ({sig.Length} bytes).");
        return 0;
    }

    static int Verify(string[] args, TextWriter errw)
    {
        string? manifestPath = Opt(args, "--manifest"), sigPath = Opt(args, "--sig"), keyPath = Opt(args, "--key");
        if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(sigPath) || string.IsNullOrWhiteSpace(keyPath))
            return Fail(errw, "verify: --manifest, --sig and --key are required.");
        if (!File.Exists(manifestPath) || !File.Exists(sigPath) || !File.Exists(keyPath))
            return Fail(errw, "verify: one or more input files not found.");
        bool ok = ManifestVerifier.Verify(
            File.ReadAllBytes(manifestPath), File.ReadAllBytes(sigPath), new[] { File.ReadAllText(keyPath) });
        errw.WriteLine(ok ? "Signature OK." : "Signature INVALID.");
        return ok ? 0 : 2;
    }

    static string? Opt(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    static int Fail(TextWriter errw, string message) { errw.WriteLine(message); return 1; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~ManifestToolCommandsTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates/ManifestToolCommands.cs KhaozEngine.Tests/Updates/ManifestToolCommandsTests.cs
git commit -m "updates(updater-glue): ke-updater command logic (manifest/genkey/sign/verify)"
```

---

## Task 4: `KhaozEngine.Updates.Tool` dotnet tool

**Files:**
- Create: `KhaozEngine.Updates.Tool/KhaozEngine.Updates.Tool.csproj`
- Create: `KhaozEngine.Updates.Tool/Program.cs`
- Modify: `KhaozEngine.slnx:34`

No unit test (the tool body is a single delegating line; its logic is covered by Task 3). Verification is build + pack.

- [ ] **Step 1: Create the project file**

`KhaozEngine.Updates.Tool/KhaozEngine.Updates.Tool.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>ke-updater</ToolCommandName>
    <PackageId>KhaozEngine.Updates.Tool</PackageId>
    <Version>$(KhaozEngineVersion)</Version>
    <Description>The ke-updater CLI: generate, sign, and verify KhaozEngine update manifests (RSA-2048). A dotnet tool wrapper over KhaozEngine.Updates.</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../KhaozEngine.Updates/KhaozEngine.Updates.csproj" />
  </ItemGroup>
</Project>
```

(`TargetFramework`, `Nullable`, `ImplicitUsings` are inherited from `Directory.Build.props`.)

- [ ] **Step 2: Create the entry point**

`KhaozEngine.Updates.Tool/Program.cs`:

```csharp
// The whole CLI lives in KhaozEngine.Updates.ManifestToolCommands so it is unit-tested and this stays thin.
return KhaozEngine.Updates.ManifestToolCommands.Run(args, System.Console.Out, System.Console.Error);
```

- [ ] **Step 3: Register in the solution**

In `KhaozEngine.slnx`, add after line 34 (`<Project Path="KhaozEngine.Updates/KhaozEngine.Updates.csproj" />`):

```xml
  <Project Path="KhaozEngine.Updates.Tool/KhaozEngine.Updates.Tool.csproj" />
```

- [ ] **Step 4: Verify it builds and packs as a tool**

Run: `dotnet build -c Release KhaozEngine.Updates.Tool/KhaozEngine.Updates.Tool.csproj`
Expected: Build succeeded.

Run: `dotnet pack -c Release KhaozEngine.Updates.Tool/KhaozEngine.Updates.Tool.csproj -o ./local-feed && ls local-feed/KhaozEngine.Updates.Tool.*.nupkg`
Expected: a `KhaozEngine.Updates.Tool.7.1.0.nupkg` (version bumps to 7.2.0 in Task 11) is listed.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates.Tool/KhaozEngine.Updates.Tool.csproj KhaozEngine.Updates.Tool/Program.cs KhaozEngine.slnx
git commit -m "updates(updater-glue): ke-updater dotnet tool package"
```

---

## Task 5: `UpdateOverlayActions` default wiring

**Files:**
- Create: `KhaozEngine.Updates/UpdateOverlayActions.cs`
- Create: `KhaozEngine.Tests/Updates/UpdateOverlayActionsTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Updates/UpdateOverlayActionsTests.cs`:

```csharp
using KhaozEngine.Updates;
using Xunit;

public class UpdateOverlayActionsTests
{
    [Theory]
    [InlineData(UpdateState.Idle, OverlayAction.None)]
    [InlineData(UpdateState.Checking, OverlayAction.None)]
    [InlineData(UpdateState.UpdateAvailable, OverlayAction.Download)]
    [InlineData(UpdateState.Downloading, OverlayAction.None)]
    [InlineData(UpdateState.ReadyToApply, OverlayAction.Apply)]
    [InlineData(UpdateState.Applying, OverlayAction.None)]
    [InlineData(UpdateState.Failed, OverlayAction.Retry)]
    public void ResolveAction_maps_state(UpdateState state, OverlayAction expected) =>
        Assert.Equal(expected, UpdateOverlayActions.ResolveAction(state));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayActionsTests"`
Expected: FAIL to compile — `UpdateOverlayActions` / `OverlayAction` do not exist.

- [ ] **Step 3: Write the implementation**

`KhaozEngine.Updates/UpdateOverlayActions.cs`:

```csharp
namespace KhaozEngine.Updates;

/// <summary>The action the overlay's trigger should perform for the current state.</summary>
public enum OverlayAction { None, Download, Apply, Retry }

/// <summary>
/// Default wiring from the Gui overlay's trigger to the <see cref="UpdateService"/>. Lets a game wire the
/// overlay in one line: <c>overlay.OnTrigger += _ =&gt; UpdateOverlayActions.Trigger(service);</c>. The
/// state→action policy is the pure <see cref="ResolveAction"/> (unit-tested); <see cref="Trigger"/> applies it.
/// </summary>
public static class UpdateOverlayActions
{
    /// <summary>Maps a state to the action its trigger should perform (None for non-actionable states).</summary>
    public static OverlayAction ResolveAction(UpdateState state) => state switch
    {
        UpdateState.UpdateAvailable => OverlayAction.Download,
        UpdateState.ReadyToApply => OverlayAction.Apply,
        UpdateState.Failed => OverlayAction.Retry,
        _ => OverlayAction.None,
    };

    /// <summary>Performs the resolved action against <paramref name="service"/> for its current state.</summary>
    public static void Trigger(UpdateService service)
    {
        switch (ResolveAction(service.State))
        {
            case OverlayAction.Download: _ = service.StartDownloadAsync(); break;
            case OverlayAction.Apply: service.ApplyUpdate(); break;
            case OverlayAction.Retry: _ = service.CheckForUpdateAsync(); break;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayActionsTests"`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates/UpdateOverlayActions.cs KhaozEngine.Tests/Updates/UpdateOverlayActionsTests.cs
git commit -m "updates(updater-glue): UpdateOverlayActions default trigger wiring"
```

---

## Task 6: Gui → Updates reference + `UpdateOverlayTheme`

**Files:**
- Modify: `KhaozEngine.Gui/KhaozEngine.Gui.csproj`
- Create: `KhaozEngine.Gui/UpdateOverlayTheme.cs`
- Create: `KhaozEngine.Tests/Gui/UpdateOverlayThemeTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Gui/UpdateOverlayThemeTests.cs`:

```csharp
using KhaozEngine.Gui;
using KhaozEngine.Updates;
using Xunit;

public class UpdateOverlayThemeTests
{
    [Fact]
    public void Default_titles_match_state()
    {
        var t = UpdateOverlayTheme.Default;
        Assert.Equal("Update Available - v1.2.3", t.TitleFor(UpdateState.UpdateAvailable, "1.2.3"));
        Assert.Equal("Update v1.2.3 Ready", t.TitleFor(UpdateState.ReadyToApply, "1.2.3"));
        Assert.Equal("Update Failed", t.TitleFor(UpdateState.Failed, null));
    }

    [Fact]
    public void Body_uses_trigger_key_label()
    {
        var t = UpdateOverlayTheme.Default;
        t.TriggerKeyLabel = "X";
        Assert.Equal("Press [X] to download", t.BodyFor(UpdateState.UpdateAvailable, new FakeUpdateStatus()));
    }

    [Fact]
    public void Downloading_body_reports_progress()
    {
        var t = UpdateOverlayTheme.Default;
        var s = new FakeUpdateStatus
        {
            State = UpdateState.Downloading,
            FilesDownloaded = 2,
            TotalFilesToDownload = 5,
            BytesDownloaded = 3 * 1024 * 1024,
            TotalDownloadBytes = 10 * 1024 * 1024,
        };
        Assert.Equal("Downloading 2/5 files (3.0/10.0 MB)", t.BodyFor(UpdateState.Downloading, s));
    }

    [Fact]
    public void AccentFor_differs_between_ready_and_failed()
    {
        var t = UpdateOverlayTheme.Default;
        Assert.NotEqual(t.AccentFor(UpdateState.ReadyToApply), t.AccentFor(UpdateState.Failed));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayThemeTests"`
Expected: FAIL to compile — `UpdateOverlayTheme` does not exist (and Gui cannot see `UpdateState` yet).

- [ ] **Step 3: Add the Gui → Updates project reference**

In `KhaozEngine.Gui/KhaozEngine.Gui.csproj`, inside the existing `<ItemGroup>` that holds the project references (alongside the Windowing and Render2D references), add:

```xml
    <ProjectReference Include="../KhaozEngine.Updates/KhaozEngine.Updates.csproj" />
```

- [ ] **Step 4: Write the theme**

`KhaozEngine.Gui/UpdateOverlayTheme.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Look, labels, and trigger binding for <see cref="UpdateOverlayView"/>. Every visual is injected (no
/// hard-coded colours in the view); <see cref="Default"/> reproduces a neutral SpaceGame-style palette.
/// Set properties to retheme, or override <see cref="TitleFor"/>/<see cref="BodyFor"/>/<see cref="AccentFor"/>
/// for fully custom (e.g. localized) text. Colours are <see cref="Vector4"/> (RGBA 0..1); the
/// <see cref="Color"/> literals convert implicitly.
/// </summary>
public class UpdateOverlayTheme
{
    // Panel + chrome
    public Vector4 DimFill = Color.FromBytes(0, 0, 0, 140);
    public Vector4 PanelFill = Color.FromBytes(12, 16, 28, 230);
    public Vector4 BodyText = Color.FromBytes(180, 190, 210);
    public Vector4 ProgressBackground = Color.FromBytes(30, 40, 60, 200);
    public Vector4 ProgressFill = Color.FromBytes(80, 160, 255, 230);

    // Per-state accent (title text + border tint)
    public Vector4 AvailableAccent = Color.FromBytes(100, 200, 255);
    public Vector4 DownloadingAccent = Color.FromBytes(100, 200, 255);
    public Vector4 ReadyAccent = Color.FromBytes(120, 255, 120);
    public Vector4 ApplyingAccent = Color.FromBytes(255, 220, 100);
    public Vector4 FailedAccent = Color.FromBytes(255, 140, 100);

    // Layout
    public float PanelWidth = 480f;
    public float PanelPadding = 24f;
    public float TitleScale = 0.7f;
    public float BodyScale = 0.5f;
    public float ProgressBarHeight = 6f;
    public float BorderThickness = 1f;
    public float FadeSpeed = 4f; // alpha units/sec (~0.25s fade-in)

    // Trigger binding
    public Key TriggerKey = Key.U;
    public GamepadButton? TriggerButton = GamepadButton.Y;
    public string TriggerKeyLabel = "U";

    /// <summary>Accent colour for <paramref name="state"/> (title text + border).</summary>
    public virtual Vector4 AccentFor(UpdateState state) => state switch
    {
        UpdateState.UpdateAvailable => AvailableAccent,
        UpdateState.Downloading => DownloadingAccent,
        UpdateState.ReadyToApply => ReadyAccent,
        UpdateState.Applying => ApplyingAccent,
        UpdateState.Failed => FailedAccent,
        _ => AvailableAccent,
    };

    /// <summary>Title line for <paramref name="state"/>.</summary>
    public virtual string TitleFor(UpdateState state, string? remoteVersion) => state switch
    {
        UpdateState.UpdateAvailable => $"Update Available - v{remoteVersion}",
        UpdateState.Downloading => "Downloading Update...",
        UpdateState.ReadyToApply => $"Update v{remoteVersion} Ready",
        UpdateState.Applying => "Applying Update...",
        UpdateState.Failed => "Update Failed",
        _ => string.Empty,
    };

    /// <summary>Body line for <paramref name="state"/>.</summary>
    public virtual string BodyFor(UpdateState state, IUpdateStatus status) => state switch
    {
        UpdateState.UpdateAvailable => $"Press [{TriggerKeyLabel}] to download",
        UpdateState.Downloading => FormatDownloading(status),
        UpdateState.ReadyToApply => $"Press [{TriggerKeyLabel}] to restart and apply",
        UpdateState.Applying => "Game will restart shortly",
        UpdateState.Failed => $"Press [{TriggerKeyLabel}] to retry",
        _ => string.Empty,
    };

    static string FormatDownloading(IUpdateStatus s)
    {
        double mb = s.BytesDownloaded / (1024d * 1024d);
        double totalMb = s.TotalDownloadBytes / (1024d * 1024d);
        return $"Downloading {s.FilesDownloaded}/{s.TotalFilesToDownload} files ({mb:0.0}/{totalMb:0.0} MB)";
    }

    /// <summary>A fresh default theme (neutral palette, [U] / gamepad-Y trigger).</summary>
    public static UpdateOverlayTheme Default => new();
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayThemeTests"`
Expected: PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Gui/KhaozEngine.Gui.csproj KhaozEngine.Gui/UpdateOverlayTheme.cs KhaozEngine.Tests/Gui/UpdateOverlayThemeTests.cs
git commit -m "gui(updater-glue): UpdateOverlayTheme + Gui->Updates reference"
```

---

## Task 7: `UpdateOverlayView` presenter widget

**Files:**
- Create: `KhaozEngine.Gui/UpdateOverlayView.cs`
- Create: `KhaozEngine.Tests/Gui/OverlayTestInput.cs`
- Create: `KhaozEngine.Tests/Gui/UpdateOverlayViewTests.cs`

- [ ] **Step 1: Write the shared input helper + failing test**

`KhaozEngine.Tests/Gui/OverlayTestInput.cs` (shared `InputState` builders, global namespace):

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

/// <summary>InputState builders for overlay tests.</summary>
public static class OverlayTestInput
{
    public static InputState KeyFrame(Key k) => new(
        new HashSet<Key> { k }, new HashSet<Key> { k }, new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(),
        Vector2.Zero, Vector2.Zero, 0, 960, 540);

    public static InputState PadFrame(GamepadButton b) => new(
        new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
        new HashSet<MouseButton>(), new HashSet<MouseButton>(),
        Vector2.Zero, Vector2.Zero, 0, 960, 540,
        new[]
        {
            new GamepadState(0,
                new HashSet<GamepadButton> { b }, new HashSet<GamepadButton> { b }, new HashSet<GamepadButton>(),
                Vector2.Zero, Vector2.Zero, 0, 0),
        });
}
```

`KhaozEngine.Tests/Gui/UpdateOverlayViewTests.cs`:

```csharp
using KhaozEngine.Gui;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;
using Xunit;

public class UpdateOverlayViewTests
{
    [Theory]
    [InlineData(UpdateState.Idle, false)]
    [InlineData(UpdateState.Checking, false)]
    [InlineData(UpdateState.UpdateAvailable, true)]
    [InlineData(UpdateState.Downloading, true)]
    [InlineData(UpdateState.ReadyToApply, true)]
    [InlineData(UpdateState.Applying, true)]
    [InlineData(UpdateState.Failed, true)]
    public void IsVisible_matches_state(UpdateState s, bool vis) =>
        Assert.Equal(vis, UpdateOverlayView.IsVisible(s));

    [Fact]
    public void Trigger_key_in_visible_state_raises_events_and_consumes()
    {
        var view = new UpdateOverlayView();
        UpdateState? got = null;
        int count = 0;
        view.OnTrigger += s => got = s;
        view.Triggered += () => count++;

        bool consumed = view.Update(new FakeUpdateStatus { State = UpdateState.UpdateAvailable },
            OverlayTestInput.KeyFrame(Key.U), 0.016f);

        Assert.True(consumed);
        Assert.Equal(UpdateState.UpdateAvailable, got);
        Assert.Equal(1, count);
    }

    [Fact]
    public void No_trigger_and_no_consume_in_hidden_state()
    {
        var view = new UpdateOverlayView();
        bool fired = false;
        view.Triggered += () => fired = true;

        bool consumed = view.Update(new FakeUpdateStatus { State = UpdateState.Idle },
            OverlayTestInput.KeyFrame(Key.U), 0.016f);

        Assert.False(consumed);
        Assert.False(fired);
    }

    [Fact]
    public void Gamepad_button_triggers()
    {
        var view = new UpdateOverlayView(); // default TriggerButton = Y
        int count = 0;
        view.Triggered += () => count++;

        view.Update(new FakeUpdateStatus { State = UpdateState.ReadyToApply },
            OverlayTestInput.PadFrame(GamepadButton.Y), 0.016f);

        Assert.Equal(1, count);
    }

    [Fact]
    public void Wrong_key_does_not_trigger()
    {
        var view = new UpdateOverlayView();
        bool fired = false;
        view.Triggered += () => fired = true;

        view.Update(new FakeUpdateStatus { State = UpdateState.UpdateAvailable },
            OverlayTestInput.KeyFrame(Key.J), 0.016f);

        Assert.False(fired);
    }

    [Fact]
    public void Fade_advances_toward_visible()
    {
        var view = new UpdateOverlayView();
        view.Update(new FakeUpdateStatus { State = UpdateState.UpdateAvailable }, InputState.Empty, 0.1f);
        Assert.True(view.Alpha > 0f);
    }

    [Theory]
    [InlineData(0, 100, 0f)]
    [InlineData(50, 100, 0.5f)]
    [InlineData(150, 100, 1f)]
    [InlineData(10, 0, 0f)]
    public void ProgressFraction_clamps(long done, long total, float expected) =>
        Assert.Equal(expected,
            UpdateOverlayView.ProgressFraction(new FakeUpdateStatus { BytesDownloaded = done, TotalDownloadBytes = total }), 3);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayViewTests"`
Expected: FAIL to compile — `UpdateOverlayView` does not exist.

- [ ] **Step 3: Write the widget**

`KhaozEngine.Gui/UpdateOverlayView.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Reusable in-game update-notification overlay: a pure presenter over <see cref="IUpdateStatus"/>. It
/// renders the current update state (available / downloading / ready / applying / failed) as a centred panel
/// with a progress bar, and raises <see cref="OnTrigger"/> when the bound key/button is pressed while a panel
/// is shown. It never calls the service itself — wire <see cref="OnTrigger"/> to
/// <c>KhaozEngine.Updates.UpdateOverlayActions.Trigger</c>. Headless-testable: <see cref="Update"/> needs no
/// GPU. Drop it into any Gui layer, or use <see cref="UpdateOverlayScreen"/> for stack-based games.
/// </summary>
public sealed class UpdateOverlayView
{
    public UpdateOverlayTheme Theme { get; set; }

    /// <summary>Raised with the current state when the trigger key/button is pressed while visible.</summary>
    public event Action<UpdateState>? OnTrigger;
    /// <summary>Paramless convenience; raised alongside <see cref="OnTrigger"/>.</summary>
    public event Action? Triggered;

    float _alpha; // current fade, 0..1

    public UpdateOverlayView(UpdateOverlayTheme? theme = null) => Theme = theme ?? UpdateOverlayTheme.Default;

    /// <summary>Current fade alpha (0 hidden .. 1 shown); exposed for tests/diagnostics.</summary>
    public float Alpha => _alpha;

    /// <summary>States that show a panel (and are modal). Idle/Checking are hidden.</summary>
    public static bool IsVisible(UpdateState state) => state is
        UpdateState.UpdateAvailable or UpdateState.Downloading or UpdateState.ReadyToApply
        or UpdateState.Applying or UpdateState.Failed;

    /// <summary>Download progress 0..1, clamped; 0 when the total is unknown.</summary>
    public static float ProgressFraction(IUpdateStatus s)
    {
        if (s.TotalDownloadBytes <= 0) return 0f;
        float f = (float)s.BytesDownloaded / s.TotalDownloadBytes;
        return f < 0f ? 0f : f > 1f ? 1f : f;
    }

    /// <summary>
    /// Advance the fade, detect the trigger, and report whether the overlay is showing a panel (i.e. is modal
    /// / consumed input). Pass <see cref="InputState.Empty"/> to advance visuals without accepting input.
    /// </summary>
    public bool Update(IUpdateStatus status, InputState input, float dt)
    {
        bool visible = IsVisible(status.State);
        float target = visible ? 1f : 0f;
        float step = Theme.FadeSpeed * dt;
        _alpha = target > _alpha ? MathF.Min(target, _alpha + step) : MathF.Max(target, _alpha - step);

        if (visible && TriggerPressed(input))
        {
            OnTrigger?.Invoke(status.State);
            Triggered?.Invoke();
        }
        return visible;
    }

    bool TriggerPressed(InputState input)
    {
        if (input.WasPressed(Theme.TriggerKey)) return true;
        if (Theme.TriggerButton is { } btn)
        {
            GamepadState pad = input.PrimaryGamepad;
            if (pad.IsConnected && pad.WasPressed(btn)) return true;
        }
        return false;
    }

    /// <summary>Draw the panel centred in <paramref name="viewport"/>. No-op when the state is hidden.</summary>
    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport, IUpdateStatus status)
    {
        UpdateState state = status.State;
        if (!IsVisible(state)) return;
        float a = _alpha < 0f ? 0f : _alpha > 1f ? 1f : _alpha;

        float pad = Theme.PanelPadding;
        float titleH = font.LineHeight * Theme.TitleScale;
        float bodyH = font.LineHeight * Theme.BodyScale;
        float gap = pad * 0.5f;
        bool downloading = state == UpdateState.Downloading;
        float progressBlock = downloading ? gap + Theme.ProgressBarHeight : 0f;
        float h = pad + titleH + gap + bodyH + progressBlock + pad;

        float cx = viewport.X + viewport.Width * 0.5f;
        float cy = viewport.Y + viewport.Height * 0.5f;
        var panel = new Rect(cx - Theme.PanelWidth * 0.5f, cy - h * 0.5f, Theme.PanelWidth, h);

        GuiDraw.Fill(batch, white, viewport, Mul(Theme.DimFill, a));
        GuiDraw.Fill(batch, white, panel, Mul(Theme.PanelFill, a));
        GuiDraw.Border(batch, white, panel, Theme.BorderThickness, Mul(Theme.AccentFor(state), a));

        float titleY = panel.Y + pad;
        float bodyY = titleY + titleH + gap;
        DrawCentered(batch, font, Theme.TitleFor(state, status.RemoteVersion), titleY, Theme.TitleScale, Mul(Theme.AccentFor(state), a), panel);
        DrawCentered(batch, font, Theme.BodyFor(state, status), bodyY, Theme.BodyScale, Mul(Theme.BodyText, a), panel);

        if (downloading)
        {
            float barY = bodyY + bodyH + gap;
            float barX = panel.X + pad;
            float barW = panel.Width - pad * 2f;
            GuiDraw.Fill(batch, white, new Rect(barX, barY, barW, Theme.ProgressBarHeight), Mul(Theme.ProgressBackground, a));
            GuiDraw.Fill(batch, white, new Rect(barX, barY, barW * ProgressFraction(status), Theme.ProgressBarHeight), Mul(Theme.ProgressFill, a));
        }
    }

    static void DrawCentered(SpriteBatch batch, SpriteFont font, string text, float y, float scale, Vector4 color, Rect panel)
    {
        Vector2 size = font.Measure(text) * scale;
        var pos = new Vector2(panel.X + (panel.Width - size.X) * 0.5f, y);
        batch.DrawString(font, text, pos, (Color)color, scale);
    }

    static Vector4 Mul(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayViewTests"`
Expected: PASS (all `IsVisible`, trigger, fade, and `ProgressFraction` cases).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/UpdateOverlayView.cs KhaozEngine.Tests/Gui/OverlayTestInput.cs KhaozEngine.Tests/Gui/UpdateOverlayViewTests.cs
git commit -m "gui(updater-glue): UpdateOverlayView presenter widget"
```

---

## Task 8: `UpdateOverlayScreen` thin Screen wrapper

**Files:**
- Create: `KhaozEngine.Gui/UpdateOverlayScreen.cs`
- Create: `KhaozEngine.Tests/Gui/UpdateOverlayScreenTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Gui/UpdateOverlayScreenTests.cs`:

```csharp
using KhaozEngine.Gui;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;
using Xunit;

public class UpdateOverlayScreenTests
{
    [Fact]
    public void Fires_trigger_and_toggles_modality_with_visibility()
    {
        var status = new FakeUpdateStatus { State = UpdateState.Idle };
        // font/white are unused by Update (only Draw needs them); pass null!.
        var screen = new UpdateOverlayScreen(status, null!, null!, new DesignViewport(960, 540));
        int fired = 0;
        screen.Triggered += () => fired++;

        var stack = new ScreenStack();
        stack.Add(screen);

        // Idle: hidden -> passes update through, no trigger even with the key down.
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));
        Assert.True(screen.PassUpdateThrough);
        Assert.Equal(0, fired);

        // UpdateAvailable: modal -> blocks update-through, key fires the trigger.
        status.State = UpdateState.UpdateAvailable;
        stack.Update(0.016f, OverlayTestInput.KeyFrame(Key.U));
        Assert.False(screen.PassUpdateThrough);
        Assert.Equal(1, fired);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayScreenTests"`
Expected: FAIL to compile — `UpdateOverlayScreen` does not exist.

- [ ] **Step 3: Write the screen**

`KhaozEngine.Gui/UpdateOverlayScreen.cs`:

```csharp
using System;
using KhaozEngine.Render2D;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Drop-in <see cref="Screen"/> wrapping <see cref="UpdateOverlayView"/> for stack-based games. It reads
/// input from the owning <see cref="ScreenStack"/>, draws the overlay centred in the supplied design
/// viewport, and is modal only while a panel is showing (so the game below keeps updating when idle).
/// Re-exposes the view's <see cref="OnTrigger"/>/<see cref="Triggered"/> events.
/// </summary>
public sealed class UpdateOverlayScreen : Screen
{
    readonly IUpdateStatus _status;
    readonly SpriteFont _font;
    readonly Texture2D _white;
    readonly IDesignViewport _viewport;
    readonly UpdateOverlayView _view;

    /// <summary>Raised with the current state when the trigger fires (forwards the view's event).</summary>
    public event Action<UpdateState>? OnTrigger { add => _view.OnTrigger += value; remove => _view.OnTrigger -= value; }
    /// <summary>Paramless convenience (forwards the view's event).</summary>
    public event Action? Triggered { add => _view.Triggered += value; remove => _view.Triggered -= value; }

    /// <summary>The wrapped view (e.g. to retheme at runtime).</summary>
    public UpdateOverlayView View => _view;

    public UpdateOverlayScreen(IUpdateStatus status, SpriteFont font, Texture2D white,
        IDesignViewport viewport, UpdateOverlayTheme? theme = null)
    {
        _status = status;
        _font = font;
        _white = white;
        _viewport = viewport;
        _view = new UpdateOverlayView(theme);
        DrawOrder = 10_000;        // sits on top of game UI
        PassUpdateThrough = true;  // re-evaluated each frame from visibility
    }

    public override bool Update(float dt, bool receivesInput)
    {
        InputState input = receivesInput ? Manager.Input : InputState.Empty;
        bool visible = _view.Update(_status, input, dt);
        PassUpdateThrough = !visible; // modal only while a panel is shown
        return receivesInput && visible;
    }

    public override void Draw(SpriteBatch batch) =>
        _view.Draw(batch, _font, _white, new Rect(0, 0, _viewport.Width, _viewport.Height), _status);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~UpdateOverlayScreenTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Gui/UpdateOverlayScreen.cs KhaozEngine.Tests/Gui/UpdateOverlayScreenTests.cs
git commit -m "gui(updater-glue): UpdateOverlayScreen drop-in wrapper"
```

---

## Task 9: Publish template + bundle into the package

**Files:**
- Create: `KhaozEngine.Updates/templates/publish-update.sh`
- Modify: `KhaozEngine.Updates/KhaozEngine.Updates.csproj`

No unit test (shell template). Verified by a pack-content check.

- [ ] **Step 1: Write the template**

`KhaozEngine.Updates/templates/publish-update.sh`:

```bash
#!/usr/bin/env bash
# Publish a KhaozEngine delta update. COPY this into your game repo and fill in the CONFIG block.
# Flow: build -> generate manifest (ke-updater) -> sign (ke-updater) -> upload -> update latest-{platform}.json
#
# Prereqs: the `ke-updater` dotnet tool (dotnet tool install --global KhaozEngine.Updates.Tool) and,
# for the default Azure Blob backend, the `az` CLI authenticated to the target storage account.
set -euo pipefail

# ---- CONFIG: edit these for your game --------------------------------------
STORAGE_ACCOUNT="yourgameupdates"                          # Azure Blob storage account
CONTAINER="releases"                                       # blob container
PRIVATE_KEY="${UPDATE_PRIVATE_KEY:-secrets/private.pem}"   # RSA private key (keep secret; supply via CI secret)

# Map a runtime id to the directory holding the built game files to hash.
# Replace with your build output; handle the macOS .app bundle here if needed, e.g.:
#   osx-*) echo "package/$1/MyGame.app/Contents/MacOS" ;;
resolve_build_dir() {  # $1 = runtime id
  echo "artifacts/$1"
}

# Build the game for a runtime id (replace with your build command).
build_game() {         # $1 = runtime id
  echo "TODO: build $1" >&2
}
# ---------------------------------------------------------------------------

runtime_id="${1:?usage: publish-update.sh <runtime-id> <version>}"
version="${2:?usage: publish-update.sh <runtime-id> <version>}"

build_game "$runtime_id"
build_dir="$(resolve_build_dir "$runtime_id")"
[ -d "$build_dir" ] || { echo "build dir not found: $build_dir" >&2; exit 1; }

manifest="$build_dir/manifest.json"
ke-updater manifest --dir "$build_dir" --platform "$runtime_id" --version "$version" --output "$manifest"
ke-updater sign --manifest "$manifest" --key "$PRIVATE_KEY"

# Upload the whole build dir (game files + manifest.json + manifest.json.sig).
az storage blob upload-batch \
  --account-name "$STORAGE_ACCOUNT" \
  --destination "$CONTAINER/$version/$runtime_id" \
  --source "$build_dir" --overwrite

# Point latest-{platform}.json at this version.
tmp="$(mktemp)"; printf '{"version":"%s"}' "$version" > "$tmp"
az storage blob upload \
  --account-name "$STORAGE_ACCOUNT" --container-name "$CONTAINER" \
  --name "latest-$runtime_id.json" --file "$tmp" --content-type application/json --overwrite
rm -f "$tmp"

echo "Published $version for $runtime_id." >&2
```

- [ ] **Step 2: Mark it executable**

Run: `chmod +x KhaozEngine.Updates/templates/publish-update.sh`

- [ ] **Step 3: Bundle the template into the package**

In `KhaozEngine.Updates/KhaozEngine.Updates.csproj`, add a new `<ItemGroup>` after the existing README `<ItemGroup>`:

```xml
  <ItemGroup>
    <None Include="templates/publish-update.sh" Pack="true" PackagePath="templates/" />
  </ItemGroup>
```

- [ ] **Step 4: Verify the template is packed**

Run: `dotnet pack -c Release KhaozEngine.Updates/KhaozEngine.Updates.csproj -o ./local-feed && unzip -l local-feed/KhaozEngine.Updates.*.nupkg | grep publish-update.sh`
Expected: a line listing `templates/publish-update.sh` inside the nupkg.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Updates/templates/publish-update.sh KhaozEngine.Updates/KhaozEngine.Updates.csproj
git commit -m "updates(updater-glue): parameterized publish-update.sh template, bundled in package"
```

---

## Task 10: "Adopting the updater" README section

**Files:**
- Modify: `KhaozEngine.Updates/README.md`

No test (docs).

- [ ] **Step 1: Append the adoption section**

Append to the end of `KhaozEngine.Updates/README.md`:

```markdown
## Adopting the updater (last-mile glue)

Everything below ships in this package — adopting the updater means using the engine feature only.

### 1. Keys

Generate an RSA-2048 keypair once with the `ke-updater` dotnet tool:

```
dotnet tool install --global KhaozEngine.Updates.Tool
ke-updater genkey --out ./keys
```

`keys/private.pem` signs your manifests — keep it secret (a CI secret, never committed).
`keys/public.pem` is embedded in the client via `UpdateServiceOptions.TrustedPublicKeys` (ship more
than one to rotate keys).

### 2. In-game overlay

Add `UpdateOverlayScreen` (from `KhaozEngine.Gui`) to your screen stack, pointing it at the
`UpdateService` (which implements `IUpdateStatus`) and wiring its trigger to the default action helper:

```csharp
var overlay = new UpdateOverlayScreen(updateService, font, whiteTexture, viewport);
overlay.OnTrigger += _ => UpdateOverlayActions.Trigger(updateService);
screenStack.Add(overlay);
```

Retheme via `new UpdateOverlayTheme { ... }` (colours, labels, `TriggerKey`/`TriggerButton`) or
subclass it to override `TitleFor`/`BodyFor` for localized text. For non-stack UI, use the lower-level
`UpdateOverlayView` directly (`Update(status, input, dt)` + `Draw(batch, font, white, viewport, status)`).

### 3. The updater shim

Your external updater exe is one line:

```csharp
return KhaozEngine.Updates.UpdaterShim.Main(args);
```

Publish it per-RID with a game-specific name and set `UpdateServiceOptions.UpdaterExecutableName` to match.

### 4. Publish + feed layout

Copy `templates/publish-update.sh` (shipped in this package) into your repo, fill in the CONFIG block,
and run it per platform. It builds, generates + signs the manifest with `ke-updater`, uploads, and points
the latest pointer at the new version. The feed layout the client expects:

```
<feed-root>/
  latest-<platform>.json            -> {"version":"<v>"}
  <version>/<platform>/
    manifest.json
    manifest.json.sig
    <game files...>
```
```

- [ ] **Step 2: Commit**

```bash
git add KhaozEngine.Updates/README.md
git commit -m "docs(updater-glue): Adopting the updater README section"
```

---

## Task 11: Release 7.2.0

**Files:**
- Modify: `Directory.Build.props:18`
- Modify: `CHANGELOG.md`
- Modify: `CHANGENOTES.md`
- Modify: `docs/CONSUMERS.md:7`
- Modify: `docs/ROADMAP.md:3`
- Modify: `README.md` (PackageReference examples, lines ~120-123)

- [ ] **Step 1: Bump the shared version**

In `Directory.Build.props:18`, change:

```xml
<KhaozEngineVersion>7.1.0</KhaozEngineVersion>
```

to:

```xml
<KhaozEngineVersion>7.2.0</KhaozEngineVersion>
```

- [ ] **Step 2: Add the CHANGELOG entry**

Insert at the top of the entries in `CHANGELOG.md` (above the `## 7.1.0` heading):

```markdown
## 7.2.0

Additive (non-breaking): the auto-updater's reusable last-mile glue, so games adopt the updater with thin
per-game config only (feed URL, embedded public key, a one-line shim, a themed overlay).

- `KhaozEngine.Updates`: new read-only `IUpdateStatus` (implemented by `UpdateService`) so UI can present
  update state without the concrete service. `UpdaterShim.Main(args)` — a game's external updater exe is now
  one line (`return KhaozEngine.Updates.UpdaterShim.Main(args);`). `UpdateOverlayActions.Trigger(service)` +
  `ResolveAction(state)` — the default state→action wiring (`OverlayAction` enum). `ManifestToolCommands` —
  the command logic behind the new CLI.
- `KhaozEngine.Gui`: `UpdateOverlayView` (a headless-testable presenter over `IUpdateStatus` that raises
  `OnTrigger`/`Triggered` on a bound key/gamepad button) and `UpdateOverlayScreen` (a drop-in `Screen`
  wrapper, modal only while a panel is shown), themed via `UpdateOverlayTheme`. `KhaozEngine.Gui` now
  depends on `KhaozEngine.Updates` (pure .NET, acyclic).
- New `KhaozEngine.Updates.Tool` package: the `ke-updater` dotnet tool — `manifest`, `genkey`, `sign`,
  `verify` for RSA-2048 signed manifests. Wires the `--genkey`/`--sign` deferred in 7.0.0.
- `KhaozEngine.Updates` now bundles `templates/publish-update.sh` (a parameterized publish template) and a
  README "Adopting the updater" section. No change to the security model (signing stays mandatory; HTTPS +
  same-host; size/disk caps; fail-closed apply).
```

- [ ] **Step 3: Add the CHANGENOTES digest**

Insert at the top of the entries in `CHANGENOTES.md` (above the `- **7.1.0**` line):

```markdown
- **7.2.0**: Reusable auto-updater last-mile glue. New `IUpdateStatus` decouples UI from `UpdateService`; `KhaozEngine.Gui` gains `UpdateOverlayView` + `UpdateOverlayScreen` (themeable via `UpdateOverlayTheme`, raises events, wired to the new `UpdateOverlayActions.Trigger`); `UpdaterShim.Main` makes a game's updater exe a one-liner; the new `KhaozEngine.Updates.Tool` (`ke-updater`) dotnet tool does manifest/genkey/sign/verify; and a parameterized `publish-update.sh` template + "Adopting the updater" docs ship in the Updates package. Gui now depends on Updates. Security model unchanged.
```

- [ ] **Step 4: Update the three doc-version declarations**

In `docs/CONSUMERS.md:7`, change the engine version from `7.1.0` to `7.2.0`. While there, add `KhaozEngine.Updates.Tool` (the `ke-updater` dotnet tool) to the package list/count narrative in that file.

In `docs/ROADMAP.md:3`, change `Current released version: **7.1.0**` to `Current released version: **7.2.0**`.

In `README.md`, change every `KhaozEngine.*` `<PackageReference ... Version="7.1.0" />` example to `Version="7.2.0"` (the four umbrella lines: Game2D, Game3D, Server, Foundation).

- [ ] **Step 5: Run the doc-version guard**

Run: `./scripts/check-doc-versions.sh`
Expected: exits 0 (no drift).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all green (existing suite + the new overlay/shim/tool/actions tests).

- [ ] **Step 7: Pack all packages to local-feed**

Run: `dotnet pack -c Release -o ./local-feed`
Expected: succeeds; `local-feed` now contains 7.2.0 nupkgs including `KhaozEngine.Updates.7.2.0.nupkg`,
`KhaozEngine.Gui.7.2.0.nupkg`, and `KhaozEngine.Updates.Tool.7.2.0.nupkg`.

- [ ] **Step 8: Commit**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "updates(7.2.0): centralize auto-updater last-mile glue (overlay, shim, ke-updater tool, publish template)"
```

- [ ] **Step 9: Tag + push (finishing step)**

Tagging `v7.2.0` and pushing `main` + the tag happens at branch-finish (see the finishing-a-development-branch flow), not mid-plan. CI publishes to GitHub Packages on the `v*` tag.

---

## Self-Review

**Spec coverage:**
- Deliverable 1 (overlay UI in Gui): Tasks 1 (`IUpdateStatus`), 6 (`UpdateOverlayTheme`), 7 (`UpdateOverlayView`), 8 (`UpdateOverlayScreen`), 5 (`UpdateOverlayActions` convenience). ✓
- Deliverable 2 (reusable shim entry): Task 2 (`UpdaterShim`). ✓
- Deliverable 3 (signing + publish CLI, dotnet tool `ke-updater`): Tasks 3 (command logic) + 4 (tool package). ✓
- Deliverable 4 (publish template + adoption docs): Tasks 9 (template) + 10 (README). ✓
- Release ritual (7.2.0, CHANGELOG/CHANGENOTES, 3 doc declarations, pack, tag): Task 11. ✓
- Constraints — headless tests for new logic (Tasks 1,2,3,5,6,7,8); no security-model change (additive only); Gui→Updates confirmed. ✓

**Placeholder scan:** No TBD/TODO in code or steps. (The template's `build_game`/`resolve_build_dir` contain a literal `TODO:` echo — that is intentional template content the adopting game fills in, not a plan gap.)

**Type consistency:** `IUpdateStatus` members match across Tasks 1/6/7. `OverlayAction`/`ResolveAction`/`Trigger` consistent (Task 5). `UpdateOverlayView.IsVisible`/`ProgressFraction`/`OnTrigger`/`Triggered`/`Alpha` used identically in Tasks 7/8. `ManifestToolCommands.Run(string[], TextWriter, TextWriter)` consistent across Tasks 3/4. `UpdateOverlayTheme.TitleFor/BodyFor/AccentFor/TriggerKey/TriggerButton/TriggerKeyLabel/FadeSpeed` consistent across Tasks 6/7. `ke-updater` command name consistent (Tasks 4/9/10).
```
