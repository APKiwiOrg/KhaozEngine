# KhaozEngine.Audio Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Promote Nullwake's OS-selected music backend (MonoGame `MediaPlayer` + a macOS `AVAudioPlayer` ObjC P/Invoke bridge) into a new game-agnostic `KhaozEngine.Audio` package, with the hardcoded track list parameterized and logging routed through `KhaozEngine.Diagnostics`.

**Architecture:** `AudioSystem` (public) owns volume/enable/auto-rotation state and delegates playback to an `IMusicBackend` (now public). The default constructor picks a backend by OS (`MacOsMusicBackend` on macOS, else `MonoGameMusicBackend`); a second constructor injects a backend for tests or custom platforms. Tracks are caller-registered (constructor seed + additive `RegisterTracks`). Logging is a constructor-injected `ILogger` defaulting to the `Log.For<AudioSystem>()` facade. The two real backends are thin, untested platform shims; `AudioSystem`'s logic is covered headlessly against a `FakeMusicBackend`.

**Tech Stack:** net10.0, C# latest, MonoGame.Framework.DesktopGL 3.8.*, KhaozEngine.Diagnostics, xUnit.

**Working directory:** worktree `/Users/antonio/KhaozEngine/.claude/worktrees/item7-audio` on branch `worktree-item7-audio`. All paths below are relative to it.

---

## File Structure

Created:
- `KhaozEngine.Audio/KhaozEngine.Audio.csproj` — package definition
- `KhaozEngine.Audio/README.md` — package readme (packed)
- `KhaozEngine.Audio/IMusicBackend.cs` — public backend interface (the seam)
- `KhaozEngine.Audio/MacOsMusicPlayer.cs` — internal AVAudioPlayer ObjC P/Invoke bridge
- `KhaozEngine.Audio/MacOsMusicBackend.cs` — internal macOS backend (file-path .mp3 playback)
- `KhaozEngine.Audio/MonoGameMusicBackend.cs` — internal MonoGame `Song`/`MediaPlayer` backend
- `KhaozEngine.Audio/AudioSystem.cs` — public orchestrator (volume/enable/rotation)
- `KhaozEngine.Tests/Audio/FakeMusicBackend.cs` — test double implementing `IMusicBackend`
- `KhaozEngine.Tests/Audio/StubServiceProvider.cs` — minimal `IServiceProvider` for a headless `ContentManager`
- `KhaozEngine.Tests/Audio/AudioSystemTests.cs` — headless behaviour tests

Modified:
- `KhaozEngine.slnx` — add the `KhaozEngine.Audio` project
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add a `ProjectReference` to `KhaozEngine.Audio`

> Note: this is a promotion of already-working code. Backends are lifted near-verbatim
> (only namespace + logger change) and are not unit-testable (P/Invoke + MonoGame `Song`).
> The new/changed logic — track parameterization, register-after-load guard, backend and
> logger injection — is what the headless tests in Task 8 exercise.

---

### Task 1: Scaffold the KhaozEngine.Audio package

**Files:**
- Create: `KhaozEngine.Audio/KhaozEngine.Audio.csproj`
- Create: `KhaozEngine.Audio/README.md`
- Modify: `KhaozEngine.slnx`

- [ ] **Step 1: Create the csproj**

`KhaozEngine.Audio/KhaozEngine.Audio.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Audio</PackageId>
    <Description>Game-agnostic background-music backend for MonoGame: an OS-selected music backend (macOS AVAudioPlayer via ObjC P/Invoke to dodge MonoGame's broken Song backend; MonoGame MediaPlayer elsewhere) behind a caller-registered track list with volume, enable, and automatic track rotation.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.*" />
    <ProjectReference Include="../KhaozEngine.Diagnostics/KhaozEngine.Diagnostics.csproj" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the README**

`KhaozEngine.Audio/README.md`:

```markdown
# KhaozEngine.Audio

Game-agnostic background music for MonoGame games. Works around MonoGame's broken
`Song`/`MediaPlayer` backend on macOS by using an `AVAudioPlayer` bridge (ObjC P/Invoke);
uses MonoGame `MediaPlayer` everywhere else. The backend is chosen automatically by OS.

## Quick start

```csharp
using KhaozEngine.Audio;

var audio = new AudioSystem(new[]
{
    "Music/track_one",
    "Music/track_two",
})
{
    MasterVolume = 0.66f,
    MusicVolume = 0.4f,
    MusicEnabled = true,
};

// or register additively before LoadContent:
audio.RegisterTrack("Music/track_three");

audio.LoadContent(Content);   // MonoGame ContentManager
// each frame:
audio.Update();               // advances to a new random track when the current ends
// on shutdown:
audio.Dispose();
```

