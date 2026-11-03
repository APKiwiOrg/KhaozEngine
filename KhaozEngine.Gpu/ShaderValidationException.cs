using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu;

/// <summary>A structured ShaderValidation refusal.</summary>
public sealed class ShaderValidationException : Exception
{
    /// <summary>Creates a consumer exception with the given message.</summary>
    public ShaderValidationException(string message) : base(message) { }

    /// <summary>Creates a consumer exception with an underlying compile failure.</summary>
    public ShaderValidationException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Creates a shader refusal with its reason.</summary>
    public ShaderValidationException(ShaderValidationRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a shader refusal retaining its underlying failure.</summary>
    public ShaderValidationException(ShaderValidationRefusal reason, string message, Exception inner)
        : base(message, inner) => Reason = reason;

    /// <summary>The refusal reason, null only for a consumer instance built with a message-only constructor.</summary>
    public ShaderValidationRefusal? Reason { get; }

    /// <summary>The caller's shader or pipeline label, when available.</summary>
    public string? Label { get; init; }

    /// <summary>The SPIR-V execution mode requiring specialization, when applicable.</summary>
    public string? ExecutionMode { get; init; }

    /// <summary>The refused shader stage, or null when no single stage is known.</summary>
    public GpuShaderStages? Stage { get; init; }

    /// <summary>The compile target, such as SPIR-V, HLSL or MSL.</summary>
    public string? Target { get; init; }

    /// <summary>The failed native cross-compiler operation.</summary>
    public string? Operation { get; init; }

    /// <summary>The native SPIRV-Cross result code, when available.</summary>
    public int? ResultCode { get; init; }

    /// <summary>The native compiler or driver diagnostic, when available.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>The FXC shader profile.</summary>
    public string? Profile { get; init; }

    /// <summary>The user-input semantic whose sequence was refused.</summary>
    public string? Semantic { get; init; }

    /// <summary>The first missing user-semantic index, or null for a repeated-index refusal.</summary>
    public int? FirstMissingIndex { get; init; }

    /// <summary>A read-only snapshot of source paths illustrating shader repairs.</summary>
    public IReadOnlyList<string>? RepairSourcePaths { get; init; }

    /// <summary>The set identifying this refusal, when applicable.</summary>
    public int? Set { get; init; }

    /// <summary>The binding identifying this refusal, when applicable.</summary>
    public int? Binding { get; init; }

    /// <summary>The element count identifying this refusal, when applicable.</summary>
    public int? ElementCount { get; init; }

    /// <summary>The space identifying this refusal, when applicable.</summary>
    public string? Space { get; init; }

    /// <summary>The argument index identifying this refusal, when applicable.</summary>
    public int? ArgumentIndex { get; init; }

    /// <summary>The declared set identifying this refusal, when applicable.</summary>
    public uint? DeclaredSet { get; init; }

    /// <summary>The declared binding identifying this refusal, when applicable.</summary>
    public uint? DeclaredBinding { get; init; }

    /// <summary>The emitted set identifying this refusal, when applicable.</summary>
    public uint? EmittedSet { get; init; }

    /// <summary>The emitted binding identifying this refusal, when applicable.</summary>
    public uint? EmittedBinding { get; init; }

    /// <summary>The expected index identifying this refusal, when applicable.</summary>
    public int? ExpectedIndex { get; init; }

    /// <summary>The slot identifying this refusal, when applicable.</summary>
    public int? Slot { get; init; }

    /// <summary>The declared kind identifying this refusal, when applicable.</summary>
    public GpuResourceKind? DeclaredKind { get; init; }

    /// <summary>The declared buffer kind in the MSL fallback comparison.</summary>
    public string? DeclaredBufferKind { get; init; }

    /// <summary>The emitted kind identifying this refusal, when applicable.</summary>
    public string? EmittedKind { get; init; }

    /// <summary>The pipeline kind identifying this refusal, when applicable.</summary>
    public string? PipelineKind { get; init; }

    /// <summary>Whether the native shader or pipeline call returned no NSError.</summary>
    public bool? NativeDiagnosticMissing { get; init; }

    /// <summary>The entry point keyword identifying this refusal, when applicable.</summary>
    public string? EntryPointKeyword { get; init; }

    /// <summary>The helper kind identifying this refusal, when applicable.</summary>
    public string? HelperKind { get; init; }

    /// <summary>The policy identifying this refusal, when applicable.</summary>
    public string? Policy { get; init; }

    /// <summary>The declared layout count identifying this refusal, when applicable.</summary>
    public int? DeclaredLayoutCount { get; init; }

    /// <summary>The reflected layout count identifying this refusal, when applicable.</summary>
    public int? ReflectedLayoutCount { get; init; }

    /// <summary>The declared element count identifying this refusal, when applicable.</summary>
    public int? DeclaredElementCount { get; init; }

    /// <summary>The reflected element count identifying this refusal, when applicable.</summary>
    public int? ReflectedElementCount { get; init; }

    /// <summary>The reflected kind identifying this refusal, when applicable.</summary>
    public GpuResourceKind? ReflectedKind { get; init; }

    /// <summary>The entry point identifying this refusal, when applicable.</summary>
    public string? EntryPoint { get; init; }

    /// <summary>The source origin identifying this refusal, when applicable.</summary>
    public string? SourceOrigin { get; init; }

    /// <summary>The stage count identifying this refusal, when applicable.</summary>
    public int? StageCount { get; init; }

    /// <summary>The stages identifying this refusal, when applicable.</summary>
    public GpuShaderStages? Stages { get; init; }

    /// <summary>The layout count identifying this refusal, when applicable.</summary>
    public int? LayoutCount { get; init; }

    /// <summary>The stream count identifying this refusal, when applicable.</summary>
    public int? StreamCount { get; init; }

    /// <summary>The lowest stream index identifying this refusal, when applicable.</summary>
    public int? LowestStreamIndex { get; init; }

    /// <summary>The buffer table size identifying this refusal, when applicable.</summary>
    public int? BufferTableSize { get; init; }

    /// <summary>The numeric native reflected resource kind.</summary>
    public int? NativeResourceKind { get; init; }

    /// <summary>The source declaration identifying this refusal, when applicable.</summary>
    public string? SourceDeclaration { get; init; }

    /// <summary>The numeric native vertex-input base type.</summary>
    public int? NativeBaseType { get; init; }

    /// <summary>The component count identifying this refusal, when applicable.</summary>
    public uint? ComponentCount { get; init; }

    /// <summary>The zero-based SPIR-V word offset of the malformed instruction.</summary>
    public int? WordOffset { get; init; }

    /// <summary>The word count encoded in the malformed instruction.</summary>
    public int? WordCount { get; init; }

    /// <summary>The module word count bounding the malformed instruction.</summary>
    public int? ModuleWordCount { get; init; }

    /// <summary>The rejected module byte length.</summary>
    public int? ByteLength { get; init; }

    /// <summary>The minimum module header size required by this parser, when applicable.</summary>
    public int? MinimumHeaderBytes { get; init; }

    /// <summary>The required byte width of a SPIR-V word.</summary>
    public int? WordBytes { get; init; }

    /// <summary>The actual magic identifying this refusal, when applicable.</summary>
    public uint? ActualMagic { get; init; }

    /// <summary>The expected magic identifying this refusal, when applicable.</summary>
    public uint? ExpectedMagic { get; init; }

    /// <summary>The HRESULT, populated from FXC for D3D11FxcRejected and otherwise retaining Exception's value.</summary>
    public new int HResult { get => base.HResult; init => base.HResult = value; }
}
