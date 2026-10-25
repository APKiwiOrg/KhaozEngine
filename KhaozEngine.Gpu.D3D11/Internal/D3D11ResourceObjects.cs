using System;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Eager native resource and views. The same owner binds these handles and retires the payload lease.</summary>
internal sealed record D3D11ResourceObjects(object Resource, object? Srv = null, object? Uav = null,
    object? Rtv = null, object? Dsv = null, ID3D11RingMemory? RingMemory = null) : IDisposable
{
    public void Dispose()
    {
        (Srv as IDisposable)?.Dispose(); (Rtv as IDisposable)?.Dispose();
        (Dsv as IDisposable)?.Dispose(); (Uav as IDisposable)?.Dispose();
        ((IDisposable)Resource).Dispose();
    }
}