## Notes

- Track names are content-pipeline asset names (no extension), e.g. `Music/foo`.
  The MonoGame backend loads `foo.xnb` via the pipeline; the macOS backend plays the
  raw `foo.mp3` from the content directory. Ship both for cross-platform builds.
- Logging routes through `KhaozEngine.Diagnostics`. Pass an `ILogger` to the
  constructor, or leave it null to use the ambient `Log` facade (a no-op until the
  game calls `Log.Configure(...)`).
- Custom platforms (e.g. iOS) can supply their own `IMusicBackend` via the
  `AudioSystem(IMusicBackend, ...)` constructor.
```

- [ ] **Step 3: Register the project in the solution**

In `KhaozEngine.slnx`, add this line immediately after the `KhaozEngine.App` line:

```xml
  <Project Path="KhaozEngine.Audio/KhaozEngine.Audio.csproj" />
```

- [ ] **Step 4: Restore/build the empty package**

Run: `dotnet build KhaozEngine.Audio/KhaozEngine.Audio.csproj --nologo`
Expected: build succeeds (a package with no source files compiles to an empty assembly).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Audio/KhaozEngine.Audio.csproj KhaozEngine.Audio/README.md KhaozEngine.slnx
git commit -m "Scaffold KhaozEngine.Audio package"
```

---

### Task 2: Lift IMusicBackend (public seam)

**Files:**
- Create: `KhaozEngine.Audio/IMusicBackend.cs`

- [ ] **Step 1: Create the interface**

`KhaozEngine.Audio/IMusicBackend.cs` (was `internal` in Nullwake; now `public` and doc-commented; members unchanged):

```csharp
using System;
using Microsoft.Xna.Framework.Content;

namespace KhaozEngine.Audio;

/// <summary>
/// A platform music backend: loads named tracks and plays one at a time with volume control.
/// Implemented by the bundled MonoGame and macOS backends; games or tests may supply their own.
/// </summary>
public interface IMusicBackend : IDisposable
{
    /// <summary>Human-readable backend name (used in logs).</summary>
    string Name { get; }

    /// <summary>Number of tracks successfully loaded.</summary>
    int TrackCount { get; }

    /// <summary>True while a track is currently playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Attempts to load one track. Returns false if it could not be loaded.</summary>
    bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex);

    /// <summary>Attempts to start the track at <paramref name="trackIndex"/> at the given volume.</summary>
    bool TryPlayTrack(int trackIndex, float volume);

    /// <summary>Stops playback.</summary>
    void Stop();

    /// <summary>Sets output volume (0.0 - 1.0).</summary>
    void SetVolume(float volume);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build KhaozEngine.Audio/KhaozEngine.Audio.csproj --nologo`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Audio/IMusicBackend.cs
git commit -m "Add public IMusicBackend to KhaozEngine.Audio"
```

---

### Task 3: Lift MacOsMusicPlayer (ObjC P/Invoke bridge)

**Files:**
- Create: `KhaozEngine.Audio/MacOsMusicPlayer.cs`

Lifted verbatim from Nullwake except: namespace `KhaozEngine.Audio`; the
`using Nullwake.Core.Engine;` line is dropped and replaced with
`using KhaozEngine.Diagnostics;`; an injected `ILogger` field replaces the static
`GameLogger` (3 `GameLogger.Warn(...)` call sites become `_logger.Warn(...)`). The static
class-handle/selector setup and all `[DllImport]` signatures are unchanged.

- [ ] **Step 1: Create the file**

`KhaozEngine.Audio/MacOsMusicPlayer.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>
/// Minimal AVAudioPlayer bridge for macOS DesktopGL music playback.
/// Avoids MonoGame's Song backend, which is currently unstable on macOS.
/// </summary>
internal sealed class MacOsMusicPlayer : IDisposable
{
    private static readonly IntPtr AutoreleasePoolClass;
    private static readonly IntPtr NSStringClass;
    private static readonly IntPtr NSURLClass;
    private static readonly IntPtr AVAudioPlayerClass;

