using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfSidecarKind { CompilerInventory, CompilerFailure, NonCompiler }
internal sealed record RendererPerfSidecarCounts(int FactoryAttempts, int Occurrences, int Recipes, int Reports, int Executables, int Statistics)
{
    internal static readonly RendererPerfSidecarCounts Empty = new(0, 0, 0, 0, 0, 0);
}
internal sealed record RendererPerfExpectedSidecar(RendererPerfSidecarKind Kind, string Schema,
    string Path, long ByteLength, string Sha256, RendererPerfSidecarCounts Counts);

// Registration belongs to producers. Files found in a directory never become expected evidence.
internal sealed class RendererPerfSidecarRegistry
{
    readonly List<RendererPerfExpectedSidecar> _files = [];
    readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    ImmutableArray<RendererPerfExpectedSidecar> _frozen;
    internal void Register(RendererPerfExpectedSidecar sidecar)
    {
        if (!_frozen.IsDefault) throw new InvalidOperationException("Sidecar registration is frozen.");
        Validate(sidecar);
        if (!_paths.Add(sidecar.Path)) throw new InvalidOperationException("Duplicate or aliased sidecar path.");
        _files.Add(sidecar);
    }
    internal ImmutableArray<RendererPerfExpectedSidecar> Freeze()
        => _frozen.IsDefault ? _frozen = [.. _files] : _frozen;
    internal static void Validate(RendererPerfExpectedSidecar sidecar)
    {
        Path(sidecar.Path);
        if (!Enum.IsDefined(sidecar.Kind) || string.IsNullOrWhiteSpace(sidecar.Schema) || sidecar.ByteLength <= 0
            || sidecar.ByteLength > RendererPerfReportJson.MaximumBytes || !GpuDiagnosticIdentityEncoding.ValidHash(sidecar.Sha256)
            || sidecar.Counts.FactoryAttempts < 0 || sidecar.Counts.Occurrences < 0 || sidecar.Counts.Recipes < 0
            || sidecar.Counts.Reports < 0 || sidecar.Counts.Executables < 0 || sidecar.Counts.Statistics < 0)
            throw new ArgumentException("Invalid expected sidecar metadata.");
        bool inventory = sidecar.Schema == RendererPerfCompilerJson.Schema;
        bool failure = sidecar.Schema == "khaozengine.renderer-unit-failure/1";
        if (inventory != (sidecar.Kind == RendererPerfSidecarKind.CompilerInventory)
            || failure != (sidecar.Kind == RendererPerfSidecarKind.CompilerFailure))
            throw new ArgumentException("Compiler evidence kind and schema disagree.");
    }
    internal static void Path(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || System.IO.Path.IsPathRooted(path)) throw new ArgumentException("Expected a safe relative file path.");
        foreach (string part in path.Split('/'))
        {
            if (part.Length > 255 || part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) throw new ArgumentException("Unsafe relative path component.");
            foreach (char c in part)
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_'))
                    throw new ArgumentException("Unsafe relative path character.");
        }
    }
}
