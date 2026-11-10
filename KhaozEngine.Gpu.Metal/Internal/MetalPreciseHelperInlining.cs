using System;

namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Internal cost experiment applied after the pinned precise signed-zero rewrite.
/// Only helper attributes change. This is not arithmetic or image acceptance.</summary>
internal static class MetalPreciseHelperInlining
{
    internal static string Rewrite(string preciseMsl)
        => preciseMsl.Replace("[[clang::optnone]]", "[[clang::always_inline]]", StringComparison.Ordinal);
}