    private static readonly IntPtr SelAlloc;
    private static readonly IntPtr SelDrain;
    private static readonly IntPtr SelFileUrlWithPath;
    private static readonly IntPtr SelInit;
    private static readonly IntPtr SelInitWithContentsOfUrlError;
    private static readonly IntPtr SelInitWithUtf8String;
    private static readonly IntPtr SelIsPlaying;
    private static readonly IntPtr SelLocalizedDescription;
    private static readonly IntPtr SelPlay;
    private static readonly IntPtr SelPrepareToPlay;
    private static readonly IntPtr SelRelease;
    private static readonly IntPtr SelSetNumberOfLoops;
    private static readonly IntPtr SelSetVolume;
    private static readonly IntPtr SelStop;
    private static readonly IntPtr SelUtf8String;

    private readonly ILogger _logger;
    private IntPtr _player;

    static MacOsMusicPlayer()
    {
        NativeLibrary.TryLoad("/System/Library/Frameworks/Foundation.framework/Foundation", out _);
        NativeLibrary.TryLoad("/System/Library/Frameworks/AVFoundation.framework/AVFoundation", out _);

        AutoreleasePoolClass = objc_getClass("NSAutoreleasePool");
        NSStringClass = objc_getClass("NSString");
        NSURLClass = objc_getClass("NSURL");
        AVAudioPlayerClass = objc_getClass("AVAudioPlayer");

        SelAlloc = sel_registerName("alloc");
        SelDrain = sel_registerName("drain");
        SelFileUrlWithPath = sel_registerName("fileURLWithPath:");
        SelInit = sel_registerName("init");
        SelInitWithContentsOfUrlError = sel_registerName("initWithContentsOfURL:error:");
        SelInitWithUtf8String = sel_registerName("initWithUTF8String:");
        SelIsPlaying = sel_registerName("isPlaying");
        SelLocalizedDescription = sel_registerName("localizedDescription");
        SelPlay = sel_registerName("play");
        SelPrepareToPlay = sel_registerName("prepareToPlay");
        SelRelease = sel_registerName("release");
        SelSetNumberOfLoops = sel_registerName("setNumberOfLoops:");
        SelSetVolume = sel_registerName("setVolume:");
        SelStop = sel_registerName("stop");
        SelUtf8String = sel_registerName("UTF8String");
    }

