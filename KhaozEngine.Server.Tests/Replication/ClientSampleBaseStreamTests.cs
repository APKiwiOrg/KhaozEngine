using System;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

// #1886: sample decoders can read and seek through the borrowed BaseStream.
public sealed class ClientSampleBaseStreamTests
{
    [Theory]
    [InlineData(SeekOrigin.Begin, -1L, 0)]
    [InlineData(SeekOrigin.Current, -2L, 1)]
    [InlineData(SeekOrigin.End, -4L, 0)]
    [InlineData(SeekOrigin.End, long.MaxValue, 0)]
    [InlineData(SeekOrigin.Current, int.MaxValue, 1)]
    [InlineData(SeekOrigin.Current, long.MinValue, 1)]
    [InlineData(SeekOrigin.Current, -1L, 1)]
    [InlineData(SeekOrigin.End, 2L, 0)]
    [InlineData((SeekOrigin)3, 0L, 1)]
    public void SeekMatchesReadOnlyMemoryStream(SeekOrigin origin, long offset, int start)
    {
        byte[] payload = [37, 53, 89];
        using var oracle = new MemoryStream(payload, writable: false);
        oracle.Position = start;
        Exception? expected = Record.Exception(() => { oracle.Seek(offset, origin); });

        Decode(payload, reader =>
        {
            Stream stream = reader.BaseStream;
            stream.Position = start;
            Exception? actual = Record.Exception(() => { stream.Seek(offset, origin); });
            Assert.Equal(expected?.GetType(), actual?.GetType());
            AssertSameArgumentFields(expected, actual);
            Assert.Equal(oracle.Position, stream.Position);
            Assert.Equal(oracle.ReadByte(), stream.ReadByte());
        });
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(2L)]
    [InlineData(5L)]
    public void PositionAssignmentMatchesReadOnlyMemoryStream(long position)
    {
        byte[] payload = [37, 53, 89];
        using var oracle = new MemoryStream(payload, writable: false);
        Exception? expected = Record.Exception(() => { oracle.Position = position; });

        Decode(payload, reader =>
        {
            Stream stream = reader.BaseStream;
            Exception? actual = Record.Exception(() => { stream.Position = position; });
            Assert.Equal(expected?.GetType(), actual?.GetType());
            AssertSameArgumentFields(expected, actual);
            Assert.Equal(oracle.Position, stream.Position);
            Assert.Equal(oracle.ReadByte(), stream.ReadByte());
        });
    }

    // A codec may filter a refusal by the structured fields of the argument exception, so those match too.
    private static void AssertSameArgumentFields(Exception? expected, Exception? actual)
    {
        Assert.Equal((expected as ArgumentException)?.ParamName, (actual as ArgumentException)?.ParamName);
        Assert.Equal((expected as ArgumentOutOfRangeException)?.ActualValue,
            (actual as ArgumentOutOfRangeException)?.ActualValue);
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 0, -1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    public void InvalidArrayReadMatchesReadOnlyMemoryStreamAndConsumesNothing(int size, int offset, int count)
    {
        byte[] payload = [37];
        byte[]? destination = size < 0 ? null : new byte[size];
        using var oracle = new MemoryStream(payload, writable: false);
        ArgumentException expected = Assert.IsAssignableFrom<ArgumentException>(
            Record.Exception(() => oracle.Read(destination!, offset, count)));

        Decode(payload, reader =>
        {
            Stream stream = reader.BaseStream;
            ArgumentException actual = Assert.IsAssignableFrom<ArgumentException>(
                Record.Exception(() => stream.Read(destination!, offset, count)));
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.ParamName, actual.ParamName);
            Assert.Equal(oracle.ReadByte(), stream.ReadByte());
        });
    }

    [Fact]
    public void ActiveEmptySampleHasNormalEofBehavior()
    {
        Decode([], reader =>
        {
            Assert.Equal(0L, reader.BaseStream.Length);
            Assert.Equal(0L, reader.BaseStream.Position);
            Assert.Equal(-1, reader.BaseStream.ReadByte());
            Assert.Equal(-1, reader.PeekChar());
            Assert.Equal(-1, reader.Read());
            Assert.Empty(reader.ReadBytes(1));
            Assert.Throws<EndOfStreamException>(() => reader.ReadByte());
        });
    }

    [Fact]
    public void SynchronousReadHelpersStayWithinTheSample()
    {
        Decode([37, 53, 89], reader =>
        {
            Stream stream = reader.BaseStream;
            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);
            Assert.False(stream.CanWrite);
            Assert.False(stream.CanTimeout);
            stream.Flush();

            Span<byte> first = stackalloc byte[2];
            stream.ReadExactly(first);
            Assert.Equal(new byte[] { 37, 53 }, first.ToArray());

            byte[] tail = new byte[2];
            Assert.Equal(1, stream.ReadAtLeast(tail, 2, throwOnEndOfStream: false));
            Assert.Equal((byte)89, tail[0]);
            Assert.Throws<EndOfStreamException>(() => stream.ReadExactly(tail.AsSpan()));
        });
    }

    private static void Decode(byte[] payload, Action<BinaryReader> check)
    {
        var registry = new ReplicationRegistry();
        registry.Register<Sample>(16, static (value, writer) => writer.Write(value.Value), reader =>
        {
            check(reader);
            return new Sample(0);
        }, discreteSample: true);
        Assert.True(registry.TryGet(16, out ComponentCodec codec));
        var world = new World();
        codec.SetFromBytes!(world, world.Spawn(), payload);
    }

    private readonly record struct Sample(byte Value) : IComponent;
}
