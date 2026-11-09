using System;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Prepares managed inputs before realizing native shader or pipeline state on its consuming caller.</summary>
internal sealed class D3D11DeferredResource<TPrepared, TNative>(DeviceLiveness liveness,
    Func<TPrepared> prepare, Func<TPrepared, TNative> create) : IDisposable
    where TNative : class, IDisposable
{
    readonly object gate = new();
    Func<TPrepared>? prepareInputs = prepare;
    Func<TPrepared, TNative>? createNative = create;
    TNative? native;
    volatile bool disposed;

    internal bool IsDisposed => disposed;

    internal TPrepared Prepared
    {
        get
        {
            RequireLive();
            var prepare = Volatile.Read(ref prepareInputs);
            ObjectDisposedException.ThrowIf(prepare is null, this);
            var compiled = prepare();
            RequireLive();
            return compiled;
        }
    }

    internal TNative Value
    {
        get
        {
            RequireLive();
            // Shader joins and realization finish before pipeline creation enters the native creation gate.
            var ready = Volatile.Read(ref native);
            if (ready is not null) return ready;
            var compiled = Prepared;
            lock (gate)
            {
                RequireLive();
                if (native is not null) return native;
                var created = createNative!(compiled);
                Volatile.Write(ref native, created);
                return created;
            }
        }
    }

    void RequireLive() => ObjectDisposedException.ThrowIf(disposed || liveness.IsDead, this);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (liveness.IsAlive) native?.Dispose();
            native = null;
            createNative = null;
            prepareInputs = null;
            // A pending compile owns only managed sources/cache data. Its captured failure is data too, so
            // abandoning it neither waits under teardown locks nor creates an unobserved task exception.
        }
    }
}
