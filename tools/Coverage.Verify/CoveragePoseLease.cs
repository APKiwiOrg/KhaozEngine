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

    public void Dispose()
    {
        if (value == null) return;
        value = null;
        var pending = release;
        release = null;
        pending?.Invoke();
    }
}
