using System;

namespace KhaozEngine.Tools.Coverage;

/// <summary>One decoded pose handed out by a campaign. The holder owns it until <see cref="Dispose"/>, which returns
/// its bytes to the session's live-pose account. A resident campaign lends its snapshot and releases nothing.
/// Using the value after disposal is a contract violation, so the lease refuses to hand it out again.</summary>
internal sealed class CoveragePoseLease<T> : IDisposable where T : class
{
    T? value;
    Action? release;

    internal CoveragePoseLease(T value, Action? release = null)
    {
        this.value = value;
        this.release = release;
    }

    internal T Value => value ?? throw new ObjectDisposedException(nameof(CoveragePoseLease<T>));

    /// <summary>The value a caller may keep after this lease is disposed: a copy of session storage, which the release
    /// overwrites, or the resident value itself, which the caller already owns.</summary>
    internal T Keep(Func<T, T> copy) => release == null ? Value : copy(Value);

    public void Dispose()
    {
        if (value == null) return;
        value = null;
        var pending = release;
        release = null;
        pending?.Invoke();
    }
}
