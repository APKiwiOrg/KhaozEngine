using System;
using System.Buffers;
using System.IO;
using System.Threading;
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
            Assert.Equal(oracle.Position, stream.Position);
            Assert.Equal(oracle.ReadByte(), stream.ReadByte());
        });
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
    public void AsyncReadsCompleteInlineAndHonorBoundsAndCancellation()
    {
        byte[] payload = [37, 53];
        byte[] destination = [0, 0, 89];
        Decode(payload, reader =>
        {
            Stream stream = reader.BaseStream;
            var canceled = new CancellationToken(canceled: true);
            Assert.ThrowsAny<OperationCanceledException>(() =>
                stream.ReadAsync(destination.AsMemory(), canceled).GetAwaiter().GetResult());
            Assert.ThrowsAny<OperationCanceledException>(() =>
                stream.ReadAsync(destination, 0, destination.Length, canceled).GetAwaiter().GetResult());

            var memoryRead = stream.ReadAsync(destination.AsMemory());
            bool memoryCompletedInline = memoryRead.IsCompletedSuccessfully;
            Assert.Equal(2, memoryRead.GetAwaiter().GetResult());
            Assert.True(memoryCompletedInline);
            Assert.Equal(new byte[] { 37, 53, 89 }, destination);

            stream.Position = 0;
            var arrayRead = stream.ReadAsync(destination, 1, 2, CancellationToken.None);
            bool arrayCompletedInline = arrayRead.IsCompletedSuccessfully;
            Assert.Equal(2, arrayRead.GetAwaiter().GetResult());
            Assert.True(arrayCompletedInline);
            Assert.Equal(new byte[] { 37, 37, 53 }, destination);
            Assert.Equal(0, stream.ReadAsync(destination.AsMemory()).GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void FailedMemoryDestinationDoesNotConsumeSample()
    {
        byte[] payload = [37];
        var destination = new DisposableMemory();
        Memory<byte> memory = destination.Memory;
        ((IDisposable)destination).Dispose();
        using var oracle = new MemoryStream(payload, writable: false);
        Assert.Throws<ObjectDisposedException>(() => oracle.ReadAsync(memory).GetAwaiter().GetResult());

        Decode(payload, reader =>
        {
            Assert.Throws<ObjectDisposedException>(() => reader.BaseStream.ReadAsync(memory).GetAwaiter().GetResult());
            Assert.Equal(oracle.ReadByte(), reader.BaseStream.ReadByte());
        });
    }

    [Fact]
    public void SampleStreamIsReadOnly()
    {
        Decode([37], reader =>
        {
            Stream stream = reader.BaseStream;
            Assert.False(stream.CanWrite);
            Assert.Throws<NotSupportedException>(() => stream.WriteByte(89));
            Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 89 }, 0, 1));
            Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 89 }.AsSpan()));
            Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
            Assert.Equal((byte)37, reader.ReadByte());
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

    private sealed class DisposableMemory : MemoryManager<byte>
    {
        private byte[]? buffer = new byte[1];

        public override Span<byte> GetSpan() => buffer ?? throw new ObjectDisposedException(nameof(DisposableMemory));
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => buffer = null;
    }

    private readonly record struct Sample(byte Value) : IComponent;
}
