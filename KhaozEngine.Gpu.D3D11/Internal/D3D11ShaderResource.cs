using System;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Joins CPU bytecode work before realizing a native shader on its consuming caller.</summary>
internal sealed class D3D11ShaderResource<TCompiled, TNative>(DeviceLiveness liveness,
    D3D11ShaderCompilation<TCompiled> compilation, Func<TCompiled, TNative> create) : IDisposable
    where TNative : class, IDisposable
{
    readonly object gate = new();
    Func<TCompiled, TNative>? createNative = create;
    TNative? native;
    volatile bool disposed;

    internal bool IsDisposed => disposed;

    internal TCompiled Compiled
    {
        get
        {
            RequireLive();
            var compiled = compilation.Join();
            RequireLive();
            return compiled;
        }
    }

    internal TNative Value
    {
        get
        {
            RequireLive();
            // Pipelines resolve the shader before taking the native creation gate. Their getters then use
            // this lock-free path, preserving that gate's leaf-lock rule.
            var ready = Volatile.Read(ref native);
            if (ready is not null) return ready;
            var compiled = Compiled;
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
            // A pending compile owns only managed sources/cache data. Its captured failure is data too, so
            // abandoning it neither waits under teardown locks nor creates an unobserved task exception.
        }
    }
}
