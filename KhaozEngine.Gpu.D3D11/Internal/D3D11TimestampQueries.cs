using System;
using System.Runtime.Versioning;
using Vortice.Direct3D11;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Windows-only COM calls for the query policy. No interop value types occur in fields.</summary>
    [SupportedOSPlatform("windows")]
    internal sealed class D3D11TimestampQueries(ID3D11Device device, ID3D11DeviceContext context) : ID3D11TimestampQueries
    {
        public object CreateQuery(int type) => device.CreateQuery(new QueryDescription((QueryType)type, QueryFlags.None));
        public void Begin(object query) => context.Begin((ID3D11Query)query);
        public void End(object query) => context.End((ID3D11Query)query);
        public unsafe int GetData(object query, Span<byte> data, uint flags)
        {
            fixed (byte* pointer = data)
                return context.GetData((ID3D11Query)query, (IntPtr)pointer, data.Length, (AsyncGetDataFlags)flags).Code;
        }
        public void Destroy(object query) => ((ID3D11Query)query).Dispose();
    }
}