    public MacOsMusicPlayer(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsPlaying
    {
        get
        {
            if (_player == IntPtr.Zero)
            {
                return false;
            }

            return SendByte(_player, SelIsPlaying) != 0;
        }
    }

    public bool Play(string path, float volume)
    {
        Stop();

        if (AVAudioPlayerClass == IntPtr.Zero)
        {
            _logger.Warn("Audio: AVAudioPlayer class not available");
            return false;
        }

        IntPtr pool = CreateAutoreleasePool();
        try
        {
            IntPtr nsPath = CreateNSString(path);
            try
            {
                IntPtr url = SendIntPtrIntPtr(NSURLClass, SelFileUrlWithPath, nsPath);
                IntPtr candidate = SendIntPtr(AVAudioPlayerClass, SelAlloc);
                candidate = SendIntPtrIntPtrOutIntPtr(candidate, SelInitWithContentsOfUrlError, url, out IntPtr error);

                if (candidate == IntPtr.Zero)
                {
                    _logger.Warn($"Audio: AVAudioPlayer init failed: {GetErrorDescription(error)}");
                    return false;
                }

                _player = candidate;
                SendVoidLong(_player, SelSetNumberOfLoops, 0);
                SendVoidFloat(_player, SelSetVolume, volume);
                SendByte(_player, SelPrepareToPlay);

                if (SendByte(_player, SelPlay) == 0)
                {
                    _logger.Warn("Audio: AVAudioPlayer play returned false");
                    Stop();
                    return false;
                }

                return true;
            }
            finally
            {
                Release(nsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Audio: AVAudioPlayer bridge failed: {ex.Message}");
            Stop();
            return false;
        }
        finally
        {
            DrainAutoreleasePool(pool);
        }
    }

    public void SetVolume(float volume)
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        SendVoidFloat(_player, SelSetVolume, volume);
    }

    public void Stop()
    {
        if (_player == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SendVoid(_player, SelStop);
        }
        catch
        {
            // Best-effort cleanup.
        }

        Release(_player);
        _player = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
    }

    private static IntPtr CreateAutoreleasePool()
    {
        if (AutoreleasePoolClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr pool = SendIntPtr(AutoreleasePoolClass, SelAlloc);
        return SendIntPtr(pool, SelInit);
    }

    private static IntPtr CreateNSString(string value)
    {
        IntPtr nsString = SendIntPtr(NSStringClass, SelAlloc);
        return SendIntPtrString(nsString, SelInitWithUtf8String, value);
    }

    private static void DrainAutoreleasePool(IntPtr pool)
    {
        if (pool != IntPtr.Zero)
        {
            SendVoid(pool, SelDrain);
        }
    }

    private static string GetErrorDescription(IntPtr error)
    {
        if (error == IntPtr.Zero)
        {
            return "unknown error";
        }

        IntPtr description = SendIntPtr(error, SelLocalizedDescription);
        if (description == IntPtr.Zero)
        {
            return "unknown error";
        }

        IntPtr utf8 = SendIntPtr(description, SelUtf8String);
        return utf8 == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(utf8) ?? "unknown error";
    }

    private static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            SendVoid(handle, SelRelease);
        }
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrIntPtrOutIntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, out IntPtr arg2);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern byte SendByte(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidFloat(IntPtr receiver, IntPtr selector, float value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendVoidLong(IntPtr receiver, IntPtr selector, long value);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build KhaozEngine.Audio/KhaozEngine.Audio.csproj --nologo`
Expected: build succeeds. (`SelInit` is assigned but unused — same as the original; the `1591`/unused patterns are not errors here.)

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Audio/MacOsMusicPlayer.cs
git commit -m "Lift MacOsMusicPlayer AVAudioPlayer bridge into KhaozEngine.Audio"
```

---

### Task 4: Lift the two backends (macOS + MonoGame)

**Files:**
- Create: `KhaozEngine.Audio/MacOsMusicBackend.cs`
- Create: `KhaozEngine.Audio/MonoGameMusicBackend.cs`

Both lifted verbatim except namespace, the dropped `using Nullwake.Core.Engine;`, an
injected `ILogger`, and `GameLogger.*` → `_logger.*`.

- [ ] **Step 1: Create MacOsMusicBackend**

`KhaozEngine.Audio/MacOsMusicBackend.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

internal sealed class MacOsMusicBackend : IMusicBackend
{
    private readonly List<string> _trackPaths = [];
    private readonly ILogger _logger;
    private readonly MacOsMusicPlayer _player;

    public MacOsMusicBackend(ILogger logger)
    {
        _logger = logger;
        _player = new MacOsMusicPlayer(logger);
    }

    public string Name => "macOS AVAudioPlayer";

    public int TrackCount => _trackPaths.Count;

    public bool IsPlaying => _player.IsPlaying;

    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex)
    {
        string mp3Path = Path.Combine(contentDirectory, trackName + ".mp3");
        _logger.Info($"Audio: loading track {trackIndex}: {mp3Path}");

        if (!File.Exists(mp3Path))
        {
            _logger.Warn($"Audio: track {trackIndex} not found at {mp3Path}");
            return false;
        }

        _trackPaths.Add(mp3Path);
        return true;
    }

    public bool TryPlayTrack(int trackIndex, float volume)
    {
        return _player.Play(_trackPaths[trackIndex], volume);
    }

    public void Stop()
    {
        _player.Stop();
    }

    public void SetVolume(float volume)
    {
        _player.SetVolume(volume);
    }

    public void Dispose()
    {
        _player.Dispose();
    }
}
```

- [ ] **Step 2: Create MonoGameMusicBackend**

`KhaozEngine.Audio/MonoGameMusicBackend.cs`:

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Media;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

internal sealed class MonoGameMusicBackend : IMusicBackend
{
    private readonly List<Song> _tracks = [];
    private readonly ILogger _logger;

    public MonoGameMusicBackend(ILogger logger)
    {
        _logger = logger;
    }

    public string Name => "MonoGame MediaPlayer";

    public int TrackCount => _tracks.Count;

    public bool IsPlaying
    {
        get
        {
            MediaState state = MediaPlayer.State;
            return state != MediaState.Stopped;
        }
    }

    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex)
    {
        try
        {
            _logger.Info($"Audio: loading track {trackIndex}: {trackName}");
            _tracks.Add(content.Load<Song>(trackName));
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Audio: track {trackIndex} failed: {ex.Message}");
            return false;
        }
    }

    public bool TryPlayTrack(int trackIndex, float volume)
    {
        MediaPlayer.IsRepeating = false;
        MediaPlayer.Play(_tracks[trackIndex]);
        MediaPlayer.Volume = volume;
        return true;
    }

    public void Stop()
    {
        MediaPlayer.Stop();
    }

    public void SetVolume(float volume)
    {
        MediaPlayer.Volume = volume;
    }

    public void Dispose()
    {
        try
        {
            MediaPlayer.Stop();
        }
        catch
        {
            // Best-effort shutdown.
        }

        for (int i = 0; i < _tracks.Count; i++)
        {
            try
            {
                _tracks[i].Dispose();
            }
            catch
            {
                // Best-effort shutdown.
            }
        }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build KhaozEngine.Audio/KhaozEngine.Audio.csproj --nologo`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Audio/MacOsMusicBackend.cs KhaozEngine.Audio/MonoGameMusicBackend.cs
git commit -m "Lift macOS + MonoGame music backends into KhaozEngine.Audio"
```

---

### Task 5: Lift AudioSystem with parameterized tracks, injected logger, injectable backend

**Files:**
- Create: `KhaozEngine.Audio/AudioSystem.cs`

Changes from Nullwake's `AudioSystem`: namespace; drop the static `TrackNames` array;
hold a caller-populated `List<string> _trackNames`; two constructors (default OS-backend +
injected backend); `RegisterTrack`/`RegisterTracks` (throw after load); injected `ILogger`
(default `Log.For<AudioSystem>()`); `Engine.GameLogger.Info` → `_logger.Info`. The volume /
`MusicEnabled` / `PlayRandomTrack` / `Update` / `Dispose` / `SetRng` / `ApplyVolume` logic is
unchanged.

- [ ] **Step 1: Create the file**

`KhaozEngine.Audio/AudioSystem.cs`:

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

/// <summary>
/// Manages background music playback and volume settings.
/// Uses a platform-specific music backend to provide volume control,
/// enable/disable behavior, and automatic track rotation.
/// </summary>
public sealed class AudioSystem : IDisposable
{
    private readonly IMusicBackend _backend;
    private readonly ILogger _logger;
    private readonly List<string> _trackNames;
    private Random _rng = new();
    private float _masterVolume = 0.66f;
    private float _musicVolume = 0.4f;
    private int _lastTrackIndex = -1;
    private bool _available = true;
    private bool _loaded;
    private bool _started;
    private bool _musicEnabled = true;

    /// <summary>
    /// Creates an audio system using the backend for the current OS
    /// (macOS AVAudioPlayer, otherwise MonoGame MediaPlayer).
    /// </summary>
    /// <param name="trackNames">Optional initial track names (content asset names, no extension).</param>
    /// <param name="logger">Optional logger; defaults to the ambient <c>Log</c> facade.</param>
    public AudioSystem(IEnumerable<string>? trackNames = null, ILogger? logger = null)
        : this(CreateBackend(logger ?? Log.For<AudioSystem>()), trackNames, logger)
    {
    }

    /// <summary>
    /// Creates an audio system with a caller-supplied backend (tests or custom platforms).
    /// </summary>
    /// <param name="backend">The music backend to drive.</param>
    /// <param name="trackNames">Optional initial track names (content asset names, no extension).</param>
    /// <param name="logger">Optional logger; defaults to the ambient <c>Log</c> facade.</param>
    public AudioSystem(IMusicBackend backend, IEnumerable<string>? trackNames = null, ILogger? logger = null)
    {
        _logger = logger ?? Log.For<AudioSystem>();
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _trackNames = trackNames is null ? new List<string>() : new List<string>(trackNames);
    }

    /// <summary>Adds a track to load. Must be called before <see cref="LoadContent"/>.</summary>
    public void RegisterTrack(string trackName)
    {
        if (_loaded)
        {
            throw new InvalidOperationException("Cannot register tracks after LoadContent has been called.");
        }

        _trackNames.Add(trackName);
    }

    /// <summary>Adds several tracks to load. Must be called before <see cref="LoadContent"/>.</summary>
    public void RegisterTracks(IEnumerable<string> trackNames)
    {
        foreach (string trackName in trackNames)
        {
            RegisterTrack(trackName);
        }
    }

    /// <summary>Replaces the track-shuffle RNG with a seeded instance.</summary>
    public void SetRng(Random rng) { _rng = rng; }

    /// <summary>Master volume (0.0 - 1.0). Scales all audio output.</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = MathHelper.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    /// <summary>Whether background music is enabled. Toggling stops/starts playback without changing volume.</summary>
    public bool MusicEnabled
    {
        get => _musicEnabled;
        set
        {
            if (_musicEnabled == value) return;
            _musicEnabled = value;
            if (!_available || !_loaded) return;
            try
            {
                if (_musicEnabled)
                {
                    PlayRandomTrack();
                }
                else
                {
                    _backend.Stop();
                }
            }
            catch (Exception)
            {
                _available = false;
            }
        }
    }

    /// <summary>Music volume (0.0 - 1.0). Scaled by master volume.</summary>
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = MathHelper.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    /// <summary>
    /// Loads all registered music tracks for the active platform backend.
    /// </summary>
    public void LoadContent(ContentManager content)
    {
        string contentDirectory = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, content.RootDirectory);
        _logger.Info($"Audio: using {_backend.Name} backend");

        for (int i = 0; i < _trackNames.Count; i++)
        {
            _backend.TryLoadTrack(content, contentDirectory, _trackNames[i], i);
        }

        _logger.Info($"Audio: {_backend.TrackCount}/{_trackNames.Count} tracks loaded");
        _loaded = true;

        // Apply volume that was set during construction (before native audio was ready)
        ApplyVolume();
    }

    /// <summary>
    /// Plays a random track, avoiding the same track twice in a row.
    /// </summary>
    public void PlayRandomTrack()
    {
        int trackCount = _backend.TrackCount;
        if (trackCount == 0 || !_available || !_musicEnabled) return;

        try
        {
            int index;
            if (trackCount == 1)
            {
                index = 0;
            }
            else
            {
                do
                {
                    index = _rng.Next(trackCount);
                } while (index == _lastTrackIndex);
            }

            _lastTrackIndex = index;
            if (!_backend.TryPlayTrack(index, _masterVolume * _musicVolume))
            {
                _available = false;
                return;
            }

            ApplyVolume();
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    /// <summary>
    /// Call each frame to detect when the current track ends and queue the next.
    /// Defers first playback to the first Update call so the audio subsystem is ready.
    /// </summary>
    public void Update()
    {
        if (_backend.TrackCount == 0 || !_available || !_musicEnabled) return;

        if (!_started)
        {
            _started = true;
            PlayRandomTrack();
            return;
        }

        try
        {
            if (!_backend.IsPlaying)
            {
                PlayRandomTrack();
            }
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _backend.Dispose();
    }

    private static IMusicBackend CreateBackend(ILogger logger)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsMusicBackend(logger);
        }

        return new MonoGameMusicBackend(logger);
    }

    private void ApplyVolume()
    {
        if (!_available || !_loaded) return;
        try
        {
            _backend.SetVolume(_masterVolume * _musicVolume);
        }
        catch (Exception)
        {
            _available = false;
        }
    }
}
```

- [ ] **Step 2: Build the package**

Run: `dotnet build KhaozEngine.Audio/KhaozEngine.Audio.csproj --nologo`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Audio/AudioSystem.cs
git commit -m "Lift AudioSystem with parameterized tracks + injected logger/backend"
```

---

### Task 6: Wire the test project + add test doubles

**Files:**
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Create: `KhaozEngine.Tests/Audio/FakeMusicBackend.cs`
- Create: `KhaozEngine.Tests/Audio/StubServiceProvider.cs`

- [ ] **Step 1: Add the project reference**

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add this line immediately after the
`KhaozEngine.App` `ProjectReference`:

```xml
    <ProjectReference Include="../KhaozEngine.Audio/KhaozEngine.Audio.csproj" />
```

- [ ] **Step 2: Create the fake backend**

`KhaozEngine.Tests/Audio/FakeMusicBackend.cs`:

```csharp
using System.Collections.Generic;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Audio;

namespace KhaozEngine.Tests;

/// <summary>In-memory <see cref="IMusicBackend"/> recording calls for headless AudioSystem tests.</summary>
internal sealed class FakeMusicBackend : IMusicBackend
{
    public List<string> LoadedTracks { get; } = new();
    public List<int> PlayedIndices { get; } = new();
    public List<float> Volumes { get; } = new();
    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }
    public bool LoadSucceeds { get; set; } = true;
    public bool PlaySucceeds { get; set; } = true;

    public string Name => "Fake";
    public int TrackCount => LoadedTracks.Count;
    public bool IsPlaying { get; set; }

    public bool TryLoadTrack(ContentManager content, string contentDirectory, string trackName, int trackIndex)
    {
        if (!LoadSucceeds) return false;
        LoadedTracks.Add(trackName);
        return true;
    }

    public bool TryPlayTrack(int trackIndex, float volume)
    {
        if (!PlaySucceeds) return false;
        PlayedIndices.Add(trackIndex);
        Volumes.Add(volume);
        IsPlaying = true;
        return true;
    }

    public void Stop()
    {
        StopCount++;
        IsPlaying = false;
    }

    public void SetVolume(float volume) => Volumes.Add(volume);

    public void Dispose() => Disposed = true;
}
```

- [ ] **Step 3: Create the stub service provider**

`KhaozEngine.Tests/Audio/StubServiceProvider.cs`:

```csharp
using System;

namespace KhaozEngine.Tests;

/// <summary>A no-service <see cref="IServiceProvider"/> so a headless ContentManager can be constructed.</summary>
internal sealed class StubServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
```

- [ ] **Step 4: Build the test project**

Run: `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj --nologo`
Expected: build succeeds (no tests reference the doubles yet, but they must compile).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/Audio/FakeMusicBackend.cs KhaozEngine.Tests/Audio/StubServiceProvider.cs
git commit -m "Wire KhaozEngine.Audio into tests + add FakeMusicBackend/StubServiceProvider"
```

---

### Task 7: AudioSystem behaviour tests

**Files:**
- Create: `KhaozEngine.Tests/Audio/AudioSystemTests.cs`

> These exercise the new/changed logic against the fake backend. Because `AudioSystem`
> is already implemented (Task 5), they are expected to PASS on first run — the value is
> proving the track parameterization, register guard, rotation, volume scaling, enable
> toggle, Update progression, availability flip, and dispose all behave as designed.

- [ ] **Step 1: Write the test file**

`KhaozEngine.Tests/Audio/AudioSystemTests.cs`:

```csharp
using System;
using Microsoft.Xna.Framework.Content;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class AudioSystemTests
{
    private static (AudioSystem audio, FakeMusicBackend backend) NewLoaded(params string[] tracks)
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, tracks);
        audio.LoadContent(new ContentManager(new StubServiceProvider()));
        return (audio, backend);
    }

    [Fact]
    public void RegistersTracksFromCtorAndRegisterApis()
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, new[] { "a", "b" });
        audio.RegisterTrack("c");
        audio.RegisterTracks(new[] { "d", "e" });

        audio.LoadContent(new ContentManager(new StubServiceProvider()));

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, backend.LoadedTracks);
        Assert.Equal(5, backend.TrackCount);
    }

