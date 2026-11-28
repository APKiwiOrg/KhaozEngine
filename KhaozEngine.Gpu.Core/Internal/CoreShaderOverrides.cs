using System;
using System.IO;

namespace KhaozEngine.Gpu.Core.Internal;

/// <summary>Substitutes precompiled SPIR-V for a GLSL shader pair, for the Slang trial.</summary>
internal static class CoreShaderOverrides
{
    /// <summary>Given the vertex and fragment GLSL, returns replacement SPIR-V or null to compile the GLSL.</summary>
    internal static Func<string, string, (byte[] Vertex, byte[] Fragment)?>? Override { get; set; }

    /// <summary>The sprite pair compiled from native/shaders-slang/sprite.slang.</summary>
    internal static (byte[] Vertex, byte[] Fragment) LoadSlangSprite()
        => (ReadResource("SlangTrial/sprite.vert.spv"), ReadResource("SlangTrial/sprite.frag.spv"));

    private static byte[] ReadResource(string name)
    {
        using Stream stream = typeof(CoreShaderOverrides).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
