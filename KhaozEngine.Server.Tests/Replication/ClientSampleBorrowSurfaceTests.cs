using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

// #1886: a synchronous sample borrow cannot start work that survives the callback.
public sealed class ClientSampleBorrowSurfaceTests
{
    public enum RefusedMember
    {
        ReadAsyncArray,
        ReadAsyncArrayCanceled,
        ReadAsyncMemory,
        ReadAsyncMemoryCanceled,
        BeginRead,
        EndRead,
        CopyTo,
        CopyToSized,
        CopyToAsync,
        CopyToAsyncSized,
        CopyToAsyncCanceled,
        FlushAsync,
        FlushAsyncCanceled,
        WriteArray,
        WriteSpan,
        WriteByte,
        WriteAsyncArray,
        WriteAsyncMemory,
        BeginWrite,
        EndWrite,
        SetLength,
        GetReadTimeout,
        SetReadTimeout,
        GetWriteTimeout,
        SetWriteTimeout,
    }

    public enum CloseMember
    {
        ReaderClose,
        ReaderDispose,
        StreamClose,
        StreamDispose,
        StreamDisposeAsync,
    }

    [Theory]
    [InlineData(RefusedMember.ReadAsyncArray)]
    [InlineData(RefusedMember.ReadAsyncArrayCanceled)]
    [InlineData(RefusedMember.ReadAsyncMemory)]
    [InlineData(RefusedMember.ReadAsyncMemoryCanceled)]
    [InlineData(RefusedMember.BeginRead)]
    [InlineData(RefusedMember.EndRead)]
    [InlineData(RefusedMember.CopyTo)]
    [InlineData(RefusedMember.CopyToSized)]
    [InlineData(RefusedMember.CopyToAsync)]
    [InlineData(RefusedMember.CopyToAsyncSized)]
    [InlineData(RefusedMember.CopyToAsyncCanceled)]
    [InlineData(RefusedMember.FlushAsync)]
    [InlineData(RefusedMember.FlushAsyncCanceled)]
    [InlineData(RefusedMember.WriteArray)]
    [InlineData(RefusedMember.WriteSpan)]
    [InlineData(RefusedMember.WriteByte)]
    [InlineData(RefusedMember.WriteAsyncArray)]
    [InlineData(RefusedMember.WriteAsyncMemory)]
    [InlineData(RefusedMember.BeginWrite)]
    [InlineData(RefusedMember.EndWrite)]
    [InlineData(RefusedMember.SetLength)]
    [InlineData(RefusedMember.GetReadTimeout)]
    [InlineData(RefusedMember.SetReadTimeout)]
    [InlineData(RefusedMember.GetWriteTimeout)]
    [InlineData(RefusedMember.SetWriteTimeout)]
    public void UnsupportedMemberRefusesSynchronouslyWithoutConsumingSample(RefusedMember member)
    {
        using var destination = new MemoryStream();
        byte[] buffer = [101, 113];

        void Refuses(BinaryReader reader)
        {
            Assert.Throws<NotSupportedException>(() => Invoke(member, reader.BaseStream, buffer, destination));
            Assert.Equal(new byte[] { 101, 113 }, buffer);
            Assert.Equal(0L, destination.Length);
        }

        CheckBorrowAndReuse(reader =>
        {
            Refuses(reader);
            Assert.Equal((byte)37, reader.ReadByte());
        }, Refuses);
    }

