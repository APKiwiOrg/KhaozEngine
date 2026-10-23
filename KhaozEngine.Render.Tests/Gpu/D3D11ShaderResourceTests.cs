using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11ShaderResourceTests
{
    [Fact]
    public void BytecodeJoin_DoesNotCreateNativeObjects_AndFirstNativeUseCreatesOnce()
    {
        byte[] bytes = [0x44, 0x58, 0x42, 0x43, 4];
        var compilation = D3D11ShaderCompilation<byte[]>.Start(() => bytes);
        int creates = 0;
        using var resource = new D3D11DeferredResource<byte[], Native>(new DeviceLiveness(), compilation.Join, compiled =>
        {
            creates++;
            return new Native(compiled);
        });

        Assert.Same(bytes, resource.Prepared);
        Assert.Equal(0, creates);
        var native = resource.Value;
        Assert.Same(bytes, native.Bytes);
        Assert.Same(native, resource.Value);
        Assert.Equal(1, creates);
        resource.Dispose();
        resource.Dispose();
        Assert.Equal(1, native.Disposals);
        Assert.Throws<ObjectDisposedException>(() => resource.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeWhilePending_DoesNotJoinOrCreateNativeObjects(bool fail)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var failure = new ShaderValidationException("pending compile failed");
        var compilation = D3D11ShaderCompilation<byte[]>.Start(() =>
        {
            entered.Set();
            release.Wait();
            if (fail) throw failure;
            return [7];
        });
        int creates = 0;
        var resource = new D3D11DeferredResource<byte[], Native>(new DeviceLiveness(), compilation.Join, compiled =>
        {
            creates++;
            return new Native(compiled);
        });
        try
        {
            entered.Wait();
            resource.Dispose();
            Assert.True(resource.IsDisposed);
            Assert.Equal(0, creates);
            Assert.Throws<ObjectDisposedException>(() => resource.Value);
        }
        finally { release.Set(); }

        if (fail) Assert.Same(failure, Assert.Throws<ShaderValidationException>(() => compilation.Join()));
        else Assert.Equal(new byte[] { 7 }, compilation.Join());
        Assert.Equal(0, creates);
    }

    [Fact]
    public void DeviceDeathWhilePending_RefusesNativeCreationAfterCompileCompletes()
    {
        using var release = new ManualResetEventSlim();
        var liveness = new DeviceLiveness();
        var compilation = D3D11ShaderCompilation<byte[]>.Start(() =>
        {
            release.Wait();
            return [2];
        });
        int creates = 0;
        using var resource = new D3D11DeferredResource<byte[], Native>(liveness, compilation.Join, compiled =>
        {
            creates++;
            return new Native(compiled);
        });
        liveness.MarkDead();
        release.Set();
        Assert.Equal(new byte[] { 2 }, compilation.Join());
        Assert.Throws<ObjectDisposedException>(() => resource.Value);
        Assert.Throws<ObjectDisposedException>(() => resource.Prepared);
        Assert.Equal(0, creates);
    }

    [Fact]
    public void DeviceDeathAfterNativeCreation_PreservesTheExistingNoReleaseContract()
    {
        var liveness = new DeviceLiveness();
        var compilation = D3D11ShaderCompilation<byte[]>.Start(() => [3]);
        using var resource = new D3D11DeferredResource<byte[], Native>(liveness, compilation.Join, bytes => new Native(bytes));
        var native = resource.Value;
        liveness.MarkDead();
        resource.Dispose();
        Assert.Equal(0, native.Disposals);
    }

    [Fact]
    public void CompileFailure_IsThrownAtTheConsumerAndNeverCreatesNativeObjects()
    {
        var failure = new ShaderValidationException("program: original FXC diagnostic");
        var compilation = D3D11ShaderCompilation<byte[]>.Start(() => throw failure);
        int creates = 0;
        using var resource = new D3D11DeferredResource<byte[], Native>(new DeviceLiveness(), compilation.Join, bytes =>
        {
            creates++;
            return new Native(bytes);
        });
        Assert.Same(failure, Assert.Throws<ShaderValidationException>(() => resource.Value));
        Assert.Equal(0, creates);
    }

    sealed class Native(byte[] bytes) : IDisposable
    {
        internal byte[] Bytes { get; } = bytes;
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
