using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>The native query boundary. GetData preserves its HRESULT for the device-loss check.</summary>
    internal interface ID3D11TimestampQueries
    {
        object CreateQuery(int type);
        void Begin(object query);
        void End(object query);
        int GetData(object query, Span<byte> data, uint flags);
        void Destroy(object query);
    }
}
