using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The <see cref="TheoryAttribute"/> sibling of <see cref="GpuFactAttribute"/>: a data-driven GPU test gated on
    /// the same <c>KE_GPU_TESTS</c> environment variable, through the same
    /// <see cref="GpuFactAttribute.SkipReason(string?, System.Func{string?})"/> decision (strict / probe / skip), so
    /// the two attributes can never drift apart on when a GPU test runs.
    /// </summary>
    public sealed class GpuTheoryAttribute : TheoryAttribute
    {
        public GpuTheoryAttribute()
        {
            string? reason = GpuFactAttribute.SkipReason(
                System.Environment.GetEnvironmentVariable("KE_GPU_TESTS"),
                GpuFactAttribute.ProbeReasonValue);
            if (reason != null) Skip = reason;
        }
    }
}