    [Fact]
    public void RegisterAfterLoadThrows()
    {
        var (audio, _) = NewLoaded("a");

        Assert.Throws<InvalidOperationException>(() => audio.RegisterTrack("b"));
        Assert.Throws<InvalidOperationException>(() => audio.RegisterTracks(new[] { "b" }));
    }

    [Fact]
    public void NullBackendThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new AudioSystem((IMusicBackend)null!));
    }

    [Fact]
    public void DefaultCtorHasExpectedDefaults()
    {
        var audio = new AudioSystem(new[] { "x" });

        Assert.Equal(0.66f, audio.MasterVolume);
        Assert.Equal(0.4f, audio.MusicVolume);
        Assert.True(audio.MusicEnabled);
    }

    [Fact]
    public void PlayRandomTrackNeverRepeatsPreviousIndex()
    {
        var (audio, backend) = NewLoaded("a", "b", "c");
        audio.SetRng(new Random(12345));

        for (int i = 0; i < 200; i++)
        {
            backend.IsPlaying = false;
            audio.PlayRandomTrack();
        }

        Assert.True(backend.PlayedIndices.Count > 100);
        for (int i = 1; i < backend.PlayedIndices.Count; i++)
        {
            Assert.NotEqual(backend.PlayedIndices[i - 1], backend.PlayedIndices[i]);
        }
    }

    [Fact]
    public void SingleTrackAlwaysPlaysIndexZero()
    {
        var (audio, backend) = NewLoaded("only");

        for (int i = 0; i < 5; i++)
        {
            audio.PlayRandomTrack();
        }

        Assert.Equal(5, backend.PlayedIndices.Count);
        Assert.All(backend.PlayedIndices, idx => Assert.Equal(0, idx));
    }

    [Fact]
    public void PlayUsesMasterTimesMusicVolume()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.MasterVolume = 0.5f;
        audio.MusicVolume = 0.4f;
        backend.Volumes.Clear();

        audio.PlayRandomTrack();

        Assert.Contains(backend.Volumes, v => Math.Abs(v - 0.2f) < 1e-4f);
    }

    [Fact]
    public void VolumeIsClampedToUnitRange()
    {
        var (audio, _) = NewLoaded("a");

        audio.MasterVolume = 5f;
        audio.MusicVolume = 5f;
        Assert.Equal(1f, audio.MasterVolume);
        Assert.Equal(1f, audio.MusicVolume);

        audio.MasterVolume = -5f;
        Assert.Equal(0f, audio.MasterVolume);
    }

    [Fact]
    public void DisablingMusicStopsBackend()
    {
        var (audio, backend) = NewLoaded("a", "b");

        audio.MusicEnabled = false;

        Assert.Equal(1, backend.StopCount);
    }

    [Fact]
    public void EnablingMusicStartsPlayback()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.MusicEnabled = false;
        backend.PlayedIndices.Clear();

        audio.MusicEnabled = true;

        Assert.NotEmpty(backend.PlayedIndices);
    }

    [Fact]
    public void UpdateDefersFirstPlayThenAdvancesWhenStopped()
    {
        var (audio, backend) = NewLoaded("a", "b");
        Assert.Empty(backend.PlayedIndices);

        audio.Update();                       // first Update starts playback
        Assert.Single(backend.PlayedIndices);

        audio.Update();                       // still playing -> no new track
        Assert.Single(backend.PlayedIndices);

        backend.IsPlaying = false;
        audio.Update();                       // current track ended -> next
        Assert.Equal(2, backend.PlayedIndices.Count);
    }

    [Fact]
    public void PlayFailureDisablesFurtherPlayback()
    {
        var (audio, backend) = NewLoaded("a", "b");
        backend.PlaySucceeds = false;

        audio.PlayRandomTrack();              // fails -> _available = false
        backend.PlaySucceeds = true;
        backend.PlayedIndices.Clear();

        audio.PlayRandomTrack();              // _available false -> no-op
        Assert.Empty(backend.PlayedIndices);
    }

    [Fact]
    public void SetRngMakesRotationDeterministic()
    {
        var (a1, b1) = NewLoaded("a", "b", "c");
        a1.SetRng(new Random(7));
        var (a2, b2) = NewLoaded("a", "b", "c");
        a2.SetRng(new Random(7));

        for (int i = 0; i < 20; i++)
        {
            b1.IsPlaying = false; a1.PlayRandomTrack();
            b2.IsPlaying = false; a2.PlayRandomTrack();
        }

        Assert.Equal(b1.PlayedIndices, b2.PlayedIndices);
    }

    [Fact]
    public void DisposeDisposesBackend()
    {
        var (audio, backend) = NewLoaded("a");

        audio.Dispose();

        Assert.True(backend.Disposed);
    }
}
```

- [ ] **Step 2: Run the new tests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --nologo --filter "FullyQualifiedName~AudioSystemTests"`
Expected: PASS — 14 tests, 0 failed.

