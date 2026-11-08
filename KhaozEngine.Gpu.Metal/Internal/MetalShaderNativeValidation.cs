namespace KhaozEngine.Gpu.Metal.Internal;

/// <summary>Device-free fault seams for native shader and pipeline object creation.</summary>
internal static class MetalShaderNativeValidation
{
    internal static void RequireClassAvailable(bool available, string className, bool shader = false)
    {
        if (available) return;
        string message = shader
            ? "The Objective-C runtime has no MTLCompileOptions class, which means the Metal framework did "
                + "not load. Nothing about this shader caused it."
            : $"The Objective-C runtime has no {className} class, which means the Metal framework did not "
                + "load. Nothing about this pipeline description caused it.";
        throw new ShaderValidationStateException(ShaderValidationRefusal.MetalNativeClassUnavailable, message)
        { NativeClass = className };
    }

    internal static void RequireDepthStateAvailable(bool available)
    {
        if (available) return;
        throw new ShaderValidationStateException(ShaderValidationRefusal.MetalDepthStateUnavailable,
            "The native Metal device would not create an MTLDepthStencilState. That call validates "
            + "nothing and takes no error out-parameter, so a nil is a device already in trouble "
            + "rather than a description this pipeline got wrong.")
        {
            NativeClass = "MTLDepthStencilState",
        };
    }

    internal static void RequireComputePipelineAvailable(bool available, string? diagnostic)
    {
        if (available) return;
        throw new ShaderValidationException(ShaderValidationRefusal.MetalComputePipelineRejected,
            "The native Metal device rejected this compute pipeline: "
            + (diagnostic is null
                ? "-newComputePipelineStateWithFunction:error: answered nil and wrote no NSError, which "
                    + "means the failure is not a compatibility one at all."
                : diagnostic)
            + " The function itself already compiled, so this is Metal refusing to build a pipeline out "
            + "of it rather than a syntax error in the emitted MSL.")
        {
            PipelineKind = "compute",
            Diagnostic = diagnostic,
            NativeDiagnosticMissing = diagnostic is null,
        };
    }
}
