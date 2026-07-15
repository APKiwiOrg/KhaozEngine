using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace KhaozEngine.App;

/// <summary>
/// The real <see cref="ISingleInstanceLock"/>: a named <see cref="Mutex"/> for ownership (claimed for the
/// life of the process, released on <see cref="Dispose"/> or process exit) plus a small marker file under
/// the OS temp directory as the foreground-request channel.
/// </summary>
/// <remarks>
/// The foreground channel is deliberately a file, not a named <see cref="EventWaitHandle"/> or
/// <see cref="System.Threading.Semaphore"/>: .NET's NAMED synchronization primitives are only fully
/// implemented for <see cref="Mutex"/> off Windows - a named <see cref="EventWaitHandle"/> or
/// <see cref="System.Threading.Semaphore"/> throws <see cref="PlatformNotSupportedException"/> on
/// macOS/Linux (confirmed against the actual runtime, not assumed from docs). The mutex is exactly what
/// this class needs for ownership (claim-and-hold), so it stays; the second, independent need - a losing
/// process telling the owner "come to the foreground" - is carried by a plain file touch/poll instead,
/// which works identically on every platform this engine targets.
/// </remarks>
public sealed class SystemSingleInstanceLock : ISingleInstanceLock
{
    /// <summary>How often <see cref="WaitForForegroundRequest"/> re-checks the marker file.</summary>
    const int PollIntervalMilliseconds = 200;

    Mutex? _mutex;
    string? _key;

    /// <inheritdoc/>
    public bool TryAcquire(string key, TimeSpan predecessorWait)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        string mutexName = "KhaozEngine.SingleInstance." + key;
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName, out _);
        }
        catch (UnauthorizedAccessException)
        {
            // A same-named mutex already exists but is owned by a different OS user/session: fail OPEN
            // (treat as no conflict) rather than block a legitimate separate session from ever starting.
            return true;
        }

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(predecessorWait);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed without releasing it: we still got it. This lock carries no
            // shared data (only ownership), so an abandoned handle is no different from a clean acquire.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        _key = key;

        // Clear any stale marker left by a request that arrived after the previous owner (if any) had
        // already stopped listening (e.g. it crashed, or exited between polls) - otherwise a leftover
        // request from a long-dead process would immediately fire on this fresh start.
        try { File.Delete(MarkerPath(key)); }
        catch { /* best-effort */ }

        return true;
    }

    /// <inheritdoc/>
    public void RequestForeground(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        try
        {
            string path = MarkerPath(key);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best-effort: nothing to do if the owning process cannot be signalled this way.
        }
    }

    /// <inheritdoc/>
    public bool WaitForForegroundRequest(TimeSpan timeout)
    {
        if (_key is null)
        {
            return false; // never acquired ownership: nothing to listen for.
        }

        string path = MarkerPath(_key);
        DateTime deadline = DateTime.UtcNow + (timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        while (true)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path); // consume it so it does not re-trigger on the next call.
                    return true;
                }
            }
            catch
            {
                // Transient IO (e.g. a delete race with a concurrent writer); keep polling.
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            Thread.Sleep(PollIntervalMilliseconds);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException)
            {
                // Already released, or this thread never owned it (e.g. Dispose called twice); harmless.
            }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    static string MarkerPath(string key)
    {
        string dir = Path.Combine(Path.GetTempPath(), "KhaozEngine.SingleInstance");
        return Path.Combine(dir, SanitizeFileName(key) + ".focus");
    }

    static string SanitizeFileName(string key)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            key = key.Replace(c, '_');
        }
        return key;
    }
}
