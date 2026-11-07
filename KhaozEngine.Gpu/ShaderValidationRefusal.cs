namespace KhaozEngine.Gpu;

/// <summary>Why ShaderValidation refused an operation.</summary>
public enum ShaderValidationRefusal
{
    /// <summary>Refuses a compute module without a literal local-size execution mode.</summary>
    ComputeLocalSizeMissing,

    /// <summary>Refuses a compute local size requiring specialization constant evaluation.</summary>
    ComputeLocalSizeSpecialized,

    /// <summary>Refuses a shader translation after a structured cross-compiler failure is caught.</summary>
    CrossCompileFailed,

    /// <summary>Refuses a SPIRV-Cross context that cannot be created.</summary>
    CrossContextCreationFailed,

    /// <summary>Refuses a failed SPIRV-Cross native operation.</summary>
    CrossNativeCallFailed,

    /// <summary>Refuses successful FXC compilation that produced no bytecode.</summary>
    D3D11FxcOutputEmpty,

    /// <summary>Refuses emitted HLSL rejected by FXC.</summary>
    D3D11FxcRejected,

    /// <summary>Refuses a holed user-semantic sequence that the native compiler can miscompile.</summary>
    D3D11UserSemanticHole,

    /// <summary>Refuses reflection of an empty compiled vertex module.</summary>
    D3D11VertexModuleEmpty,

    /// <summary>Refuses a vertex module whose compiled signature cannot be reflected.</summary>
    D3D11VertexReflectionFailed,

    /// <summary>Refuses GLSL source that fails compilation to SPIR-V.</summary>
    FrontEndCompileFailed,

    /// <summary>Refuses a front-end compiler initialization that returns null.</summary>
    FrontEndCompilerInitializationFailed,

    /// <summary>Refuses a failed native front-end compile result.</summary>
    FrontEndNativeCompileFailed,

    /// <summary>Refuses front-end compile options initialization that returns null.</summary>
    FrontEndOptionsInitializationFailed,

    /// <summary>Refuses front-end compilation that returns no native result.</summary>
    FrontEndResultMissing,

    /// <summary>Refuses an emitted resource use naming an undeclared binding.</summary>
    MetalBindingIndexInvalid,

    /// <summary>Refuses a Metal resource argument whose index disagrees with authored binding order.</summary>
    MetalBindingOrderMismatch,

    /// <summary>Refuses emitted Metal buffer kinds that disagree with reflected binding order.</summary>
    MetalBufferKindOrderMismatch,

    /// <summary>Refuses a cached resource use whose stage has no source payload.</summary>
    MetalCachedStageMissing,

    /// <summary>Refuses a compute pipeline rejected by the native Metal device.</summary>
    MetalComputePipelineRejected,

    /// <summary>Refuses a nil native depth-stencil state during pipeline construction.</summary>
    MetalDepthStateUnavailable,

    /// <summary>Refuses an emitted Metal entry point without an argument list.</summary>
    MetalEntryArgumentsMissing,

    /// <summary>Refuses an emitted Metal entry point with an unclosed argument list.</summary>
    MetalEntryArgumentsUnclosed,

    /// <summary>Refuses an emitted Metal entry point whose function name cannot be read.</summary>
    MetalEntryNameUnreadable,

    /// <summary>Refuses emitted MSL with no entry point for the requested stage.</summary>
    MetalEntryPointMissing,

    /// <summary>Refuses a graphics pipeline rejected by the native Metal device.</summary>
    MetalGraphicsPipelineRejected,

    /// <summary>Refuses a Metal helper buffer or memory argument outside the authored index scheme.</summary>
    MetalHelperBufferUnnumbered,

    /// <summary>Refuses a pipeline layout array whose length differs from shader reflection.</summary>
    MetalLayoutCountMismatch,

    /// <summary>Refuses a resource layout whose element count differs from shader reflection.</summary>
    MetalLayoutElementCountMismatch,

    /// <summary>Refuses a resource layout whose element kind differs from shader reflection.</summary>
    MetalLayoutKindMismatch,

    /// <summary>Refuses a compiled Metal library without the parsed entry-point function.</summary>
    MetalLibraryFunctionMissing,

    /// <summary>Refuses emitted MSL rejected by the native Metal library compiler.</summary>
    MetalLibraryRejected,

    /// <summary>Refuses shader or pipeline descriptor creation when the Objective-C class is unavailable.</summary>
    MetalNativeClassUnavailable,

    /// <summary>Refuses a Metal program stage that the program does not contain.</summary>
    MetalProgramStageMissing,

    /// <summary>Refuses a Metal program that is neither a compute stage nor a vertex and fragment pair.</summary>
    MetalProgramStageShapeInvalid,

    /// <summary>Refuses a repeated resource use in one Metal stage.</summary>
    MetalResourceUseDuplicate,

    /// <summary>Refuses a cross-compiled Metal stage with no resource-use list.</summary>
    MetalResourceUseListMissing,

    /// <summary>Refuses an emitted resource use naming an undeclared set.</summary>
    MetalSetIndexInvalid,

    /// <summary>Refuses a reflected vertex resource buffer colliding with vertex stream indices.</summary>
    MetalVertexBufferCollision,

    /// <summary>Refuses a vertex stream count outside the Metal argument table capacity.</summary>
    MetalVertexStreamCountInvalid,

    /// <summary>Refuses a reflected resource kind absent from the GPU contract mirror.</summary>
    ReflectionResourceKindUnsupported,

    /// <summary>Refuses a reflected vertex input format absent from the GPU contract mirror.</summary>
    ReflectionVertexFormatUnsupported,

    /// <summary>Refuses a truncated or non-advancing SPIR-V instruction.</summary>
    SpirvInstructionMalformed,

    /// <summary>Refuses bytes that cannot form the required SPIR-V word or module layout.</summary>
    SpirvLengthInvalid,

    /// <summary>Refuses a SPIR-V module with the wrong magic number.</summary>
    SpirvMagicInvalid,
}
