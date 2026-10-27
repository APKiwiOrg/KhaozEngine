using System;
using System.Runtime.ExceptionServices;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Audio;

public sealed partial class AudioSystem
{
    bool _disposed;

    /// <inheritdoc/>
    /// <remarks>
    /// Shutdown may run off the owning thread, provided it does not race a live update or play call.
    /// All borrowed resources are attempted before the shared context, and the first failure is rethrown.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loopMix.Clear();
        _endedLoops.Clear();
        AudioDisposal.Run(_sfxBackend.Dispose, _backend.Dispose, () => _context?.Dispose(), _logger);
    }
}

/// <summary>Ordered audio cleanup that preserves the first failure even when later diagnostics throw.</summary>
internal static class AudioDisposal
{
    internal static void Run(Action sfx, Action music, Action context, ILogger logger)
    {
        ExceptionDispatchInfo? first = null;
        Attempt(sfx);
        Attempt(music);
        Attempt(context);
        first?.Throw();

        void Attempt(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception exception)
            {
                if (first is null) first = ExceptionDispatchInfo.Capture(exception);
                else
                {
                    try { logger.Warn("secondary audio cleanup failed.", exception); }
                    catch { /* Diagnostics cannot prevent remaining resource cleanup. */ }
                }
            }
        }
    }
}
