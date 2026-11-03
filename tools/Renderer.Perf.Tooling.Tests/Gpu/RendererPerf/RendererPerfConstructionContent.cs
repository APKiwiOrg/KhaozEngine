using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// IDs belong to factory references. Content hashes own the retained values and their byte charges.
internal sealed class RendererPerfConstructionContent(RendererPerfConstructionAdmission admission)
{
    sealed record Descriptor(byte[] Bytes, object Value);
    readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    readonly Dictionary<string, Descriptor> _descriptors = new(StringComparer.Ordinal);

    internal static string SourceHash(string source)
        => Convert.ToHexStringLower(SHA256.HashData(RendererPerfConstructionEncoding.Utf8.GetBytes(source)));

    internal string Source(string source)
    {
        long bytes = RendererPerfConstructionAdmission.Text(source, 1048576);
        string hash = SourceHash(source);
        if (_sources.TryGetValue(hash, out string? existing))
        {
            if (!string.Equals(existing, source, StringComparison.Ordinal)) Collision();
            return existing;
        }
        admission.Reserve(bytes, RendererPerfConstructionAdmission.Text(hash, 64), 0);
        _sources.Add(hash, source);
        return source;
    }

    internal RendererPerfShaderRecipe Shader(RendererPerfShaderRecipe shader)
        => Intern(shader, w =>
        {
            Text(w, "shader/1"); w.Write(shader.Stages.Length);
            foreach (var s in shader.Stages)
            {
                Text(w, s.Stage); Text(w, s.EntryPoint); Text(w, SourceHash(s.Source));
                w.Write(s.LocalX); w.Write(s.LocalY); w.Write(s.LocalZ);
            }
        });

    internal RendererPerfLayoutRecipe Layout(RendererPerfLayoutRecipe layout)
        => Intern(layout, w =>
        {
            Text(w, "layout/1"); w.Write(layout.Elements.Length);
            foreach (var e in layout.Elements)
            { Text(w, e.Name); w.Write((int)e.Kind); w.Write((int)e.Stages); w.Write(e.Dynamic); }
        });

    internal RendererPerfPipelineRecipe Pipeline(RendererPerfPipelineRecipe recipe,
        IReadOnlyList<RendererPerfShaderRecipe?> shaders, IReadOnlyList<RendererPerfLayoutRecipe?> layouts)
        => Intern(recipe, w =>
        {
            Text(w, "pipeline/1"); w.Write((int)recipe.Kind);
            // Interned graph values have stable first-input IDs within this capture or reader.
            w.Write(shaders[recipe.ShaderId]!.Id); w.Write(recipe.LayoutIds.Length);
            foreach (int id in recipe.LayoutIds) w.Write(layouts[id]!.Id);
            w.Write(recipe.Graphics is not null);
            if (recipe.Graphics is { } graphics) RendererPerfConstructionEncoding.Graphics(w, graphics);
            RendererPerfConstructionEncoding.DigestValue(w, recipe.CodeGenerationSha256);
        });

    T Intern<T>(T value, Action<BinaryWriter> encode) where T : class
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, RendererPerfConstructionEncoding.Utf8, true)) encode(writer);
        var bytes = stream.GetBuffer().AsSpan(0, checked((int)stream.Length));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (_descriptors.TryGetValue(hash, out var existing))
        {
            if (!bytes.SequenceEqual(existing.Bytes) || existing.Value is not T) Collision();
            return (T)existing.Value;
        }
        admission.Reserve(0, bytes.Length, 0);
        _descriptors.Add(hash, new(bytes.ToArray(), value));
        return value;
    }
    static void Text(BinaryWriter writer, string text) => RendererPerfConstructionEncoding.Text(writer, text);
    static void Collision() => throw new RendererPerfConstructionRefusalException("Construction content digest collision has unequal values.");
}
