using KhaozEngine.Gpu.TestKit;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The <see cref="TheoryAttribute"/> sibling of <see cref="GpuFactAttribute"/>: a data-driven GPU test gated on
    /// the same <c>KE_GPU_TESTS</c> environment variable through <see cref="GpuTestGate"/>, so the two attributes
    /// cannot drift apart on when a GPU test runs.
    /// </summary>
    public sealed class GpuTheoryAttribute : TheoryAttribute
    {
        public GpuTheoryAttribute()
        {
            string? reason = GpuTestGate.SkipReason();
            if (reason != null) Skip = reason;
        }
    }
}
