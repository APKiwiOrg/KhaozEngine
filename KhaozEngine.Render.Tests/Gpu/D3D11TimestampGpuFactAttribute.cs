using KhaozEngine.Gpu.TestKit;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The ordinary native GPU gate, restricted to the running Direct3D 11 backend.</summary>
    public sealed class D3D11TimestampGpuFactAttribute : FactAttribute
    {
        public D3D11TimestampGpuFactAttribute()
        {
            Skip = GpuTestGate.SkipReason();
            if (Skip is not null) return;

            string backend = GpuTestGate.BackendName;
            if (backend != "direct3d11-native")
                Skip = $"The running backend is {backend}, so this Direct3D 11 timestamp guarantee does not apply.";
        }
    }
}
