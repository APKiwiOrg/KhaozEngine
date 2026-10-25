using System;
using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Gpu.Internal;

/// <summary>Canonical length-prefixed little-endian encodings. No native handle enters these identities.</summary>
internal static class GpuDiagnosticIdentityEncoding
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static GpuDiagnosticDigest Bytes(ReadOnlySpan<byte> bytes, string source)
        => bytes.IsEmpty ? new(null, source, GpuDiagnosticStatus.Invalid, "The compiled module is empty.")
            : new(Convert.ToHexStringLower(SHA256.HashData(bytes)), source, GpuDiagnosticStatus.Available, null);

    internal static GpuDiagnosticDigest Text(string text, string source) => Bytes(Utf8.GetBytes(text), source);

    internal static GpuDiagnosticDigest Known(string? hash, string source)
        => ValidHash(hash) ? new(hash, source, GpuDiagnosticStatus.Available, null)
            : new(null, source, GpuDiagnosticStatus.Invalid, "Rejected malformed digest from " + source + ".");

    internal static bool ValidHash(string? hash)
    {
        if (hash is null || hash.Length != 64) return false;
        foreach (char c in hash) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    internal static GpuDiagnosticDigest Stages(string field, ImmutableArray<GpuPipelineStageIdentity> stages)
    {
        if (field is not ("source" or "artifact")) throw new ArgumentException("Unknown stage field.", nameof(field));
        string domain = "khaozengine.pipeline-stage-set/1";
        if (stages.IsDefaultOrEmpty) return new(null, domain, GpuDiagnosticStatus.Invalid, "No shader stages were retained.");
        foreach (var stage in stages)
        {
            var digest = field == "source" ? stage.SourceSha256 : stage.ShaderArtifactSha256;
            if (digest.Status != GpuDiagnosticStatus.Available)
                return stages.Length == 1 ? digest : new(null, domain, digest.Status, digest.Reason);
            if (!ValidHash(digest.Sha256) || string.IsNullOrWhiteSpace(digest.Source) || digest.Reason is not null)
                return new(null, domain, GpuDiagnosticStatus.Invalid, "A stage digest is malformed.");
        }
        if (stages.Length == 1) return field == "source" ? stages[0].SourceSha256 : stages[0].ShaderArtifactSha256;
        return Encode(domain, w =>
        {
            String(w, field); w.Write(checked((uint)stages.Length));
            foreach (var stage in stages)
            {
                String(w, stage.Stage); String(w, stage.EntryPoint);
                w.Write(Convert.FromHexString((field == "source" ? stage.SourceSha256 : stage.ShaderArtifactSha256).Sha256!));
            }
        });
    }

    internal static GpuDiagnosticDigest CodeGeneration(GpuBackendKind backend, ImmutableArray<GpuDiagnosticOption> options)
        => Encode("khaozengine.pipeline-code-generation/1", w => { Backend(w, backend); Options(w, options); });

    internal static GpuDiagnosticDigest Configuration(GpuEffectiveDiagnosticConfiguration c)
        => Encode("khaozengine.pipeline-diagnostic-configuration/1", w =>
        {
            Backend(w, c.Backend); String(w, c.AdapterKey); String(w, c.Driver);
            w.Write(c.Requested.CompilerMetadata); w.Write(c.Requested.RuntimeInvocations);
            w.Write(c.CompilerMetadataEnabled); w.Write(c.RuntimeInvocationsEnabled); w.Write(c.DebugLayerEnabled);
            w.Write(c.ValidationEnabled); w.Write(c.PipelineStatisticsSupported); w.Write(c.PipelineExecutableFeatureEnabled);
            w.Write(c.PipelineCaptureFlags); Options(w, c.CodeGenerationOptions); Options(w, c.ObservationOptions);
        });

    static void Backend(BinaryWriter w, GpuBackendKind backend)
    {
        if (!Enum.IsDefined(backend)) throw new ArgumentException("Unknown backend.", nameof(backend));
        w.Write((int)backend);
    }

    static void Options(BinaryWriter w, ImmutableArray<GpuDiagnosticOption> options)
    {
        if (options.IsDefault || options.Length > 64) throw new ArgumentException("Option table exceeds 64 entries.");
        w.Write(checked((uint)options.Length));
        string? previous = null;
        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Key) || Utf8.GetByteCount(option.Key) > 256
                || Utf8.GetByteCount(option.Value) > 4096 || previous is not null && string.CompareOrdinal(previous, option.Key) >= 0)
                throw new ArgumentException("Options require bounded unique ordinal ordered keys and values.");
            String(w, option.Key); String(w, option.Value); previous = option.Key;
        }
    }

    static void String(BinaryWriter w, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        w.Write(checked((uint)bytes.Length)); w.Write(bytes);
    }

    static GpuDiagnosticDigest Encode(string domain, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Utf8, leaveOpen: true)) { String(writer, domain); write(writer); }
        return Bytes(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)), domain);
    }
}
