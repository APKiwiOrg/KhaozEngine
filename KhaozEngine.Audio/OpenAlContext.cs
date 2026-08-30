using System;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;

namespace KhaozEngine.Audio;

/// <summary>
/// Owns the single per-process OpenAL device + context plus the shared <see cref="AL"/> / <see cref="ALContext"/>
/// API handles. OpenAL has exactly one current context per process, so the music backend and the SFX backend
/// must share it. <see cref="AudioSystem"/> creates one of these and hands it to both backends; the backends
/// borrow the context (they do not dispose it). Throws if no audio device is available so the caller can fall
/// back to a silent backend.
/// </summary>
internal sealed unsafe class OpenAlContext : IDisposable
{
    readonly ALContext _alc;
    readonly AL _al;
    readonly Device* _device;
    readonly Context* _context;
    readonly AlErrorLog _errors;

    /// <summary>Shared AL (sources / buffers / listener) API. Borrowed by the backends; do not dispose.</summary>
    public AL Al => _al;

    /// <summary>Shared ALContext (device / context) API. Borrowed by the backends; do not dispose.</summary>
    public ALContext Alc => _alc;

    public OpenAlContext(ILogger? logger = null)
    {
        // soft: true targets the bundled openal-soft (Silk.NET.OpenAL.Soft.Native) rather than the platform's
        // default OpenAL, so macOS uses the shipped lib instead of its deprecated system OpenAL.framework.
        _alc = ALContext.GetApi(true);
        _al = AL.GetApi(true);
        _device = _alc.OpenDevice("");
        if (_device == null) throw new InvalidOperationException("OpenAL: could not open an audio device");
        _context = _alc.CreateContext(_device, null);
        _alc.MakeContextCurrent(_context);
        // A null device is the only construction failure that throws. Everything past it can fail quietly, and
        // a context that never became current makes every later AL call a silent no-op.
        _errors = new AlErrorLog(logger ?? Log.For<OpenAlContext>());
        _errors.Check("context setup", _alc.GetError(_device));
    }

    public void Dispose()
    {
        _alc.MakeContextCurrent(null);
        if (_context != null) _alc.DestroyContext(_context);
        if (_device != null) _alc.CloseDevice(_device);
        _al.Dispose();
        _alc.Dispose();
    }
}
