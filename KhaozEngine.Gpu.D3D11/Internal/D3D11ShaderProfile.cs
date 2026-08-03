using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>Which stage a shader is compiled for. The engine's own three, not Direct3D's full set: the seam
    /// exposes a vertex and fragment PAIR plus a compute kernel and nothing else.</summary>
    internal enum D3D11ShaderStage
    {
        /// <summary>The vertex stage of a graphics program.</summary>
        Vertex,
        /// <summary>The fragment stage of a graphics program. Direct3D calls it the pixel shader.</summary>
        Fragment,
        /// <summary>A compute kernel.</summary>
        Compute,
    }

    /// <summary>
    /// The FXC target profile per stage, decision S1. Shader Model 5.0 across the board, which is the last model
    /// FXC emits and the highest DXBC a Direct3D 11 device consumes.
    /// <para>
    /// THE VERSION IS NOT A KNOB, and the reason is the same fact that eliminated DXC from this program: DXC emits
    /// DXIL, <c>CreateVertexShader</c> and its siblings consume DXBC, and there is no supported DXC path to DXBC.
    /// So Shader Model 6.x is unreachable from a Direct3D 11 backend regardless of anyone's view of it, and 5.0 is
    /// not a conservative choice but the only one. Section 8 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c> says so and says not to relitigate it.
    /// </para>
    /// <para>
    /// Pure, device-free and free of any Direct3D type, so it is a plain string table the cache key can be built
    /// from on any operating system.
    /// </para>
    /// </summary>
    internal static class D3D11ShaderProfile
    {
        /// <summary>The vertex profile.</summary>
        internal const string Vertex = "vs_5_0";

        /// <summary>The pixel (fragment) profile.</summary>
        internal const string Fragment = "ps_5_0";

        /// <summary>The compute profile.</summary>
        internal const string Compute = "cs_5_0";

        /// <summary>The entry point every emitted module declares. SPIRV-Cross names the entry point <c>main</c>
        /// on the HLSL side too, matching the GLSL convention the whole seam uses.</summary>
        internal const string EntryPoint = "main";

        /// <summary>The profile for <paramref name="stage"/>.</summary>
        internal static string For(D3D11ShaderStage stage) => stage switch
        {
            D3D11ShaderStage.Vertex => Vertex,
            D3D11ShaderStage.Fragment => Fragment,
            D3D11ShaderStage.Compute => Compute,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "Every shader stage the seam exposes has an FXC profile. An unmapped one would compile against "
                + "whatever profile string happened to be last, which fails at FXC rather than silently, but "
                + "names the wrong thing when it does."),
        };
    }
}
