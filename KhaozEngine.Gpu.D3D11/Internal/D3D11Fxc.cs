using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE ONE FXC CALL IN THE ENGINE, decision S1. HLSL in, DXBC out, plus the input-signature reflection
    /// decision S5 asserts against. Owning this call rather than letting Veldrid make it is what buys the disk
    /// cache (S4), the register numbering (S2) and a CPU-only FXC leg in CI (S5), and it is why there is exactly
    /// one place the compile flags are chosen.
    ///
    /// <para>
    /// DXC IS NOT AN OPTION HERE and it is not a preference. DXC emits DXIL, <c>CreateVertexShader</c> and its
    /// siblings consume DXBC, and there is no supported DXC path to DXBC. Shader Model 6.x is therefore
    /// unreachable from a Direct3D 11 backend, whatever anyone thinks of it. Section 8 of the design says so and
    /// asks that it not be relitigated.
    /// </para>
    /// <para>
    /// THE WINDOWS BOUNDARY, in the shape decision P1 requires. Every body here is <c>NoInlining</c> and no
    /// signature names a Vortice type, so a caller compiled on macOS or Linux resolves nothing from the interop
    /// assembly, and the load-path assertions in the suite stay green. The flags cross as a plain <c>uint</c>
    /// from <see cref="D3D11ShaderDebug"/> for exactly that reason, and the reflected signature crosses back as
    /// the engine's own <see cref="D3D11ShaderInputSemantic"/>.
    /// </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class D3D11Fxc
    {
        /// <summary>
        /// Compile <paramref name="hlsl"/> to DXBC for <paramref name="profile"/> under <paramref name="flags"/>.
        /// <paramref name="label"/> names the shader in any failure, because the seam's
        /// <c>CreateShadersFromSpirv</c> takes two GLSL strings and no name, so a raw FXC message would identify
        /// nothing out of roughly fifty sources.
        /// </summary>
        /// <exception cref="ShaderValidationException">FXC rejected the emitted HLSL. The message carries FXC's
        /// own diagnostic, which names a line of the EMITTED source rather than of the GLSL it came from.
        /// </exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static byte[] Compile(string hlsl, string profile, uint flags, string label)
        {
            ArgumentNullException.ThrowIfNull(hlsl);
            ArgumentNullException.ThrowIfNull(profile);

            Blob? code = null;
            Blob? errors = null;
            try
            {
                // The ANSI marshalling inside this overload passes the string's CHAR count as the byte count,
                // which is correct precisely while the source is ASCII. Everything SPIRV-Cross emits is, and a
                // non-ASCII byte would be mangled by the marshalling before it ever reached the length.
                // null defines and null include, which is what Vortice's own convenience overloads pass down for
                // "none". They are annotated non-nullable there, hence the suppressions: an empty ShaderMacro
                // array is NOT the same thing, since the marshalling would hand FXC a zero-length array rather
                // than the null pointer its contract asks for. Emitted HLSL is self-contained: SPIRV-Cross
                // resolves every include and every macro before it writes a line.
                SharpGen.Runtime.Result result = Compiler.Compile(
                    hlsl, null!, null!, D3D11ShaderProfile.EntryPoint, label, profile,
                    (ShaderFlags)flags, out code, out errors);

                if (result.Failure || code is null)
                {
                    string detail = errors is null ? "(FXC returned no diagnostic)" : errors.AsString();
                    throw new ShaderValidationException(
                        $"{label}: FXC rejected the cross-compiled HLSL for profile {profile} "
                        + $"(HRESULT 0x{result.Code:X8}). This is HLSL that SPIRV-Cross emitted, so the line "
                        + "numbers below are the EMITTED source, not the GLSL. FXC said: " + detail);
                }

                byte[] dxbc = code.AsBytes();
                if (dxbc.Length == 0)
                {
                    throw new ShaderValidationException(
                        $"{label}: FXC reported success for profile {profile} and produced no bytes. Nothing "
                        + "downstream can use an empty module, and a zero-length DXBC handed to the device fails "
                        + "somewhere far less informative than here.");
                }
                return dxbc;
            }
            finally
            {
                code?.Dispose();
                errors?.Dispose();
            }
        }

        /// <summary>
        /// The input signature of a compiled VERTEX module, in the order FXC reports it, as engine values. This
        /// is the ground truth decision S5 asserts against: it reflects the bytes the input layout is validated
        /// with, so a module that came out of the disk cache rather than out of a compiler in this process is
        /// read the same way.
        /// </summary>
        /// <exception cref="ShaderValidationException">The bytes are not a reflectable module.</exception>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static D3D11ShaderInputSemantic[] ReflectVertexInputs(ReadOnlySpan<byte> dxbc, string label)
        {
            if (dxbc.IsEmpty)
            {
                throw new ShaderValidationException(
                    $"{label}: cannot reflect an empty vertex module. The input signature is what the contiguity "
                    + "check reads, so there is nothing to check and nothing to build an input layout against.");
            }

            ID3D11ShaderReflection? reflection = null;
            try
            {
                reflection = Compiler.Reflect<ID3D11ShaderReflection>(dxbc);
                ShaderParameterDescription[] parameters = reflection.InputParameters;
                var semantics = new D3D11ShaderInputSemantic[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    semantics[i] = new D3D11ShaderInputSemantic(
                        parameters[i].SemanticName ?? string.Empty, (uint)parameters[i].SemanticIndex);
                }
                return semantics;
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                throw new ShaderValidationException(
                    $"{label}: the compiled vertex module could not be reflected, so its input signature cannot "
                    + $"be checked for the holed-TEXCOORD hazard: {ex.Message}", ex);
            }
            finally
            {
                reflection?.Dispose();
            }
        }
    }
}