    [Theory]
    [InlineData(CloseMember.ReaderClose)]
    [InlineData(CloseMember.ReaderDispose)]
    [InlineData(CloseMember.StreamClose)]
    [InlineData(CloseMember.StreamDispose)]
    [InlineData(CloseMember.StreamDisposeAsync)]
    public Task ClosingOneBorrowLeavesTheNextCodecUsable(CloseMember member) =>
        // A broken borrow can poison a thread-local reader. Keep that failure isolated to this test.
        Task.Factory.StartNew(() => CheckBorrowAndReuse(reader =>
        {
            Assert.Equal((byte)37, reader.ReadByte());
            Close(member, reader);
            Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
            Assert.Throws<ObjectDisposedException>(() => reader.PeekChar());
            Assert.Throws<ObjectDisposedException>(() => reader.BaseStream.Length);
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void CheckBorrowAndReuse(Action<BinaryReader> withinBorrow, Action<BinaryReader>? atIdle = null)
    {
        const ushort firstId = ReplicationRegistry.FirstExtensionTypeId;
        const ushort nextId = firstId + 1;
        BinaryReader? retained = null;
        var registry = new ReplicationRegistry();
        registry.Register<Sample>(firstId, static (value, writer) => writer.Write(value.Value), reader =>
        {
            retained = reader;
            withinBorrow(reader);
            return new Sample(37);
        }, discreteSample: true);
        registry.Register<Sample>(nextId, static (value, writer) => writer.Write(value.Value),
            static reader => new Sample(reader.ReadByte()), discreteSample: true);
        Assert.True(registry.TryGet(firstId, out ComponentCodec first));
        Assert.True(registry.TryGet(nextId, out ComponentCodec next));
        var world = new World();
        Entity entity = world.Spawn();

        first.SetFromBytes!(world, entity, [37, 53]);
        Assert.Equal(new Sample(37), world.Get<Sample>(entity));
        Assert.NotNull(retained);
        atIdle?.Invoke(retained);

        next.SetFromBytes!(world, entity, [89]);

        Assert.Equal(new Sample(89), world.Get<Sample>(entity));
    }

    private static void Invoke(RefusedMember member, Stream stream, byte[] buffer, Stream destination)
    {
        var canceled = new CancellationToken(canceled: true);
        switch (member)
        {
            case RefusedMember.ReadAsyncArray:
                DrainReturnedTask(stream.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None));
                break;
            case RefusedMember.ReadAsyncArrayCanceled:
                DrainReturnedTask(stream.ReadAsync(buffer, 0, buffer.Length, canceled));
                break;
            case RefusedMember.ReadAsyncMemory:
                DrainReturnedTask(stream.ReadAsync(buffer.AsMemory()).AsTask());
                break;
            case RefusedMember.ReadAsyncMemoryCanceled:
                DrainReturnedTask(stream.ReadAsync(buffer.AsMemory(), canceled).AsTask());
                break;
            case RefusedMember.BeginRead:
                CompleteReturnedRead(stream, stream.BeginRead(buffer, 0, buffer.Length, null, null));
                break;
            case RefusedMember.EndRead:
                _ = stream.EndRead(Task.CompletedTask);
                break;
            case RefusedMember.CopyTo:
                stream.CopyTo(destination);
                break;
            case RefusedMember.CopyToSized:
                stream.CopyTo(destination, 1);
                break;
            case RefusedMember.CopyToAsync:
                DrainReturnedTask(stream.CopyToAsync(destination));
                break;
            case RefusedMember.CopyToAsyncSized:
                DrainReturnedTask(stream.CopyToAsync(destination, 1, CancellationToken.None));
                break;
            case RefusedMember.CopyToAsyncCanceled:
                DrainReturnedTask(stream.CopyToAsync(destination, canceled));
                break;
            case RefusedMember.FlushAsync:
                DrainReturnedTask(stream.FlushAsync());
                break;
            case RefusedMember.FlushAsyncCanceled:
                DrainReturnedTask(stream.FlushAsync(canceled));
                break;
            case RefusedMember.WriteArray:
                stream.Write(buffer, 0, buffer.Length);
                break;
            case RefusedMember.WriteSpan:
                stream.Write(buffer.AsSpan());
                break;
            case RefusedMember.WriteByte:
                stream.WriteByte(101);
                break;
            case RefusedMember.WriteAsyncArray:
                DrainReturnedTask(stream.WriteAsync(buffer, 0, buffer.Length, CancellationToken.None));
                break;
            case RefusedMember.WriteAsyncMemory:
                DrainReturnedTask(stream.WriteAsync(buffer.AsMemory()).AsTask());
                break;
            case RefusedMember.BeginWrite:
                CompleteReturnedWrite(stream, stream.BeginWrite(buffer, 0, buffer.Length, null, null));
                break;
            case RefusedMember.EndWrite:
                stream.EndWrite(Task.CompletedTask);
                break;
            case RefusedMember.SetLength:
                stream.SetLength(0);
                break;
            case RefusedMember.GetReadTimeout:
                _ = stream.ReadTimeout;
                break;
            case RefusedMember.SetReadTimeout:
                stream.ReadTimeout = 5;
                break;
            case RefusedMember.GetWriteTimeout:
                _ = stream.WriteTimeout;
                break;
            case RefusedMember.SetWriteTimeout:
                stream.WriteTimeout = 5;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member));
        }
    }

    // A regression can return an outstanding operation. Join it before returning the borrow and
    // distinguish a faulted result from an exception thrown by the call itself.
    private static void DrainReturnedTask(Task task)
    {
        try { task.GetAwaiter().GetResult(); }
        catch (Exception exception) { throw new InvalidOperationException("The operation returned a task.", exception); }
    }

    private static void CompleteReturnedRead(Stream stream, IAsyncResult result)
    {
        WaitForReturnedOperation(result);
        try { _ = stream.EndRead(result); }
        catch (Exception exception) { throw new InvalidOperationException("The operation returned an APM result.", exception); }
    }

    private static void CompleteReturnedWrite(Stream stream, IAsyncResult result)
    {
        WaitForReturnedOperation(result);
        try { stream.EndWrite(result); }
        catch (Exception exception) { throw new InvalidOperationException("The operation returned an APM result.", exception); }
    }

    private static void WaitForReturnedOperation(IAsyncResult result)
    {
        if (result is Task task) DrainReturnedTask(task);
        else result.AsyncWaitHandle.WaitOne();
    }

    private static void Close(CloseMember member, BinaryReader reader)
    {
        switch (member)
        {
            case CloseMember.ReaderClose:
                reader.Close();
                break;
            case CloseMember.ReaderDispose:
                reader.Dispose();
                break;
            case CloseMember.StreamClose:
                reader.BaseStream.Close();
                break;
            case CloseMember.StreamDispose:
                reader.BaseStream.Dispose();
                break;
            case CloseMember.StreamDisposeAsync:
                reader.BaseStream.DisposeAsync().GetAwaiter().GetResult();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(member));
        }
    }

    private readonly record struct Sample(byte Value) : IComponent;
}
