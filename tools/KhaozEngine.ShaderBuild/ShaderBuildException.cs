using System;

namespace KhaozEngine.ShaderBuild;

/// <summary>Why the shader build refused, for callers that act on the reason.</summary>
public enum ShaderBuildFailure
{
    ToolchainMissing,
    ToolchainVersion,
    DigestMismatch,
    DownloadFailed,
    CompileFailed,
    ManifestInvalid,
    MetalIndexInStreamRange,
}

/// <summary>A shader build refusal carrying its reason and the identifiers a person needs to fix it.</summary>
public sealed class ShaderBuildException : Exception
{
    public ShaderBuildException(
        ShaderBuildFailure failure,
        string message,
        Uri? assetUrl = null,
        string? overrideVariable = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        AssetUrl = assetUrl;
        OverrideVariable = overrideVariable;
    }

    public ShaderBuildFailure Failure { get; }

    /// <summary>The pinned toolchain download the refusal concerns, when there is one.</summary>
    public Uri? AssetUrl { get; }

    /// <summary>The environment variable that supplies a local toolchain instead, when that is the way out.</summary>
    public string? OverrideVariable { get; }

    /// <summary>The manifest program the refusal concerns, when there is one.</summary>
    public string? ProgramId { get; init; }

    /// <summary>The shader parameter the refusal concerns, when there is one.</summary>
    public string? Parameter { get; init; }
}
