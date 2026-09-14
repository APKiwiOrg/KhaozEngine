using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

/// <summary>
/// Owns the single per-process OpenAL device + context plus the shared <see cref="AL"/> / <see cref="ALContext"/>
/// API handles. OpenAL has exactly one current context per process, so the music backend and the SFX backend
/// must share it. <see cref="AudioSystem"/> creates one of these and hands it to both backends; the backends
/// borrow the context (they do not dispose it). Throws if no audio device is available so the caller can fall
/// back to a silent backend.
/// <para>The device is opened as the default output and follows it: <see cref="PollOutputDevice"/> reopens it in
/// place when the default changes or the device is lost (<see cref="AudioDeviceFollower"/>). A reopen keeps the
/// context, every source and every buffer, so neither backend reloads anything.</para>
/// </summary>
internal sealed unsafe class OpenAlContext : IDisposable, IAlcOutputDevice
{
    // ALC values Silk.NET's enums leave out.
    const int AlcDefaultAllDevicesSpecifier = 0x1012;
    const int AlcAllDevicesSpecifier = 0x1013;
    const int AlcConnected = 0x313;

    readonly ALContext _alc;
    readonly AL _al;
    readonly Device* _device;
    readonly Context* _context;
    readonly AlErrorLog _errors;
    readonly AudioDeviceFollower _follower;
    readonly long _openedAt;
    readonly bool _canEnumerate;
    readonly bool _reportsDisconnect;
    // Raw entry points. alcGetString is called directly so the returned pointer is read, never freed, and
    // alcReopenDeviceSOFT has no exported symbol in the bundled 1.23.1 natives, only an entry in ALC's proc table.
    readonly delegate* unmanaged[Cdecl]<Device*, int, byte*> _getString;
    readonly delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte> _reopen;
    bool _disposed;

    /// <summary>Shared AL (sources / buffers / listener) API. Borrowed by the backends; do not dispose.</summary>
    public AL Al => _al;

    /// <summary>Shared ALContext (device / context) API. Borrowed by the backends; do not dispose.</summary>
    public ALContext Alc => _alc;

    public OpenAlContext(ILogger? logger = null)
    {
        ILogger log = logger ?? Log.For<OpenAlContext>();
        // soft: true targets the bundled openal-soft (Silk.NET.OpenAL.Soft.Native) rather than the platform's
        // default OpenAL, so macOS uses the shipped lib instead of its deprecated system OpenAL.framework.
        _alc = ALContext.GetApi(true);
        _al = AL.GetApi(true);
        _errors = new AlErrorLog(log);
        _getString = (delegate* unmanaged[Cdecl]<Device*, int, byte*>)_alc.Context.GetProcAddress("alcGetString");
        _canEnumerate = _alc.IsExtensionPresent(null, "ALC_ENUMERATE_ALL_EXT");
        string? defaultAtOpen = ProbeDefaultDeviceName();

        _device = _alc.OpenDevice("");
        if (_device == null) throw new InvalidOperationException("OpenAL: could not open an audio device");
        _context = _alc.CreateContext(_device, null);
        _alc.MakeContextCurrent(_context);
        // A null device is the only construction failure that throws. Everything past it can fail quietly, and
        // a context that never became current makes every later AL call a silent no-op.
        _errors.Check("context setup", _alc.GetError(_device));
        HoldSourcesOnDisconnect();

        _reportsDisconnect = _alc.IsExtensionPresent(_device, "ALC_EXT_disconnect");
        if (_alc.IsExtensionPresent(_device, "ALC_SOFT_reopen_device"))
            _reopen = (delegate* unmanaged[Cdecl]<Device*, byte*, int*, byte>)_alc.GetProcAddress(_device, "alcReopenDeviceSOFT");
        _openedAt = Stopwatch.GetTimestamp();
        // Opened with an empty name, which is the default output, so the device follows the default.
        _follower = new AudioDeviceFollower(this, followsDefault: true, defaultAtOpen, _errors, log, TimeSpan.Zero);
    }

    /// <summary>
    /// Reopens the device when the default output changed or the device was lost. Cheap between checks, which run
    /// once a second. Call from the thread that drives audio, every frame.
    /// </summary>
    public void PollOutputDevice()
    {
        // The raw entry points go stale once Dispose releases the native library, so a late Update must not reach them.
        if (_disposed) return;
        _follower.Poll(Stopwatch.GetElapsedTime(_openedAt));
    }

    bool IAlcOutputDevice.CanReopen => _reopen != null;

    bool IAlcOutputDevice.IsConnected
    {
        get
        {
            if (!_reportsDisconnect) return true;
            int connected = 1;
            _alc.GetContextProperty(_device, (GetContextInteger)AlcConnected, 1, &connected);
            return connected != 0;
        }
    }

    string? IAlcOutputDevice.CurrentDeviceName => ReadString(_device, AlcAllDevicesSpecifier);

    public string? ProbeDefaultDeviceName()
    {
        if (!_canEnumerate) return null;
        // ALC_DEFAULT_ALL_DEVICES_SPECIFIER only enumerates while OpenAL Soft's device list is still empty, and
        // otherwise answers from whatever list the last full enumeration left, so on its own it never sees a change.
        // Asking for the full list is what re-enumerates, and every backend lists the current default first.
        _ = _getString(null, AlcAllDevicesSpecifier);
        return ReadString(null, AlcDefaultAllDevicesSpecifier);
    }

    bool IAlcOutputDevice.TryReopen(out ContextError error)
    {
        // A null name reopens on the default output. Null attributes match how the context was created.
        bool reopened = _reopen(_device, null, null) != 0;
        error = _alc.GetError(_device);
        return reopened;
    }

    // A lost device stops every playing source by default, and a stopped streaming source reports all of its
    // buffers processed, so the music refill would race through the track while nothing can be heard. Holding
    // them instead lets a reopen resume playback where the device dropped it. The extension is still experimental
    // in 1.23.1 (AL_SOFTX_), so the enum is looked up by name rather than written down.
    void HoldSourcesOnDisconnect()
    {
        if (!_al.IsExtensionPresent("AL_SOFT_hold_on_disconnect") && !_al.IsExtensionPresent("AL_SOFTX_hold_on_disconnect"))
            return;
        int stopSourcesOnDisconnect = _al.GetEnumValue("AL_STOP_SOURCES_ON_DISCONNECT_SOFT");
        if (stopSourcesOnDisconnect == 0) return;
        _al.Disable((Capability)stopSourcesOnDisconnect);
        _errors.Check("hold sources on disconnect", _al.GetError());
    }

    string? ReadString(Device* device, int param)
    {
        byte* value = _getString(device, param);
        return value == null || *value == 0 ? null : Marshal.PtrToStringUTF8((nint)value);
    }

    public void Dispose()
    {
        _disposed = true;
        _alc.MakeContextCurrent(null);
        if (_context != null) _alc.DestroyContext(_context);
        if (_device != null) _alc.CloseDevice(_device);
        _al.Dispose();
        _alc.Dispose();
    }
}
