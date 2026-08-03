using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION S5's CI LEG: run the whole shipped shader path with NO DEVICE, so HLSL that SPIRV-Cross emits and
    /// FXC rejects, and a holed vertex input signature, both fail as a test rather than as a corrupted frame.
    ///
    /// <para>
    /// WHAT <see cref="ShaderValidation"/> CANNOT DO, and why this exists beside it. The seam's validator
    /// cross-compiles a pair to HLSL, MSL, GLSL and ESSL and stops there: it has never actually COMPILED the HLSL
    /// it produced. That is the gap both production incidents fell through, because SPIRV-Cross emitted the holed
    /// signature happily and only FXC and WARP had a problem with it. Closing the gap means calling FXC, and FXC
    /// is <c>d3dcompiler_47.dll</c>, so the leg is Windows-only by nature and cannot live in a package every 2D
    /// and 3D game references.
    /// </para>
    /// <para>
    /// IT SHARES THE SHIPPED PATH RATHER THAN MIRRORING IT, which is the whole reason it is here and not in
    /// <c>KhaozEngine.Gpu</c>. It calls the same <see cref="D3D11ShaderBuild"/> the resource factory calls, under
    /// the same pinned cross-compile options and the same FXC profile and flags. A validator with its own second
    /// FXC call site would drift from the shipped one, and would then be validating a shader nobody ships, which
    /// is worse than not validating at all because it reads as coverage.
    /// </para>
    /// <para>
    /// THE DISK CACHE IS DELIBERATELY OFF here. A cache hit means FXC never ran, and a validation leg that can
    /// pass without running the compiler it exists to run is not a validation leg.
    /// </para>
    /// </summary>
    internal static class D3D11ShaderValidation
    {
        /// <summary>Validate a GLSL 450 vertex and fragment pair through the real shader path.</summary>
        /// <exception cref="PlatformNotSupportedException">Called off Windows, where there is no FXC.</exception>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V, the pair failed to
        /// cross-compile, FXC rejected the emitted HLSL, or the vertex input signature is holed.</exception>
        internal static void ValidatePair(string vertexGlsl, string fragmentGlsl, string? label = null)
        {
            ArgumentNullException.ThrowIfNull(vertexGlsl);
            ArgumentNullException.ThrowIfNull(fragmentGlsl);
            if (!KhaozEngineD3D11.IsPlatformSupported) throw NotOnThisPlatform();

            _ = D3D11ShaderBuild.Pair(vertexGlsl, fragmentGlsl, D3D11ShaderDebug.Optimized, cache: null,
                label ?? "shader pair");
        }

        /// <summary>Validate a GLSL 450 compute source through the real shader path.</summary>
        /// <exception cref="PlatformNotSupportedException">Called off Windows, where there is no FXC.</exception>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, failed to
        /// cross-compile, declares no resolvable workgroup size, or FXC rejected the emitted HLSL.</exception>
        internal static void ValidateCompute(string computeGlsl, string? label = null)
        {
            ArgumentNullException.ThrowIfNull(computeGlsl);
            if (!KhaozEngineD3D11.IsPlatformSupported) throw NotOnThisPlatform();

            _ = D3D11ShaderBuild.Compute(computeGlsl, D3D11ShaderDebug.Optimized, cache: null,
                label ?? "compute shader");
        }

        // Not D3D11PlatformGuard's wording: that one is for a Windows-only OBJECT reached off Windows, which is
        // an internal invariant break. This is a caller asking for a compile on a machine that has no compiler,
        // which is an ordinary and answerable mistake, so it says what to do about it instead.
        static PlatformNotSupportedException NotOnThisPlatform()
            => new("Validating a shader through FXC needs d3dcompiler, which exists only on Windows. Gate the "
                + $"call on {nameof(KhaozEngineD3D11)}.{nameof(KhaozEngineD3D11.IsPlatformSupported)} and use "
                + "KhaozEngine.Gpu's ShaderValidation for the device-free, all-platform cross-compile check.");
    }
}
