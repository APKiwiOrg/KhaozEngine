using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A <see cref="FactAttribute"/> that is SKIPPED unless the environment variable <c>KE_GPU_TESTS=1</c> is
    /// set. GPU golden tests need a Metal device, so default <c>dotnet test</c> (and CI) skip them; the dev Mac
    /// runs them with <c>KE_GPU_TESTS=1 dotnet test</c>.
    /// </summary>
    public sealed class GpuFactAttribute : FactAttribute
    {
        public GpuFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("KE_GPU_TESTS") != "1")
                Skip = "set KE_GPU_TESTS=1 to run GPU golden tests";
        }
    }
}
