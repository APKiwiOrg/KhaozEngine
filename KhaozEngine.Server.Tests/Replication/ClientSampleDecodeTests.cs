using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

// #1886: buffered reads must preserve the legacy sample boundaries and callback isolation.
public sealed class ClientSampleDecodeTests
{
    [Theory]
    [InlineData(1, false, 0.25f, -0.5f)]
    [InlineData(16, false, 0.75f, 5.5f)]
    [InlineData(1, true, 0.5f, -3.5f)]
    [InlineData(16, true, 0.75f, 8.5f)]
    public void FixedDelayWritesTheLegacyValueBitForBit(ushort typeId, bool discrete, float factor, float expected)
    {
        bool readingHistory = false;
        var registry = ClientSampleFixture.Registry(typeId, discrete, reader =>
        {
            float value = reader.ReadSingle();
            if (value < 0 && !readingHistory) reader.ReadInt32();
            return new ClientSampleFixture.Sample(value);
        });
        var fixture = new ClientSampleFixture(registry, -3.5f, 8.5f);
        readingHistory = true; // The older sample has a trailer that this read leaves unread.

        fixture.Interpolate(factor);

        Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(fixture.Value));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(16, false)]
    [InlineData(1, true)]
    [InlineData(16, true)]
    public void ShorterCurrentSampleCannotReadTheOlderSamplesTrailer(ushort typeId, bool discrete)
    {
        bool overread = false;
        var registry = ClientSampleFixture.Registry(typeId, discrete, reader =>
        {
            float value = reader.ReadSingle();
            if (value < 0 || overread) reader.ReadInt32();
            return new ClientSampleFixture.Sample(value);
        });
        var fixture = new ClientSampleFixture(registry, -3.5f, 8.5f);
        fixture.Interpolate(0.25f); // Establish a passing control and read the older sample's trailer.
        overread = true;

        Assert.Throws<EndOfStreamException>(() => fixture.Interpolate(0.75f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyReaderCanDecodeAnotherViewBeforeResuming(bool discrete)
    {
        ClientSampleFixture? nested = null;
        var registry = ClientSampleFixture.Registry(1, discrete, reader =>
        {
            float value = reader.ReadSingle();
            ClientSampleFixture? target = nested;
            if (target is not null)
            {
                nested = null;
                target.Interpolate(0.25f);
            }
            if (value < 0) value += reader.ReadInt32();
            return new ClientSampleFixture.Sample(value);
        });
        var outer = new ClientSampleFixture(registry, -3.5f, 8.5f);
        var inner = new ClientSampleFixture(registry, 21f, 29f);
        nested = inner;

        outer.Interpolate(0.25f);

        Assert.Equal(discrete ? 12.5f : 11.5f, outer.Value);
        Assert.Equal(discrete ? 21f : 23f, inner.Value);
    }

    [Fact]
    public async Task ViewsSharingARegistryReadIndependentSamplesAcrossThreads()
    {
        var firstReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var secondReadFinished = new ManualResetEventSlim();
        bool readingHistory = false;
        var registry = ClientSampleFixture.Registry(1, false, reader =>
        {
            float value = reader.ReadSingle();
            if (readingHistory && value == -3.5f)
            {
                firstReadEntered.SetResult();
                secondReadFinished.Wait();
            }
            if (value < 0) value += reader.ReadInt32();
            return new ClientSampleFixture.Sample(value);
        });
        var first = new ClientSampleFixture(registry, -3.5f, 8.5f);
        var second = new ClientSampleFixture(registry, 21f, 29f);
        readingHistory = true;

        Task rendering = Task.Run(() =>
        {
            try { first.Interpolate(0.25f); }
            finally { firstReadEntered.TrySetResult(); }
        });
        await firstReadEntered.Task;
        try
        {
            second.Interpolate(0.75f);
        }
        finally
        {
            secondReadFinished.Set();
            await rendering;
        }

        Assert.Equal(11.5f, first.Value);
        Assert.Equal(27f, second.Value);
    }

    [Fact]
    public void EscapedReaderCannotSeeALaterSampleDuringItsCallback()
    {
        BinaryReader? earlier = null;
        bool readingHistory = false;
        float? escapedValue = null;
        var registry = ClientSampleFixture.Registry(16, true, reader =>
        {
            float value = reader.ReadSingle();
            if (readingHistory)
            {
                if (earlier is null)
                {
                    earlier = reader;
                }
                else
                {
                    try
                    {
                        earlier.BaseStream.Position = 0;
                        escapedValue = earlier.ReadSingle();
                    }
                    catch (ObjectDisposedException)
                    {
                        // A callback-scoped borrow may refuse access to the expired reader.
                    }
                }
            }
            return new ClientSampleFixture.Sample(value);
        });
        var fixture = new ClientSampleFixture(registry, 3.5f, 8.5f);
        readingHistory = true;

        fixture.Interpolate(0.25f);
        fixture.Interpolate(0.75f);

        Assert.Equal(8.5f, fixture.Value);
        if (escapedValue.HasValue)
            Assert.Equal(3.5f, escapedValue.Value);
    }
}
