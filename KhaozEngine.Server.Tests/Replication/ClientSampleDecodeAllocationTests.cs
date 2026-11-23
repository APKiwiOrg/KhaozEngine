using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Replication;

// #1886: a warmed client update must not allocate a reader and stream per sample.
[Collection("AllocSensitive")]
public sealed class ClientSampleDecodeAllocationTests(ITestOutputHelper output)
{
    private const int EntityCount = 32;
    private const int WarmupUpdates = 64;
    private const int Updates = 128;
    private const int UpdatesPerBracket = 8;

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
        var samples = new float[(WarmupUpdates + Updates) / UpdatesPerBracket + 1];
        for (int i = 0; i < samples.Length; i++) samples[i] = 2 + 8 * i;
        var fixture = new ClientSampleFixture(registry, samples, EntityCount);

        bool matches = true;
        for (int i = 0; i < WarmupUpdates; i++) Update(i);
        long allocated = AllocationMeasurement.Measure(
            $"Fixed-delay {(discrete ? "discrete" : "lerp")} sample decode",
            () =>
            {
                for (int i = WarmupUpdates; i < WarmupUpdates + Updates; i++) Update(i);
            });
        Assert.True(matches, "The advancing updates must publish the sample selected at every measured render time.");
        return allocated;

        void Update(int index)
        {
            int bracket = index / UpdatesPerBracket;
            float fraction = (index % UpdatesPerBracket + 0.5f) / UpdatesPerBracket;
            fixture.Interpolate(bracket + fraction);
            float lower = samples[bracket];
            float upper = samples[bracket + 1];
            float expected = discrete ? samples[fraction <= 0.5f ? bracket : bracket + 1] : lower + (upper - lower) * fraction;
            matches &= fixture.Value == expected;
        }
    }
}
