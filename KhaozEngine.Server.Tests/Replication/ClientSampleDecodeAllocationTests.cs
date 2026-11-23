using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Replication;

// #1886: a warmed client update must not allocate a reader and stream per sample.
[Collection("AllocSensitive")]
public sealed class ClientSampleDecodeAllocationTests(ITestOutputHelper output)
{
    private const int EntityCount = 32;
    private const int Updates = 128;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedDelaySampleDecodeAllocatesZeroBytes(bool discrete)
    {
        long allocated = Measure(discrete);
        Assert.Equal(0L, allocated);
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportFixedDelaySampleDecodeAllocation()
    {
        output.WriteLine($"{EntityCount} entities, one component per entity, {Updates} measured updates");
        output.WriteLine($"Lerp: {Measure(discrete: false) / (double)Updates:F2} bytes/update");
        output.WriteLine($"Discrete: {Measure(discrete: true) / (double)Updates:F2} bytes/update");
    }

    private static long Measure(bool discrete)
    {
        var registry = ClientSampleFixture.Registry(16, discrete,
            static reader => new ClientSampleFixture.Sample(reader.ReadSingle()));
        var fixture = new ClientSampleFixture(registry, 2f, 10f, EntityCount);
        for (int i = 0; i < 64; i++) fixture.Interpolate(0.75f);
        Assert.Equal(discrete ? 10f : 8f, fixture.Value);

        return AllocationMeasurement.Measure($"Fixed-delay {(discrete ? "discrete" : "lerp")} sample decode", () =>
        {
            for (int i = 0; i < Updates; i++) fixture.Interpolate(0.75f);
        });
    }
}
