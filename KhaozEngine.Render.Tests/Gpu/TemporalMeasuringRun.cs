using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Temporary. Fails a measuring run so every leg prints the figures.</summary>
    internal static class TemporalMeasuringRun
    {
        public static void Report() => Assert.Fail("measuring run, the figures are in the output");
    }
}