If any fail, debug with `superpowers:systematic-debugging` before proceeding (a likely
first-run snag is the headless `ContentManager` constructor — if `new ContentManager(new
StubServiceProvider())` throws, that is the only place to investigate; the fake backend
ignores its argument so no content is ever loaded).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/Audio/AudioSystemTests.cs
git commit -m "Add headless AudioSystem behaviour tests"
```

---

### Task 8: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build KhaozEngine.slnx --nologo`
Expected: build succeeds, `KhaozEngine.Audio` included.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --nologo`
Expected: PASS — `268 + 14 = 282` total, 0 failed. (Baseline was 268.)

- [ ] **Step 3: Confirm no release-discipline violations**

Run: `git diff --stat origin/main -- Directory.Build.props CHANGELOG.md docs/CONSUMERS.md`
Expected: empty output (no version bump, no changelog, no consumers edit — coordinator owns 3.3.0).

- [ ] **Step 4: Final status for the coordinator**

Run: `git log --oneline origin/main..HEAD` and `git status -sb`
Expected: the task commits listed, working tree clean. Report branch, worktree path,
package added, files added, and the test-count delta (+14) back to the coordinating chat.

---

## Notes for the executor

- Do NOT bump `<Version>`, edit `CHANGELOG.md`, touch `docs/CONSUMERS.md`, or `dotnet pack`
  into the shared `local-feed`. The coordinating chat owns the batched 3.3.0 release.
- Stay in this worktree; do not merge to `main` or tag.
- The only cross-package wiring permitted is the two one-line additions (`KhaozEngine.slnx`,
  `KhaozEngine.Tests.csproj`). Anything beyond that is a coordinator decision.
