using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Keep each history mode's scene and cached measurements independent within a class fixture.</summary>
internal sealed class TemporalHistoryVariants<T> : IDisposable where T : class
{
    readonly T _owner;
    readonly TemporalHistoryDetail _mode;
    readonly Lazy<T> _other;
    bool _disposed;

    internal TemporalHistoryVariants(T owner, TemporalHistoryDetail mode, Func<TemporalHistoryDetail, TemporalHistoryVariants<T>, T> create)
    {
        _owner = owner;
        _mode = mode;
        _other = new Lazy<T>(() => create(mode == TemporalHistoryDetail.Display
            ? TemporalHistoryDetail.Fine : TemporalHistoryDetail.Display, this));
    }

    internal T For(TemporalHistoryDetail mode)
    {
        if (mode is not (TemporalHistoryDetail.Display or TemporalHistoryDetail.Fine))
            throw new ArgumentOutOfRangeException(nameof(mode));
        return mode == _mode ? _owner : _other.Value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_other.IsValueCreated && _other.Value is IDisposable disposable) disposable.Dispose();
    }
}
