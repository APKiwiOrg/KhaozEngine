using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11CompilationReadinessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Explicit_compilation_join_preserves_failure_and_leaves_native_realization_for_the_consumer(bool compute, bool fail)
    {
        var compilationFailure = new ShaderValidationException("Synthetic CPU compiler refusal.");
        var nativeBoundary = new InvalidOperationException("Synthetic native realization boundary.");
        if (compute)
        {
            var compilation = D3D11ShaderCompilation<D3D11PreparedCompute>.Start(() =>
                fail ? throw compilationFailure : new(new(new byte[] { 1 }, 1, 1, 1), null));
            using var shader = new D3D11ComputeShader(new DeviceLiveness(), compilation, _ => throw nativeBoundary);
            Verify(((IGpuComputeShader)shader).EnsureCompilationReady, shader.EnsureReady);
        }
        else
        {
            var compilation = D3D11ShaderCompilation<D3D11PreparedGraphics>.Start(() =>
                fail ? throw compilationFailure : new(new(new byte[] { 1 }, new byte[] { 2 }), null, null));
            using var shader = new D3D11ShaderSet(new DeviceLiveness(), compilation, _ => throw nativeBoundary);
            Verify(((IGpuShaderSet)shader).EnsureCompilationReady, shader.EnsureReady);
        }

        void Verify(Action join, Action consume)
        {
            if (fail) Assert.Same(compilationFailure, Assert.Throws<ShaderValidationException>(join));
            else
            {
                join();
                // Calling the consuming boundary still reaches native realization. A join that did so
                // early would already have thrown the same sentinel above.
                Assert.Same(nativeBoundary, Assert.Throws<InvalidOperationException>(consume));
            }
        }
    }
}
